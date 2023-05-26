using Parlo.Collections;
using Parlo.Exceptions;
using Parlo.Packets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.IO.Compression;
using System.IO;

namespace Parlo
{
    /// <summary>
    /// Occurs when a packet was received.
    /// </summary>
    /// <param name="Sender">The NetworkClient instance that sent or received the packet.</param>
    /// <param name="P">The Packet that was received.</param>
    public delegate void ReceivedUDPPacketDelegate(UDPNetworkClient Sender, Packet P);

    /// <summary>
    /// Occurs when a client missed too many heartbeats.
    /// </summary>
    /// <param name="Sender">The client that lost too many heartbeats.</param>
    public delegate void OnUDPConnectionLostDelegate(UDPNetworkClient Sender);

    /// <summary>
    /// A network client that uses UDP to send and receive data.
    /// </summary>
    public class UDPNetworkClient : IDisposable, IAsyncDisposable
    {
        private ISocket m_Sock;

        /// <summary>
        /// This client's SessionID. Ensures that a client is unique even if 
        /// multiple clients are trying to connect using the same IP.
        /// </summary>
        public Guid SessionID { get; } = Guid.NewGuid();

        /// <summary>
        /// Constructs a new UDPNetworkClient instance.
        /// </summary>
        /// <param name="Sock">The socket to use.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Sock"/>was null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if <paramref name="Sock"/> had a <see cref="SocketType"/>
        /// of <see cref="SocketType.Stream"/>.</exception>
        public UDPNetworkClient(ISocket Sock)
        {
            if(Sock == null)
                throw new ArgumentNullException(nameof(Sock));
            if(Sock.SockType == SocketType.Stream)
                throw new ArgumentException("The socket must be a UDP socket. Please use NetworkClient for TCP.", nameof(Sock));

            m_Sock = Sock;
        }

        /// <summary>
        /// Gets the maximum payload size for a UDP packet, assuming IPv4.
        /// </summary>
        public const int MaxIPv4UDPPayloadSize = MTU - 20 - 8;

        /// <summary>
        /// Gets the maximum payload size for a UDP packet, assuming IPv6.
        /// </summary>
        public const int MaxIPv6UDPPayloadSize = MTU - 40 - 8;

        /// <summary>
        /// CancellationTokenSource that can be set to cancel the ResendTimedOutPacketsAsync() task.
        /// </summary>
        protected CancellationTokenSource m_ResendUnackedCTS = new CancellationTokenSource();

        /// <summary>
        /// CancellationTokenSource that can be set to cancel the ReceiveFromAsync() task.
        /// </summary>
        protected CancellationTokenSource m_ReceiveFromCTS = new CancellationTokenSource();

        /// <summary>
        /// Gets or sets the time in seconds before a UDP packet is considered lost.
        /// Defaults to 5.
        /// </summary>
        public int UDPTimeout
        {
            get { return m_UDPTimeout; }
            set { m_UDPTimeout = value; }
        }

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
                if (value > 65535)
                    throw new ArgumentOutOfRangeException(nameof(value), "The maximum datagram size cannot be larger than 65535 bytes.");

                m_MaxDatagramSize = value;
            }
        }

        /// <summary>
        /// The Maximum Transmission Unit (MTU) for the NetworkClient.
        /// This value assumes an Ethernet-based network with a 1500-byte MTU.
        /// </summary>
        private const int MTU = 1500;

        /// <summary>
        /// The number of seconds before packets are considered to be
        /// timed out and an ack will be resent.
        /// </summary>
        protected int m_UDPTimeout = 5;

        /// <summary>
        /// The maximum number of times a packet can be resent over UDP.
        /// Defaults to 5.
        /// </summary>
        protected int m_MaxResends = 5;

        //The last Round Trip Time (RTT) in milliseconds.
        private int m_LastRTT = 0;

        private int m_MaxDatagramSize = 65535;

        /// <summary>
        /// Fired when this NetworkClient instance missed too many heartbeats.
        /// </summary>
        public event OnUDPConnectionLostDelegate OnConnectionLost;

        /// <summary>
        /// Fired when this NetworkClient instance received data.
        /// </summary>
        public event ReceivedUDPPacketDelegate OnReceivedData;

        /// <summary>
        /// Fired when a network error occured.
        /// </summary>
        public event NetworkErrorDelegate OnNetworkError;

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
        /// Starts receiving UDP data and resending timed out packets.
        /// The <see cref="ISocket"/>s <see cref="ISocket.RemoteEndPoint"/> must be set before calling this method.
        /// If a <see cref="SocketException"/> occurs, the <see cref="OnNetworkError"/> event is invoked.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the <see cref="ISocket"/> that this client was constructed 
        /// with didn't have a SocketType of DGram.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the <see cref="ISocket.RemoteEndPoint"/> was null.</exception>
        public void StartReceiveFromAsync()
        {
            if (m_Sock.SockType != SocketType.Dgram)
                throw new InvalidOperationException("This method can only be called on UDP clients.");
            if (m_Sock.RemoteEndPoint == null)
                throw new InvalidOperationException("Remote endpoint cannot be null!");

            try
            {
                m_Sock.Bind(m_Sock.RemoteEndPoint);
            }
            catch (SocketException Ex)
            {
                Logger.Log("SocketException in NetworkClient.Bind : " + Ex.Message, LogLevel.error);
                OnNetworkError?.Invoke(Ex);
            }

            //We don't need to check these tasks, as they will deal with errors internally.
            _ = ResendTimedOutPacketsAsync();
            _ = ReceiveFromAsync();
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
            if (Data == null)
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
            if (Data == null)
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
            if (Data == null)
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
        /// Resends packets that have not been acknowledged by the other party.
        /// </summary>
        /// <returns>An awaitable task.</returns>
        internal async Task ResendTimedOutPacketsAsync()
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
                                //TODO: Add error checking here.
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
                catch (SocketException Ex)
                {
                    Logger.Log("SocketException in ResendTimedOutPacketsAsync: " + Ex.Message, LogLevel.error);
                    OnNetworkError?.Invoke(Ex);
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
        internal async Task SendAcknowledgementAsync(EndPoint SenderEndPoint, int SequenceNumber)
        {
            m_LastAcknowledgedSequenceNumber = Math.Max(m_LastAcknowledgedSequenceNumber, SequenceNumber);
            string AcknowledgmentMessage = $"ACK|{m_LastAcknowledgedSequenceNumber}";
            byte[] AcknowledgmentData = Encoding.ASCII.GetBytes(AcknowledgmentMessage);

            await m_Sock.SendToAsync(new ArraySegment<byte>(AcknowledgmentData), SocketFlags.None, SenderEndPoint);
        }

        /// <summary>
        /// Packets that have been sent but not yet acknowledged.
        /// </summary>
        internal ConcurrentDictionary<int, (byte[] PacketData, DateTime SentTime, int NumRetransmissions)> m_SentPackets =
            new ConcurrentDictionary<int, (byte[], DateTime, int)>();

        private int m_SequenceNumber = 0;

        /// <summary>
        /// The last acknowledged sequence number.
        /// </summary>
        internal int m_LastAcknowledgedSequenceNumber = 0;

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

            if (Data.Length > m_MaxDatagramSize)
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
                if (m_ReceiveFromCTS.IsCancellationRequested)
                    break;

                try
                {
                    //Receive the data and store the sender's endpoint
                    SocketReceiveFromResult Result = await m_Sock.ReceiveFromAsync(new ArraySegment<byte>(ReceiveBuffer),
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
                catch (SocketException Ex)
                {
                    Logger.Log("Error receiving data: " + Ex.Message, LogLevel.error);
                    OnNetworkError?.Invoke(Ex);
                }
                catch (Exception Ex)
                {
                    Logger.Log("Error receiving data: " + Ex.Message, LogLevel.error); ;
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
            if (SenderEndPoint == null)
                throw new ArgumentNullException("SenderEndPoint", "SenderEndPoint cannot be null.");
            if (ReceivedData == null)
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

                if ((m_LengthOfCurrentFragPacket > MaxDatagramSize) || ((m_LengthOfCurrentFragPacket + ReceivedData.Length) >
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
            if (Data == null)
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
                    if (!IsCompressed)
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

        /// <summary>
        /// Returns a unique hash code for this NetworkClient instance.
        /// Needs to be implemented for this class to be usable in a 
        /// Dictionary.
        /// </summary>
        /// <returns>A unique hash code.</returns>
        public override int GetHashCode()
        {
            return SessionID.GetHashCode();
        }

        /// <summary>
        /// Finalizer for UDPNetworkClient.
        /// </summary>
        ~UDPNetworkClient()
        {
            Dispose(false);
        }

        /// <summary>
        /// Disposes of the resources used by this UDPNetworkClient instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Disposes of the resources used by this UDPNetworkClient instance.
        /// <param name="Disposed">Was this resource disposed explicitly?</param>
        /// </summary>
        protected virtual void Dispose(bool Disposed)
        {
            if (Disposed)
            {
                if (m_Sock != null)
                    m_Sock.Dispose();

                // Prevent the finalizer from calling ~UDPNetworkClient, since the object is already disposed at this point.
                GC.SuppressFinalize(this);
            }
            else
                Logger.Log("UDPNetworkClient not explicitly disposed!", LogLevel.error);
        }

        /// <summary>
        /// Disposes of the async resources used by this UDPNetworkClient instance.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            m_ResendUnackedCTS.Cancel();
            m_ReceiveFromCTS.Cancel();
        }
    }
}
