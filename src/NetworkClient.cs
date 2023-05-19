/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;
using Parlo.Packets;
using System.Linq;
using Parlo.Collections;
using System.Xml.Schema;
using Parlo.Exceptions;

namespace Parlo
{
    /// <summary>
    /// Occurs when a network error happened.
    /// </summary>
    /// <param name="Exception">The SocketException that was thrown.</param>
	public delegate void NetworkErrorDelegate(SocketException Exception);

    /// <summary>
    /// Occurs when a packet was received.
    /// </summary>
    /// <param name="Sender">The NetworkClient instance that sent or received the packet.</param>
    /// <param name="P">The Packet that was received.</param>
	public delegate void ReceivedPacketDelegate(NetworkClient Sender, Packet P);

    /// <summary>
    /// Occurs when a client connected to a server.
    /// </summary>
    /// <param name="LoginArgs">The arguments that were used to establish the connection.</param>
	public delegate void OnConnectedDelegate(LoginArgsContainer LoginArgs);

    /// <summary>
    /// Occurs when a client received a heartbeat. Used for testing purposes,
    /// but can be used by consumers of the <see cref="NetworkClient"/> class
    /// to inform about heartbeats.
    /// </summary>
    /// <param name="Client">The <see cref="NetworkClient"/> that received a Heartbeat.</param>
    public delegate void ReceivedHeartbeatDelegate(NetworkClient Client);

    /// <summary>
    /// Occurs when a client a client disconnected.
    /// </summary>
    /// <param name="Sender">The NetworkClient instance that disconnected.</param>
    public delegate void ClientDisconnectedDelegate(NetworkClient Sender);

    /// <summary>
    /// Occurs when a server sent a packet saying it's about to disconnect.
    /// </summary>
    /// <param name="Sender">The NetworkClient instance used to connect to the server.</param>
	public delegate void ServerDisconnectedDelegate(NetworkClient Sender);

    /// <summary>
    /// Occurs when a client missed too many heartbeats.
    /// </summary>
    /// <param name="Sender">The client that lost too many heartbeats.</param>
    public delegate void OnConnectionLostDelegate(NetworkClient Sender);

    /// <summary>
    /// Represents a client connected to a server.
    /// </summary>
    public class NetworkClient : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// This client's SessionID. Ensures that a client is unique even if 
        /// multiple clients are trying to connect using the same IP.
        /// </summary>
        public Guid SessionId { get; } = Guid.NewGuid();

        private byte[] m_RecvBuf;

        private DateTime m_LastHeartbeatSent;
        private bool m_IsAlive = true;
        private readonly SemaphoreSlim m_IsAliveLock;

        private readonly SemaphoreSlim m_MissedHeartbeatsLock;
        private int m_MissedHeartbeats = 0;

        private int m_MaxMissedHeartbeats = 6;
        private int m_HeartbeatInterval = 30; //In seconds.

        private CancellationTokenSource m_SendHeartbeatCTS = new CancellationTokenSource();
        private CancellationTokenSource m_CheckMissedHeartbeatsCTS = new CancellationTokenSource();

        private Listener m_Listener;
        private ISocket m_Sock;
        private string m_IP;
        private int m_Port;

        /// <summary>
        /// Gets the max packet size for TCP packets.
        /// Defaults to 1024 bytes.
        /// </summary>
        public int MaxTCPPacketSize
        {
            get { return ProcessingBuffer.MAX_PACKET_SIZE; }
        }

        private readonly CancellationTokenSource m_ReceiveCancellationTokenSource = new CancellationTokenSource();

        private readonly SemaphoreSlim m_ConnectedLock;
        private bool m_Connected = false;

        //The last Round Trip Time (RTT) in milliseconds.
        private int m_LastRTT = 0;

        /// <summary>
        /// The Maximum Transmission Unit (MTU) for the NetworkClient.
        /// This value assumes an Ethernet-based network with a 1500-byte MTU.
        /// </summary>
        private const int MTU = 1500;

        /// <summary>
        /// Gets the maximum payload size for a UDP packet, assuming IPv4.
        /// </summary>
        public const int MaxIPv4UDPPayloadSize = MTU - 20 - 8;

        /// <summary>
        /// Gets the maximum payload size for a UDP packet, assuming IPv6.
        /// </summary>
        public const int MaxIPv6UDPPayloadSize = MTU - 40 - 8;

        private CancellationTokenSource m_ResendUnackedCTS = new CancellationTokenSource();
        private CancellationTokenSource m_ReceiveFromCTS = new CancellationTokenSource();

        private int m_UDPTimeout = 5;

        /// <summary>
        /// Gets or sets the time in seconds before a UDP packet is considered lost.
        /// Defaults to 5.
        /// </summary>
        public int UDPTimeout
        {
            get { return m_UDPTimeout; }
            set { m_UDPTimeout = value; }
        }

        private int m_MaxResends = 5;

        /// <summary>
        /// Gets or sets the maximum number of times a packet can be resent over UDP
        /// before it is considered lost. Defaults to 5. If a packet is not acknowledged
        /// within this number of resends, the connection is considered lost.
        /// </summary>
        public int MaxResends
        {
            get { return m_MaxResends; }
            set { m_MaxResends = value; }
        }

        private int m_MaxDatagramSize = 65535;

        /// <summary>
        /// Gets or sets the absolute upper limit for the size of a datagram.
        /// This is NOT the same as <see cref="MaxIPv4UDPPayloadSize"/> or 
        /// <see cref="MaxIPv6UDPPayloadSize"/>. Any packet larger than this
        /// limit will not be sent, even as fragments.
        /// This value must NOT be larger than 65535, as that is the maximum
        /// hardcoded limit for a datagram in the UDP protocol.
        /// </summary>
        public int MaxDatagramSize
        {
            get
            {
                return m_MaxDatagramSize;
            }
            set
            {
                if(value > 65535)
                    throw new ArgumentOutOfRangeException(nameof(value), "The maximum datagram size cannot be larger than 65535 bytes.");

                m_MaxDatagramSize = value;
            }
        }

        /// <summary>
        /// The time, in seconds, between checking the RTT (Round Trip Time).
        /// </summary>
        public int RTTUpdateThreshold
        {
            get;
            set;
        } = 5;

        /// <summary>
        /// Should this client adaptively apply compression to outgoing packets?
        /// Defaults to true.
        /// </summary>
        public bool ApplyCompresssion
        {
            get;
            set;
        } = true;

        /// <summary>
        /// The minimum data size for applying compression.
        /// Defaults to 1024 bytes (1 KB).
        /// </summary>
        public int CompressionThreshold
        {
            get;
            set;
        } = 1024;

        /// <summary>
        /// The default compression level (Fastest, Optimal, NoCompression).
        /// Defaults to Optimal.
        /// </summary>
        public CompressionLevel CompressionLevel
        {
            get;
            set;
        } = CompressionLevel.Optimal;

        /// <summary>
        /// The RTT (Round Trip Time) compression threshold.
        /// Defaults to 100 ms.
        /// </summary>
        public double RTTCompressionThreshold
        {
            get;
            set;
        } = 100;

        /// <summary>
        /// Is this client connected to a server?
        /// </summary>
        public bool IsConnected
        {
            get { return m_Connected; }
        }

        /// <summary>
        /// Is this client still alive,
        /// I.E has it not missed too 
        /// many heartbeats?
        /// </summary>
        public bool IsAlive
        {
            get { return m_IsAlive; }
        }

        /// <summary>
        /// Gets or sets the number of missed heartbeats.
        /// Defaults to 6. If this number is reached, 
        /// the client will be assumed to be disconnected
        /// and will be disposed.
        /// </summary>
        public int MaxMissedHeartbeats
        {
            get { return m_MaxMissedHeartbeats; }
            set { m_MaxMissedHeartbeats = value; }
        }

        /// <summary>
        /// The number of missed heartbeats.
        /// </summary>
        public int MissedHeartbeats
        {
            get { return m_MissedHeartbeats; }
        }

        /// <summary>
        /// Gets or sets how many seconds should pass before a heartbeat is sent.
        /// Defaults to 30 seconds.
        /// </summary>
        public int HeartbeatInterval
        {
            get { return m_HeartbeatInterval; }
            set { m_HeartbeatInterval = value; }
        }

        private int m_NumLogicalProcessors = Environment.ProcessorCount;
        private int m_NumberOfCores = 0;

        /// <summary>
        /// Used to control the number of threads that can access the
        /// shared resources of a ProcessingBuffer instance. By default,
        /// it is set to the total number of logical processors on the
        /// system. You may manually set this value if you have specific
        /// requirements, such as when you have set a process affinity.
        /// In general, it is recommended to leave this value unchanged
        /// and let the system manage resource utilization.
        /// </summary>
        public int NumberOfLogicalProcessors
        {
            get { return m_NumLogicalProcessors; }
            set { m_NumLogicalProcessors = value; }
        }

        /// <summary>
        /// Used to control the number of threads that can access
        /// the shared resources of a ProcessingBuffer instance. By default,
        /// it is set to the number of cores on the system. You may manually
        /// set this value if you have specific requirements or if the number
        /// of logical processors is greater than the number of cores.
        /// However, in most cases, it is recommended to leave this value
        /// unchanged and let the system manage resource utilization.
        /// </summary>
        public int NumberOfCores
        {
            get { return m_NumberOfCores; }
            set { m_NumberOfCores = value; }
        }

        private ProcessingBuffer m_ProcessingBuffer = new ProcessingBuffer();

        private LoginArgsContainer m_LoginArgs;

        /// <summary>
        /// Fired when a network error occured.
        /// </summary>
		public event NetworkErrorDelegate OnNetworkError;

        /// <summary>
        /// Fired when this NetworkClient instance received data.
        /// </summary>
		public event ReceivedPacketDelegate OnReceivedData;

        /// <summary>
        /// Fired when this NetworkClient instance connected to a server.
        /// </summary>
		public event OnConnectedDelegate OnConnected;

        /// <summary>
        /// Fired when a heartbeat was received.
        /// </summary>
        public event ReceivedHeartbeatDelegate OnReceivedHeartbeat;

        /// <summary>
        /// Fired when this NetworkClient instance disconnected.
        /// </summary>
        public event ClientDisconnectedDelegate OnClientDisconnected;

        /// <summary>
        /// Fired when this MetworkClient instance received a packet about the server's impending disconnection.
        /// </summary>
	    public event ServerDisconnectedDelegate OnServerDisconnected;

        /// <summary>
        /// Fired when this NetworkClient instance missed too many heartbeats.
        /// </summary>
        public event OnConnectionLostDelegate OnConnectionLost;

        /// <summary>
        /// Initializes a client for connecting to a remote server and listening to data.
        /// </summary>
        /// <param name="Sock">A socket for connecting.</param>
        /// <param name="MaxPacketSize">Maximum packet size. Defaults to 1024 bytes.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Sock"/> was null.</exception>
        public NetworkClient(ISocket Sock, int MaxPacketSize = 1024)
        {
            if (Sock == null)
                throw new ArgumentNullException("Sock was null!");

            m_Sock = Sock;

            m_NumberOfCores = ProcessorInfo.GetPhysicalCoreCount();

            m_MissedHeartbeatsLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);
            m_IsAliveLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);
            m_ConnectedLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);

            if(MaxPacketSize != 1024)
            {
                m_RecvBuf = new byte[MaxPacketSize];
                ProcessingBuffer.MAX_PACKET_SIZE = MaxPacketSize;
            }
            else
                m_RecvBuf = new byte[ProcessingBuffer.MAX_PACKET_SIZE];

            if (Sock.SockType == SocketType.Stream)
                m_ProcessingBuffer.OnProcessedPacket += M_ProcessingBuffer_OnProcessedPacket;
            else
            {
                _ = ResendTimedOutPacketsAsync();
                _ = ReceiveFromAsync();
            }
        }

        /// <summary>
        /// Initializes a client that listens for data.
        /// </summary>
        /// <param name="ClientSocket">The client's socket.</param>
        /// <param name="Server">The Listener instance calling this constructor.</param>
        /// <param name="MaxPacketSize">Maximum packet size. Defaults to 1024 bytes.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="ClientSocket"/> or <paramref name="Server"/>
        /// was null.</exception>
        public NetworkClient(ISocket ClientSocket, Listener Server, int MaxPacketSize = 1024)
        {
            if (ClientSocket == null || Server == null)
                throw new ArgumentNullException("ClientSocket or Server!");

            m_Sock = ClientSocket;
            m_Listener = Server;

            if (MaxPacketSize != 1024)
            {
                m_RecvBuf = new byte[MaxPacketSize];
                ProcessingBuffer.MAX_PACKET_SIZE = MaxPacketSize;
            }
            else
                m_RecvBuf = new byte[ProcessingBuffer.MAX_PACKET_SIZE];

            m_NumberOfCores = ProcessorInfo.GetPhysicalCoreCount();

            m_MissedHeartbeatsLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);
            m_IsAliveLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);
            m_ConnectedLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);

            m_ProcessingBuffer.OnProcessedPacket += M_ProcessingBuffer_OnProcessedPacket;

            if (ClientSocket.SockType == SocketType.Stream)
            {
                _ = ReceiveAsync(); // Start the BeginReceive task without awaiting it

                m_MissedHeartbeats = 0;
                _ = CheckforMissedHeartbeats();

                lock (m_ConnectedLock)
                    m_Connected = true;
            }
            else
            {
                _ = ResendTimedOutPacketsAsync();
                _ = ReceiveFromAsync();
            }
        }

        /// <summary>
        /// Initializes a client that listens for data.
        /// This constructur is called by the Parlo.Tests library.
        /// </summary>
        /// <param name="ClientSocket">The client's socket.</param>
        /// <param name="Server">The Listener instance calling this constructor.</param>
        /// <param name="HeartbeatInterval">The interval at which heartbeats are sent. Defaults to 5.</param>
        /// <param name="MaxMissedHeartbeats">The maximum number of missed heartbeats before the connection is considered lost. 
        /// Defaults to 6.</param>
        /// <param name="OnClientDisconnectedAction">An action to be called when the client disconnects. Defaults to null.</param>
        /// <param name="OnConnectionLostAction">An action to be called when the connection is lost. Defaults to null.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="ClientSocket"/> or <paramref name="Server"/>
        /// is null.</exception>
        public NetworkClient(ISocket ClientSocket, Listener Server, int HeartbeatInterval = 5, int MaxMissedHeartbeats = 6,
            Action<NetworkClient> OnClientDisconnectedAction = null,
            Action<NetworkClient> OnConnectionLostAction = null)
        {
            if (ClientSocket == null || Server == null)
                throw new ArgumentNullException("ClientSocket or Server!");

            m_Sock = ClientSocket;
            m_Listener = Server;
            m_RecvBuf = new byte[ProcessingBuffer.MAX_PACKET_SIZE];

            m_NumberOfCores = ProcessorInfo.GetPhysicalCoreCount();

            m_MissedHeartbeatsLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);
            m_IsAliveLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);
            m_ConnectedLock = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);

            m_ProcessingBuffer.OnProcessedPacket += M_ProcessingBuffer_OnProcessedPacket;

            if (OnClientDisconnectedAction != null)
                OnClientDisconnected += (Client) => { OnClientDisconnectedAction(Client); };

            if (OnConnectionLostAction != null)
                OnConnectionLost += (Client) => { OnConnectionLostAction(Client); };

            if (m_Sock.SockType == SocketType.Stream)
            {
                m_MissedHeartbeats = 0;
                m_HeartbeatInterval = HeartbeatInterval;
                m_MaxMissedHeartbeats = MaxMissedHeartbeats;
                _ = CheckforMissedHeartbeats();
            }

            lock (m_ConnectedLock)
                m_Connected = true;

            _ = ReceiveAsync(); // Start the BeginReceive task without awaiting it
        }

        /// <summary>
        /// Connects to a server.
        /// </summary>
        /// <param name="LoginArgs">Arguments used for login.</param>
        public async Task ConnectAsync(LoginArgsContainer LoginArgs)
        {
            if(LoginArgs == null)
                throw new ArgumentNullException(nameof(LoginArgs));

            m_LoginArgs = LoginArgs;

            //Making sure that the client is not already connecting to the login server.
            if (!m_Sock.Connected)
            {
                try
                {
                    m_IP = LoginArgs.Address;
                    m_Port = LoginArgs.Port;
                    await m_Sock.ConnectAsync(m_IP, m_Port);

                    await m_ConnectedLock.WaitAsync();
                    m_Connected = true;
                    m_ConnectedLock.Release();

                    _ = ReceiveAsync();
                    _ = SendHeartbeatAsync();

                    OnConnected?.Invoke(m_LoginArgs);

                }
                catch (SocketException e)
                {
                    //Hopefully all classes inheriting from NetworkedUIElement will subscribe to this...
                    OnNetworkError?.Invoke(e);
                }
            }
        }

        /// <summary>
        /// Should the data be compressed?
        /// </summary>
        /// <param name="Data">The to check.</param>
        /// <param name="RTT">The Round Trip Time.</param>
        /// <returns>True if the data should be compressed, false otherwise.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Data"/> is null.</exception>
        private bool ShouldCompressData(byte[] Data, int RTT)
        {
            if(Data == null)
                throw new ArgumentNullException(nameof(Data));

            if (!ApplyCompresssion)
                return false;

            if (Data.Length < CompressionThreshold)
                return false;

            if (RTT > RTTCompressionThreshold)
                return true;

            return false;
        }

        /// <summary>
        /// Compresses data with GZip.
        /// </summary>
        /// <param name="Data">The data to compress.</param>
        /// <param name="IsUDPData">Is this data sent over UDP?</param>
        /// <returns>The compressed data as an array of bytes.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Data"/> is null.</exception>
        private byte[] CompressData(byte[] Data, bool IsUDPData = false)
        {
            if(Data == null)
                throw new ArgumentNullException(nameof(Data));

            using (var CompressedStream = new MemoryStream())
            {
                using (var GzipStream = new GZipStream(CompressedStream, CompressionLevel))
                {
                    if (!IsUDPData)
                    {
                        GzipStream.Write(Data, 3, Data.Length); //Start reading at offset 3, because the header is 4 bytes.
                        GzipStream.Flush();
                    }
                    else
                    {
                        GzipStream.Write(Data, 4, Data.Length); //Start reading at offset 4, because the header is 5 bytes.
                        GzipStream.Flush();
                    }
                }

                return CompressedStream.ToArray();
            }
        }

        /// <summary>
        /// Decompresses data with GZip.
        /// </summary>
        /// <param name="Data">The data to decompress.</param>
        /// <returns>The decompressed data as an array of bytes.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Data"/> is null.</exception>
        private byte[] DecompressData(byte[] Data)
        {
            if(Data == null)
                throw new ArgumentNullException("Data");

            using (var CompressedStream = new MemoryStream(Data))
            {
                using (var DecompressedStream = new MemoryStream())
                {
                    using (var GzipStream = new GZipStream(CompressedStream, CompressionMode.Decompress))
                        GzipStream.CopyTo(DecompressedStream);

                    return DecompressedStream.ToArray();
                }
            }
        }

        #region UDP

        /// <summary>
        /// Resends packets that have not been acknowledged by the other party.
        /// </summary>
        /// <returns>An awaitable task.</returns>
        protected async Task ResendTimedOutPacketsAsync()
        {
            TimeSpan Timeout = TimeSpan.FromSeconds(m_UDPTimeout); // Set the desired timeout for packets

            while (true)
            {
                if (m_ResendUnackedCTS.IsCancellationRequested)
                    break;

                try
                {
                    List<int> KeysToRemove = new List<int>();
                    foreach (var Entry in m_SentPackets)
                    {
                        int SequenceNumber = Entry.Key;
                        (byte[] PacketData, DateTime SentTime, int NumRetransmissions) = Entry.Value;

                        if (DateTime.UtcNow - SentTime > Timeout)
                        {
                            Logger.Log($"Resending timed-out packet with sequence number: {SequenceNumber}", LogLevel.info);
                            if (m_SentPackets[SequenceNumber].NumRetransmissions < m_MaxResends)
                            {
                                await m_Sock.SendToAsync(new ArraySegment<byte>(PacketData), SocketFlags.None,
                                    m_Sock.RemoteEndPoint);
                                int Retransmissions = m_SentPackets[SequenceNumber].NumRetransmissions;
                                Retransmissions++;
                                // Update the timestamp
                                m_SentPackets[SequenceNumber] = (PacketData, DateTime.UtcNow, Retransmissions);
                            }
                            else
                            {
                                Logger.Log($"Packet with sequence number {SequenceNumber} has timed out and reached the maximum " +
                                    $"number of retransmissions. " + $"Removing it from the list of sent packets.",
                                    LogLevel.warn);
                                KeysToRemove.Add(SequenceNumber);
                                OnConnectionLost?.Invoke(this);
                            }
                        }
                    }

                    // Remove packets that have timed out and reached the maximum number of retransmissions from the dictionary
                    foreach (int Key in KeysToRemove)
                    {
                        (byte[], DateTime, int) Value;
                        m_SentPackets.TryRemove(Key, out Value);
                    }

                    KeysToRemove.Clear();
                }
                catch (Exception Ex)
                {
                    Logger.Log("Error in ResendTimedOutPacketsAsync: " + Ex.Message, LogLevel.error);
                }

                await Task.Delay(Timeout); // Wait for the specified timeout duration before checking again
            }
        }

        /// <summary>
        /// Sends an ACK packet to the specified endpoint.
        /// </summary>
        /// <param name="SenderEndPoint">The endpoint to send to.</param>
        /// <param name="SequenceNumber">The sequence number of the packet to acknowledge.</param>
        /// <returns>An avaitable task.</returns>
        private async Task SendAcknowledgementAsync(EndPoint SenderEndPoint, int SequenceNumber)
        {
            m_LastAcknowledgedSequenceNumber = Math.Max(m_LastAcknowledgedSequenceNumber, SequenceNumber);
            string AcknowledgmentMessage = $"ACK|{m_LastAcknowledgedSequenceNumber}";
            byte[] AcknowledgmentData = Encoding.ASCII.GetBytes(AcknowledgmentMessage);

            await m_Sock.SendToAsync(new ArraySegment<byte>(AcknowledgmentData), SocketFlags.None, SenderEndPoint);
        }

        private ConcurrentDictionary<int, (byte[] PacketData, DateTime SentTime, int NumRetransmissions)> m_SentPackets = 
            new ConcurrentDictionary<int, (byte[], DateTime, int)>();
        private int m_SequenceNumber = 0, m_LastAcknowledgedSequenceNumber = 0;
        private ConcurrentSet<int> m_OutOfOrderPackets = new ConcurrentSet<int>();

        /// <summary>
        /// Asynchronously sends data to a client or server over UDP.
        /// If the data is larger than <see cref="MaxIPv4UDPPayloadSize"/> 
        /// or <see cref="MaxIPv6UDPPayloadSize"/> bytes, depending on the
        /// <see cref="AddressFamily"/> of the <see cref="ParloSocket"/>,
        /// it will be split into multiple fragments.
        /// </summary>
        /// <param name="Data">The data to send.</param>
        /// <param name="EP">The EndPoint to send the data to.</param>
        /// <param name="Reliable">Should the data be sent reliably?</param>
        /// <returns>A Task instance that can be await-ed.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Data"/> is null.</exception>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="EP"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the length of <paramref name="Data"/> is longer than
        /// <see cref="MaxDatagramSize"/> bytes.</exception>
        public async Task SendToAsync(byte[] Data, EndPoint EP, bool Reliable = false)
        {
            if (Data == null || Data.Length < 1)
                throw new ArgumentNullException("Data");

            if (EP == null)
                throw new ArgumentNullException("EP");

            if(Data.Length > m_MaxDatagramSize)
                throw new ArgumentOutOfRangeException($"Data is too large. Max size is {m_MaxDatagramSize} bytes.",
                        "Data");

            try
            {
                // If data exceeds MTU, split it into chunks and send each chunk
                if (Data.Length > ((m_Sock.AFamily == AddressFamily.InterNetwork) ? 
                    MaxIPv4UDPPayloadSize : MaxIPv6UDPPayloadSize))
                {
                    List<byte[]> Chunks;
                    if (m_Sock.AFamily == AddressFamily.InterNetwork)
                        Chunks = SplitDataIntoChunks(Data, MaxIPv4UDPPayloadSize);
                    else
                        Chunks = SplitDataIntoChunks(Data, MaxIPv6UDPPayloadSize);

                    foreach (byte[] Chunk in Chunks)
                        await SendToAsync(Chunk, EP, Reliable);

                    return;
                }

                byte[] PacketToSend;
                if (Reliable)
                {
                    int CurrentSequenceNumber = Interlocked.Increment(ref m_SequenceNumber);

                    if (ShouldCompressData(Data, m_LastRTT))
                    {
                        byte[] CompressedData = CompressData(Data, true);

                        byte[] SequenceNumberBytes = BitConverter.GetBytes(CurrentSequenceNumber);
                        PacketToSend = new byte[CompressedData.Length + SequenceNumberBytes.Length];

                        Array.Copy(SequenceNumberBytes, PacketToSend, SequenceNumberBytes.Length);
                        Array.Copy(CompressedData, 0, PacketToSend, SequenceNumberBytes.Length, CompressedData.Length);
                    }
                    else
                    {
                        byte[] SequenceNumberBytes = BitConverter.GetBytes(CurrentSequenceNumber);
                        PacketToSend = new byte[Data.Length + SequenceNumberBytes.Length];
                        Array.Copy(SequenceNumberBytes, PacketToSend, SequenceNumberBytes.Length);
                        Array.Copy(Data, 0, PacketToSend, SequenceNumberBytes.Length, Data.Length);
                    }

                    m_SentPackets[CurrentSequenceNumber] = (PacketToSend, DateTime.UtcNow, 0);
                    await m_Sock.SendToAsync(new ArraySegment<byte>(PacketToSend), SocketFlags.None, EP);
                }
                else
                {
                    PacketToSend = Data;

                    if (ShouldCompressData(Data, m_LastRTT))
                    {
                        Packet CompressedPacket = new Packet(Data[0], PacketToSend, true, Reliable);
                        await m_Sock.SendToAsync(new ArraySegment<byte>(CompressedPacket.BuildPacket()), SocketFlags.None, EP);
                    }
                    else
                        await m_Sock.SendToAsync(new ArraySegment<byte>(PacketToSend), SocketFlags.None, EP);
                }
            }
            catch (SocketException E)
            {
                if (E.SocketErrorCode == SocketError.MessageSize)
                {
                    Logger.Log("Error sending data: Datagram is too long.", LogLevel.warn);
                    throw new ArgumentOutOfRangeException("Data", "Datagram is too long.");
                }
            }
            catch (Exception Ex)
            {
                Logger.Log("Error sending data: " + Ex.Message, LogLevel.error);
            }
        }

        /// <summary>
        /// Receives data from a client or server over UDP.
        /// </summary>
        /// <returns>An awaitable task.</returns>
        /// <exception cref="SocketException">Thrown if a <see cref="SocketException"/> occured during sending.</exception>
        /// <exception cref="Exception">Thrown if an <see cref="Exception"/> occured during sending.</exception>
        private async Task ReceiveFromAsync()
        {
            byte[] ReceiveBuffer = new byte[MTU];

            while (true)
            {
                if(m_ReceiveFromCTS.IsCancellationRequested)
                    break;

                try
                {
                    //Receive the data and store the sender's endpoint
                    SocketReceiveFromResult Result = await m_Sock.ReceiveFromAsync(new ArraySegment<byte>(ReceiveBuffer),
                        SocketFlags.None, new IPEndPoint(IPAddress.Parse(m_Sock.Address), 0));
                        SocketFlags.None, new IPEndPoint(IPAddress.Parse(m_Sock.Address), m_Sock.Port));

                    //Process the received data
                    byte[] ReceivedData = new byte[Result.ReceivedBytes];
                    Array.Copy(ReceiveBuffer, ReceivedData, Result.ReceivedBytes);

                    //Check if the received data is an acknowledgement message
                    string ReceivedMessage = Encoding.ASCII.GetString(ReceivedData);
                    if (ReceivedMessage.StartsWith("ACK|"))
                    {
                        int AcknowledgedSequenceNumber = int.Parse(ReceivedMessage.Substring(4));
                        foreach (var key in m_SentPackets.Keys.Where(k => k <= AcknowledgedSequenceNumber).ToList())
                        {
                            (byte[], DateTime, int) value;
                            m_SentPackets.TryRemove(key, out value);
                        }
                    }
                    else
                    {
                        //Assume the first 4 bytes are the sequence number
                        int ReceivedSequenceNumber = BitConverter.ToInt32(ReceivedData, 0);

                        if (ReceivedSequenceNumber > m_LastAcknowledgedSequenceNumber)
                        {
                            if (ReceivedSequenceNumber != m_LastAcknowledgedSequenceNumber + 1)
                                m_OutOfOrderPackets.Add(ReceivedSequenceNumber); //Packet received out of order
                            else
                            {
                                //Next packet in order received
                                m_LastAcknowledgedSequenceNumber++;

                                //Check for any buffered packets that can now be acknowledged in order
                                while (m_OutOfOrderPackets.Remove(m_LastAcknowledgedSequenceNumber + 1))
                                    m_LastAcknowledgedSequenceNumber++;

                                await SendAcknowledgementAsync(Result.RemoteEndPoint, m_LastAcknowledgedSequenceNumber);
                            }
                        }

                        await ProcessReceivedDataAsync(Result.RemoteEndPoint, ReceivedData);
                    }
                }
                catch (SocketException E)
                {
                    Logger.Log("Error receiving data: " + E.Message, LogLevel.error);
                }
                catch (Exception Ex)
                {
                    Logger.Log("Error receiving data: " + Ex.Message, LogLevel.error);
                }
            }
        }

        private ConcurrentDictionary<int, byte[]> m_FragmentedPackets = new ConcurrentDictionary<int, byte[]>();
        private int m_LengthOfCurrentFragPacket = 0;
 
        /// <summary>
        /// Processes data received over UDP.
        /// </summary>
        /// <param name="SenderEndPoint">The endpoint from which the data was sent.</param>
        /// <param name="ReceivedData">The received data.</param>
        /// <returns>An availatable task.</returns>
        /// <exception cref="ArgumentException">Thrown if <paramref name="SenderEndPoint"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown if <paramref name="ReceivedData"/> is null.</exception>
        /// <exception cref="BufferOverflowException">Thrown if the total length of all fragments received
        /// exceeds <see cref="MaxDatagramSize"/> or the total length of all fragments received + the
        /// length of <paramref name="ReceivedData"/> exceeds <see cref="MaxDatagramSize"/></exception>
        private async Task ProcessReceivedDataAsync(EndPoint SenderEndPoint, byte[] ReceivedData)
        {
            if(SenderEndPoint == null)
                throw new ArgumentNullException("SenderEndPoint", "SenderEndPoint cannot be null.");
            if(ReceivedData == null)
                throw new ArgumentNullException("ReceivedData", "ReceivedData cannot be null.");

            int SequenceNumber = BitConverter.ToInt32(ReceivedData.ToList().GetRange(0, 4).ToArray());
            bool IsCompressed = (ReceivedData[5] == 1) ? true : false;
            bool IsReliable = (ReceivedData[6] == 1) ? true : false;

            //The length starts at the 8th byte because of the sequence number.
            ushort Length = BitConverter.ToUInt16(ReceivedData.ToList().GetRange(8, 2).ToArray());
            if (Length > ReceivedData.Length) //We received a fragmented packet!
            {
                if (Length > m_MaxDatagramSize) //Houston, we have a problem - ABANDON SHIP!
                    throw new BufferOverflowException();

                if((m_LengthOfCurrentFragPacket > MaxDatagramSize) || ((m_LengthOfCurrentFragPacket + ReceivedData.Length) > 
                    MaxDatagramSize))
                    throw new BufferOverflowException(); //Houston, we have a problem - ABANDON SHIP!

                //Only add a fragment if it hasn't already been received.
                if (m_FragmentedPackets.TryAdd(SequenceNumber, ReceivedData))
                {
                    m_LengthOfCurrentFragPacket += ReceivedData.Length;

                    if (m_LengthOfCurrentFragPacket == Length)
                    {
                        SortedList<int, byte[]> SortedPackets = new SortedList<int, byte[]>(m_FragmentedPackets);

                        List<byte> ReconstructedData = new List<byte>();
                        bool IsFirstPacket = true;

                        foreach (var Packet in SortedPackets)
                        {
                            byte[] Chunk = Packet.Value;

                            if (IsFirstPacket)
                            {
                                // For the first packet, we include the entire chunk (which includes the header)
                                byte[] Header = new byte[(int)PacketHeaders.UDP];
                                Array.Copy(Chunk, 0, Header, 0, (int)PacketHeaders.UDP);
                                ReconstructedData.AddRange(Header);
                                ReconstructedData.AddRange((IsCompressed == true) ?
                                    DecompressData(Chunk.Skip((int)PacketHeaders.UDP).ToArray()) :
                                    Chunk.Skip((int)PacketHeaders.UDP));
                                IsFirstPacket = false;
                            }
                            else
                            {
                                // For subsequent packets, we skip the header when adding to the reconstructed data
                                ReconstructedData.AddRange((IsCompressed == true) ?
                                    DecompressData(Chunk.Skip((int)PacketHeaders.UDP).ToArray()) :
                                    Chunk.Skip((int)PacketHeaders.UDP));
                            }
                        }

                        ProcessDataInOrder(ReconstructedData.ToArray());

                        m_FragmentedPackets.Clear();
                        m_LengthOfCurrentFragPacket = 0;
                    }
                    else
                        return;
                }
            }

            if (IsReliable)
            {
                if (SequenceNumber > m_LastAcknowledgedSequenceNumber)
                {
                    if (SequenceNumber != m_LastAcknowledgedSequenceNumber + 1)
                        m_OutOfOrderPackets.Add(SequenceNumber); //Packet received out of order
                    else
                    {
                        //Next packet in order received
                        m_LastAcknowledgedSequenceNumber++;

                        //Check for any buffered packets that can now be acknowledged in order
                        while (m_OutOfOrderPackets.Remove(m_LastAcknowledgedSequenceNumber + 1))
                            m_LastAcknowledgedSequenceNumber++;

                        await SendAcknowledgementAsync(SenderEndPoint, m_LastAcknowledgedSequenceNumber);
                    }
                }
            }
        }

        /// <summary>
        /// Processes data received over UDP in order.
        /// </summary>
        /// <param name="ReceivedData">The received data.</param>
        private void ProcessDataInOrder(byte[] ReceivedData)
        {
            int SequenceNumber = BitConverter.ToInt32(ReceivedData.ToList().GetRange(0, 4).ToArray(), 0);
            byte ID = ReceivedData[4];
            bool IsCompressed = (ReceivedData[6] == 1) ? true : false;
            bool IsReliable = (ReceivedData[7] == 1) ? true : false;
            byte[] DecompressedData;

            Packet ReceivedPacket;

            if (IsCompressed)
            {
                if (ReceivedData.Length < MTU)
                {
                    DecompressedData = DecompressData(ReceivedData);
                    ReceivedPacket = new Packet((byte)SequenceNumber, ID, DecompressedData, IsCompressed, IsReliable);
                }
                else
                    ReceivedPacket = new Packet((byte)SequenceNumber, ID, ReceivedData, IsCompressed, IsReliable);
            }
            else
                ReceivedPacket = new Packet((byte)SequenceNumber, ID, ReceivedData, IsCompressed, IsReliable);

            OnReceivedData?.Invoke(this, ReceivedPacket);
        }

        /// <summary>
        /// Splits a packet into chunks if the packet's size was larger than the MTU.
        /// </summary>
        /// <param name="Data">The data to split.</param>
        /// <param name="MTU">The MTU.</param>
        /// <param name="IsCompressed">Is the packet compressed?</param>
        /// <returns>A <see cref="List{T}"/> with the chunks as binary arrays.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Data"/> is null.</exception>
        private List<byte[]> SplitDataIntoChunks(byte[] Data, int MTU, bool IsCompressed = false)
        {
            if(Data == null)
                throw new ArgumentNullException("Data", "Data cannot be null.");

            List<byte[]> Chunks = new List<byte[]>();
            byte[] CompressedData;

            // Compress the data before splitting into chunks, if compression is needed.
            if (IsCompressed)
                CompressedData = CompressData(Data, true);
            else
                CompressedData = Data;

            //Copy over the header.
            byte[] Header = new byte[3];
            Array.Copy(Data, Header, 3); // The first 3 bytes are the header, minus the length.

            byte[] LengthBytes = BitConverter.GetBytes((ushort)CompressedData.Length);

            byte[] TmpHeader = new byte[Header.Length + LengthBytes.Length];
            Array.Copy(Header, 0, TmpHeader, 0, Header.Length);
            Array.Copy(LengthBytes, 0, TmpHeader, Header.Length, LengthBytes.Length);

            for (int i = 0; i < CompressedData.Length; i += MTU)
            {
                int CurrentChunkSize = Math.Min(MTU, CompressedData.Length - i);
                byte[] Chunk = new byte[CurrentChunkSize];
                Array.Copy(CompressedData, i, Chunk, 0, CurrentChunkSize);

                if (i > 0)
                {
                    List<byte> ChunkList = new List<byte>(Header);
                    ChunkList.AddRange(Chunk);
                    Chunks.Add(ChunkList.ToArray());
                }
                else
                {
                    if(!IsCompressed)
                        Chunks.Add(Chunk);
                    else //If the data was compressed, it means the first chunk won't have the header.
                    {
                        List<byte> ChunkList = new List<byte>(Header);
                        ChunkList.AddRange(Chunk);
                        Chunks.Add(ChunkList.ToArray());
                    }
                }
            }

            return Chunks;
        }

        #endregion

        /// <summary>
        /// Asynchronously sends data to a connected client or server.
        /// </summary>
        /// <param name="Data">The data to send.</param>
        /// <returns>A Task instance that can be await-ed.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Data"/> is null or has a length of 0.</exception>
        /// <exception cref="SocketException">Thrown if the socket attempted to send data without being connected.</exception>
        /// <exception cref="BufferOverflowException">Thrown if the size of <paramref name="Data"/> is larger than
        /// <see cref="MaxTCPPacketSize"/>.</exception>
        public async Task SendAsync(byte[] Data)
        {
            if (Data == null || Data.Length < 1)
                throw new ArgumentNullException("Data");
            if (Data.Length > ProcessingBuffer.MAX_PACKET_SIZE)
                throw new BufferOverflowException(); //Houston, we have a problem - ABANDON SHIP!

            try
            {
                if (m_Connected)
                {
                    if (ShouldCompressData(Data, m_LastRTT))
                    {
                        byte[] CompressedData = CompressData(Data);
                        Packet CompressedPacket = new Packet(Data[0], CompressedData, true);
                        await m_Sock.SendAsync(new ArraySegment<byte>(CompressedPacket.BuildPacket()), SocketFlags.None);
                        CompressedData = null;
                    }
                    else
                        await m_Sock.SendAsync(new ArraySegment<byte>(Data), SocketFlags.None);
                }
                else
                    throw new SocketException((int)SocketError.NotConnected);
            }
            catch (SocketException E)
            {
                if (E.SocketErrorCode == SocketError.NotConnected)
                {
                    Logger.Log("Error sending data: Client is not connected.", LogLevel.warn);
                    throw E;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Error sending data: " + ex.Message, LogLevel.error);

                //Disconnect without sending the disconnect message to prevent recursion.
                await DisconnectAsync(false);
            }
        }

        /// <summary>
        /// We processed a packet, hurray!
        /// </summary>
        /// <param name="ProcessedPacket">The packet that was processed.</param>
        private void M_ProcessingBuffer_OnProcessedPacket(Packet ProcessedPacket)
        {
            if (ProcessedPacket.ID == (byte)ParloIDs.SGoodbye)
            {
                OnServerDisconnected?.Invoke(this);
                return;
            }
            if (ProcessedPacket.ID == (byte)ParloIDs.CGoodbye) //Client notified server of disconnection.
            {
                OnClientDisconnected?.Invoke(this);
                return;
            }
            if (ProcessedPacket.ID == (byte)ParloIDs.Heartbeat)
            {
                lock (m_IsAliveLock) //Just lock here because this section is not async.
                    m_IsAlive = true;

                lock (m_MissedHeartbeatsLock) //Just lock here because this section is not async.
                    m_MissedHeartbeats = 0;

                HeartbeatPacket Heartbeat = HeartbeatPacket.ByteArrayToObject(ProcessedPacket.Data);
                TimeSpan Duration = DateTime.UtcNow - Heartbeat.SentTimestamp;
                m_LastRTT = (int)(Duration.TotalMilliseconds + Heartbeat.TimeSinceLast.TotalMilliseconds);

                OnReceivedHeartbeat?.Invoke(this);

                return;
            }

            if (ProcessedPacket.IsCompressed == 1)
            {
                byte[] DecompressedData = DecompressData(ProcessedPacket.Data);
                OnReceivedData?.Invoke(this, new Packet(ProcessedPacket.ID, DecompressedData, false));
            }
            else
                OnReceivedData?.Invoke(this, ProcessedPacket);
        }

        /// <summary>
        /// Asynchronously receives data.
        /// </summary>
        private async Task ReceiveAsync()
        {
            byte[] TmpBuf;

            while (m_Connected)
            {
                if (m_ReceiveCancellationTokenSource.Token.IsCancellationRequested)
                    return;

                if (m_Sock == null || !m_Sock.Connected)
                    return;

                try
                {
                    int BytesRead = await m_Sock.ReceiveAsync(new ArraySegment<byte>(m_RecvBuf), SocketFlags.None);

                    if (BytesRead > 0)
                    {
                        TmpBuf = new byte[BytesRead];
                        Buffer.BlockCopy(m_RecvBuf, 0, TmpBuf, 0, BytesRead);
                        //Clear, to make sure this buffer is always fresh.
                        Array.Clear(m_RecvBuf, 0, m_RecvBuf.Length);

                        try
                        {
                            //Keep shoveling shit into the buffer as fast as we can.
                            m_ProcessingBuffer.AddData(TmpBuf); //Hence the Shoveling Shit Algorithm (SSA).
                        }
                        catch(BufferOverflowException)
                        {
                            Logger.Log("Tried adding too much data into ProcessingBuffer!", LogLevel.warn);
                            //This should never happen, so we don't need to do anything here.
                        }
                    }
                    else //Can't do anything with this!
                    {
                        await DisconnectAsync(false);
                        return;
                    }

                    await Task.Delay(10); //STOP HOGGING THE PROCESSOR!
                }
                catch (SocketException)
                {
                    await DisconnectAsync(false);
                    return;
                }
            }
        }

        /// <summary>
        /// This NetworkClient's remote IP. Will return null if the NetworkClient's socket is not connected remotely.
        /// </summary>
        public string RemoteIP
        {
            get
            {
                IPEndPoint RemoteEP = (IPEndPoint)m_Sock.RemoteEndPoint;

                if (RemoteEP != null)
                    return RemoteEP.Address.ToString();
                else
                    return null;
            }
        }

        /// <summary>
        /// This socket's remote port. Will return 0 if the socket is not connected remotely.
        /// </summary>
        public int RemotePort
        {
            get
            {
                IPEndPoint RemoteEP = (IPEndPoint)m_Sock.RemoteEndPoint;

                if (RemoteEP != null)
                    return RemoteEP.Port;
                else
                    return 0;
            }
        }

        #region Heartbeat

        /// <summary>
        /// Periodically sends a heartbeat to the server.
        /// How often is determined by HeartbeatInterval.
        /// </summary>
        private async Task SendHeartbeatAsync()
        {
            try
            {
                while (true)
                {
                    if (m_SendHeartbeatCTS.Token.IsCancellationRequested)
                        break;

                    try
                    {
                        HeartbeatPacket Heartbeat = new HeartbeatPacket((DateTime.UtcNow > m_LastHeartbeatSent) ?
                            DateTime.UtcNow - m_LastHeartbeatSent : m_LastHeartbeatSent - DateTime.UtcNow);
                        m_LastHeartbeatSent = DateTime.UtcNow;
                        byte[] HeartbeatData = Heartbeat.ToByteArray();
                        Packet Pulse = new Packet((byte)ParloIDs.Heartbeat, HeartbeatData, false);
                        await SendAsync(Pulse.BuildPacket());
                    }
                    catch (Exception E)
                    {
                        Logger.Log("Error sending heartbeat: " + E.Message, LogLevel.error);
                    }

                    await Task.Delay(m_HeartbeatInterval * 1000, m_SendHeartbeatCTS.Token);
                }
            }
            catch (TaskCanceledException)
            {
                Logger.Log("SendHeartbeat task cancelled", LogLevel.info);
            }
        }

        /// <summary>
        /// Periodically checks for missed heartbeats.
        /// How often is determined by HeartbeatInterval.
        /// </summary>
        private async Task CheckforMissedHeartbeats()
        {
            try
            {
                while (true)
                {
                    if (m_CheckMissedHeartbeatsCTS.Token.IsCancellationRequested)
                        break;

                    await m_MissedHeartbeatsLock.WaitAsync();
                    m_MissedHeartbeats++;
                    m_MissedHeartbeatsLock.Release();

                    if (m_MissedHeartbeats > m_MaxMissedHeartbeats)
                    {
                        await m_IsAliveLock.WaitAsync();
                        m_IsAlive = false;
                        m_IsAliveLock.Release();

                        OnConnectionLost?.Invoke(this);
                    }

                    await Task.Delay(m_HeartbeatInterval * 1000, m_CheckMissedHeartbeatsCTS.Token);
                }
            }
            catch (TaskCanceledException)
            {
                Logger.Log("CheckforMissedHeartbeats task cancelled", LogLevel.info);
            }
        }

        #endregion

        /// <summary>
        /// Disconnects this NetworkClient instance and stops
        /// all sending and receiving of data.
        /// </summary>
        /// <param name="SendDisconnectMessage">Should a DisconnectMessage be sent?</param>
        public async Task DisconnectAsync(bool SendDisconnectMessage = true)
        {
            try
            {
                if (m_Connected && m_Sock != null)
                {
                    if (SendDisconnectMessage)
                    {
                        //Set the timeout to five seconds by default for clients,
                        //even though it's not really important for clients.
                        GoodbyePacket ByePacket = new GoodbyePacket((int)ParloDefaultTimeouts.Client);
                        byte[] ByeData = ByePacket.ToByteArray();
                        Packet Goodbye = new Packet((byte)ParloIDs.CGoodbye, ByeData, false);
                        await SendAsync(Goodbye.BuildPacket());
                    }

                    if (m_Sock.Connected)
                    {
                        // Shutdown and disconnect the socket.
                        m_Sock.Shutdown(SocketShutdown.Both);
                        m_Sock.Disconnect(true);
                    }

                    await m_ConnectedLock.WaitAsync();
                    m_Connected = false;
                    m_ConnectedLock.Release();
                }
            }
            catch (SocketException E)
            {
                Logger.Log("Exception happened during NetworkClient.DisconnectAsync():" + E.ToString(), LogLevel.error);
            }
            catch (ObjectDisposedException)
            {
                Logger.Log("NetworkClient.DisconnectAsync() tried to shutdown or disconnect socket that was already disposed",
                    LogLevel.warn);
            }
        }

        /// <summary>
        /// Finds a PacketHandler instance based on the provided ID.
        /// </summary>
        /// <param name="ID">The ID of the PacketHandler to retrieve.</param>
        /// <returns>A PacketHandler instance if it was found, or null if it wasn't.</returns>
        private PacketHandler FindPacketHandler(byte ID)
        {
            PacketHandler Handler = PacketHandlers.Get(ID);

            if (Handler != null)
                return Handler;
            else return null;
        }

        /// <summary>
        /// Returns a unique hash code for this NetworkClient instance.
        /// Needs to be implemented for this class to be usable in a 
        /// Dictionary.
        /// </summary>
        /// <returns>A unique hash code.</returns>
        public override int GetHashCode()
        {
            return SessionId.GetHashCode();
        }

        /// <summary>
        /// Finalizer for NetworkClient.
        /// </summary>
        ~NetworkClient()
        {
            Dispose(false);
        }

        /// <summary>
        /// Disposes of the resources used by this NetworkClient instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Disposes of the resources used by this NetworkClient instance.
        /// <param name="Disposed">Was this resource disposed explicitly?</param>
        /// </summary>
        protected virtual void Dispose(bool Disposed)
        {
            if (Disposed)
            {
                if (m_Sock != null)
                    m_Sock.Dispose();

                m_ProcessingBuffer.OnProcessedPacket -= M_ProcessingBuffer_OnProcessedPacket;
                m_ProcessingBuffer.Dispose();

                // Prevent the finalizer from calling ~NetworkClient, since the object is already disposed at this point.
                GC.SuppressFinalize(this);
            }
            else
                Logger.Log("NetworkClient not explicitly disposed!", LogLevel.error);
        }

        /// <summary>
        /// Disposes of the async resources used by this NetworkClient instance.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (m_Sock.SockType == SocketType.Stream)
            {
                m_ReceiveCancellationTokenSource.Cancel();

                if (m_Listener == null) //This is already called by the NetworkListener instance in its Dispose method...
                    await DisconnectAsync();
            }

            if (m_Sock.SockType == SocketType.Dgram)
            {
                m_ResendUnackedCTS.Cancel();
                m_ReceiveFromCTS.Cancel();
            }

            m_CheckMissedHeartbeatsCTS.Cancel();
            m_SendHeartbeatCTS.Cancel();
        }
    }
}
