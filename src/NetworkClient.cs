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
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Parlo.Tests")]
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
	public delegate Task ReceivedPacketDelegate(NetworkClient Sender, Packet P);

    /// <summary>
    /// Occurs when a client connected to a server.
    /// </summary>
    /// <param name="LoginArgs">The arguments that were used to establish the connection.</param>
	public delegate Task OnConnectedDelegate(LoginArgsContainer LoginArgs);

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
        /// <exception cref="InvalidOperationException">Thrown if <paramref name="Sock"/> had a <see cref="SocketType"/>
        /// of <see cref="SocketType.Dgram"/>.</exception>
        public NetworkClient(ISocket Sock, int MaxPacketSize = 1024)
        {
            if (Sock == null)
                throw new ArgumentNullException("Sock was null!");

            if (Sock.SockType == SocketType.Dgram)
                throw new InvalidOperationException("Please use Parlo.UDPNetworkClient for UDP connections!");

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

            m_ProcessingBuffer.OnProcessedPacket += M_ProcessingBuffer_OnProcessedPacket;
        }

        /// <summary>
        /// Initializes a client that listens for data.
        /// </summary>
        /// <param name="ClientSocket">The client's socket.</param>
        /// <param name="Server">The Listener instance calling this constructor.</param>
        /// <param name="MaxPacketSize">Maximum packet size. Defaults to 1024 bytes.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="ClientSocket"/> or <paramref name="Server"/>
        /// was null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if <paramref name="ClientSocket"/> had a <see cref="SocketType"/>
        /// of <see cref="SocketType.Dgram"/>.</exception>
        public NetworkClient(ISocket ClientSocket, Listener Server, int MaxPacketSize = 1024)
        {
            if (ClientSocket == null || Server == null)
                throw new ArgumentNullException("ClientSocket or Server!");

            if (ClientSocket.SockType == SocketType.Dgram)
                throw new InvalidOperationException("Please use Parlo.UDPNetworkClient for UDP connections!");

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

            lock (m_ConnectedLock)
                m_Connected = true;

            _ = ReceiveAsync(); // Start the BeginReceive task without awaiting it

            m_MissedHeartbeats = 0;
            _ = CheckforMissedHeartbeats();
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
        /// <exception cref="InvalidOperationException">Thrown if <paramref name="ClientSocket"/> had a <see cref="SocketType"/>
        /// of <see cref="SocketType.Dgram"/>.</exception>
        internal NetworkClient(ISocket ClientSocket, Listener Server, int HeartbeatInterval = 5, int MaxMissedHeartbeats = 6,
            Action<NetworkClient> OnClientDisconnectedAction = null,
            Action<NetworkClient> OnConnectionLostAction = null)
        {
            if (ClientSocket == null || Server == null)
                throw new ArgumentNullException("ClientSocket or Server!");

            if (ClientSocket.SockType == SocketType.Dgram)
                throw new InvalidOperationException("Please use Parlo.UDPNetworkClient for UDP connections!");

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

            m_MissedHeartbeats = 0;
            m_HeartbeatInterval = HeartbeatInterval;
            m_MaxMissedHeartbeats = MaxMissedHeartbeats;
            _ = CheckforMissedHeartbeats();

            lock (m_ConnectedLock)
                m_Connected = true;

            _ = ReceiveAsync(); // Start the BeginReceive task without awaiting it
        }

        /// <summary>
        /// Connects to a server.
        /// If a <see cref="SocketException"/> occurs, the <see cref="OnNetworkError"/> is invoked.
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
        private byte[] CompressData(byte[] Data)
        {
            if(Data == null)
                throw new ArgumentNullException(nameof(Data));

            using (var CompressedStream = new MemoryStream())
            {
                using (var GzipStream = new GZipStream(CompressedStream, CompressionLevel))
                {
                    GzipStream.Write(Data, 3, Data.Length); //Start reading at offset 3, because the header is 4 bytes.
                    GzipStream.Flush();
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
        private async Task M_ProcessingBuffer_OnProcessedPacket(Packet ProcessedPacket)
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
                await m_IsAliveLock.WaitAsync();
                    m_IsAlive = true;
                m_IsAliveLock.Release();

                await m_MissedHeartbeatsLock.WaitAsync();
                    m_MissedHeartbeats = 0;
                m_MissedHeartbeatsLock.Release();

                HeartbeatPacket Heartbeat = HeartbeatPacket.ByteArrayToObject(ProcessedPacket.Data);
                TimeSpan Duration = DateTime.UtcNow - Heartbeat.SentTimestamp;
                m_LastRTT = (int)(Duration.TotalMilliseconds + Heartbeat.TimeSinceLast.TotalMilliseconds);

                OnReceivedHeartbeat?.Invoke(this);

                return;
            }

            if (ProcessedPacket.IsCompressed == 1)
            {
                byte[] DecompressedData = DecompressData(ProcessedPacket.Data);
                await OnReceivedData?.Invoke(this, new Packet(ProcessedPacket.ID, DecompressedData, false));
            }
            else
                await OnReceivedData?.Invoke(this, ProcessedPacket);
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
                catch (SocketException Ex)
                {
                    Logger.Log("Exception in NetworkClient.ReceiveAsync: " + Ex.ToString(), LogLevel.error);
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
            m_ReceiveCancellationTokenSource.Cancel();

            if (m_Listener == null) //This is already called by the NetworkListener instance in its Dispose method...
                await DisconnectAsync();

            m_CheckMissedHeartbeatsCTS.Cancel();
            m_SendHeartbeatCTS.Cancel();
        }
    }
}
