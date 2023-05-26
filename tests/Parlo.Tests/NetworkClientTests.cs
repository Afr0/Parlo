using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parlo.Packets;
using Parlo;
using System.Net;
using Moq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace Parlo.Tests
{
    /// <summary>
    /// Internal class used for testing the <see cref="NetworkClient"/> class.
    /// Exposes some of the protected members of the class.
    /// </summary>
    internal class TestClient : NetworkClient
    {
        public TestClient(ISocket Socket) : base(Socket)
        {
        }

        /// <summary>
        /// Gets or sets the number of seconds before a packet is 
        /// considered to be timed out and the ack is resent.
        /// </summary>
        public int UDPTimeout
        {
            get { return m_UDPTimeout; }
            set { m_UDPTimeout = value; }
        }

        /// <summary>
        /// Gets or sets the number of times a packet can be 
        /// resent before the connection is considered lost.
        /// </summary>
        public int MaxResends
        {
            get { return m_MaxResends; }
            set { m_MaxResends = value; }
        }

        /// <summary>
        /// Gets the <see cref="ConcurrentDictionary{TKey, TValue}"/>
        /// used to hold the packets that have been sent.
        /// </summary>
        public ConcurrentDictionary<int, (byte[], DateTime, int)> SentPackets
        {
            get { return m_SentPackets; }
        }

        /// <summary>
        /// Gets a CancellationTokenSource that can be set to 
        /// cancel the ResendTimedOutPacketsAsync() task.
        /// </summary>
        public CancellationTokenSource ResendUnackedCTS
        {
            get { return m_ResendUnackedCTS; }
        }

        /// <summary>
        /// CancellationTokenSource that can be set to
        /// cancel the ReceiveFromAsync() task.
        /// </summary>
        public CancellationTokenSource ReceiveFromCTS
        {
            get { return m_ReceiveFromCTS; }
        }


        /// <summary>
        /// Gets or sets the last sequence number that was acknowledged by the other party.
        /// </summary>
        public int LastAckedSequenceNumber
        {
            get { return m_LastAcknowledgedSequenceNumber; }
            set { m_LastAcknowledgedSequenceNumber = value; }
        }

        /// <summary>
        /// Resends packets that have not been acknowledged by the other party.
        /// </summary>
        /// <returns>An awaitable task.</returns>
        public async Task ResendTimedoutPacketsAsync()
        {
            await ResendTimedOutPacketsAsync();
        }

        /// <summary>
        /// Sends an acknowledgement to the other party.
        /// </summary>
        /// <param name="Endpoint">THe endpoint of the other party.</param>
        /// <param name="SequenceNumber">The sequence number of the packet to acknowledge.</param>
        /// <returns>An awaitable task.</returns>
        public async Task SendAckAsync(IPEndPoint Endpoint, int SequenceNumber)
        {
            await SendAcknowledgementAsync(Endpoint, SequenceNumber);
        }
    }

    [TestClass]
    public class NetworkClientTests
    {
        private bool m_MockSocketConnected = false;

        /// <summary>
        /// Tests whether the <see cref="NetworkClient"/> can receive a heartbeat.
        /// </summary>
        [TestMethod]
        public async Task Test_HeartbeatReceived()
        {
            //Arrange
            Mock<ISocket> MockSocket = new Mock<ISocket>();
            MockSocket.SetupGet(y => y.SockType).Returns(SocketType.Stream);
            NetworkClient Client = new NetworkClient(MockSocket.Object);
            TaskCompletionSource<bool> HeartbeatReceivedTcs = new TaskCompletionSource<bool>();
            bool HeartbeatReceived = false;

            HeartbeatPacket Heartbeat = new HeartbeatPacket(TimeSpan.FromSeconds(30));
            byte[] HeartbeatData = Heartbeat.ToByteArray();
            Packet Pulse = new Packet((byte)ParloIDs.Heartbeat, HeartbeatData, false);
            byte[] PulseData = Pulse.BuildPacket();

            MockSocket.SetupGet(x => x.Connected).Returns(() => m_MockSocketConnected);
            MockSocket.Setup(x => x.ConnectAsync(It.IsAny<string>(), It.IsAny<int>()))
                .Returns(Task.CompletedTask)
                .Callback(() => m_MockSocketConnected = true);
            MockSocket.Setup(x => x.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<SocketFlags>()))
                      .Returns(Task.FromResult(PulseData.Length));

            MockSocket.Setup(x => x.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<SocketFlags>()))
                .ReturnsAsync(PulseData.Length)
                .Callback<ArraySegment<byte>, SocketFlags>((Buffer, Flags) =>
                {
                    if(Buffer.Array != null)
                        Array.Copy(PulseData, 0, Buffer.Array, Buffer.Offset, PulseData.Length);
                });

            Client.OnReceivedHeartbeat += (Client) =>
            {
                HeartbeatReceived = true;
                HeartbeatReceivedTcs.SetResult(true);
            };

            LoginArgsContainer LoginArgs = new LoginArgsContainer();
            LoginArgs.Address = "127.0.0.1";
            LoginArgs.Password = "8080";

            //Act
            await Client.ConnectAsync(LoginArgs);
            await HeartbeatReceivedTcs.Task; // Wait for the heartbeat to be received

            //Assert
            Assert.IsTrue(HeartbeatReceived);

            //Cleanup
            Client.Dispose();
        }

        /// <summary>
        /// Tests wether the Listener detects a missed heartbeat.
        /// </summary>
        [TestMethod]
        public async Task Test_MissedHeartbeat()
        {
            //Arrange
            Mock<ISocket> MockServerSocket = new Mock<ISocket>();
            MockServerSocket.SetupGet(y => y.SockType).Returns(SocketType.Stream);
            Listener Server = new Listener(MockServerSocket.Object);
            bool MissedHeartbeatDetected = false;

            Server.OnDisconnected += (Sender) =>
            {
                MissedHeartbeatDetected = true;
                return Task.CompletedTask;
            };

            using (CancellationTokenSource Cts = new CancellationTokenSource())
            {
                MockServerSocket.Setup(x => x.AcceptAsync()).Returns(() =>
                {
                    Cts.Cancel(); //Cancel the task so that the listener will stop listening.
                    Mock<ISocket> AcceptedSocket = new Mock<ISocket>();
                    AcceptedSocket.SetupGet(y => y.SockType).Returns(SocketType.Stream);

                    AcceptedSocket.Setup(y => y.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<SocketFlags>()))
                    .ReturnsAsync(0); //Return 0 bytes because it shouldn't matter for this test.

                    return Task.FromResult(AcceptedSocket.Object);
                });

                MockServerSocket.Setup(x => x.Bind(It.IsAny<IPEndPoint>())).Callback(() => { });
                MockServerSocket.Setup(x => x.Listen(It.IsAny<int>())).Callback(() => { });

                //Act
                Task ServerTask = Server.InitializeAsync(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8080), 1024, Cts);

                await ServerTask;

                //Heartbeat interval on the accepted socket is 5 seconds.
                await Task.Delay(TimeSpan.FromSeconds(5)); // Wait for heartbeat packets to be exchanged
            }

            //Assert
            Assert.IsTrue(MissedHeartbeatDetected);

            //Cleanup
            Server.Dispose();
        }

        /// <summary>
        /// Tests whether timed out packets are resent.
        /// </summary>
        [TestMethod]
        public async Task TestResendTimedOutPacketsAsync()
        {
            //Arrange
            Mock<ISocket> MockSocket = new Mock<ISocket>();
            MockSocket.SetupGet(y => y.SockType).Returns(SocketType.Dgram);
            MockSocket.SetupGet(y => y.AFamily).Returns(AddressFamily.InterNetwork);
            MockSocket.SetupGet(y => y.Address).Returns("127.0.0.1");
            MockSocket.SetupGet(y => y.Port).Returns(8080);
            UDPNetworkClient Client = null;

            MockSocket.Setup(s => s.SendToAsync(It.IsAny<ArraySegment<byte>>(), SocketFlags.None, It.IsAny<EndPoint>()))
                .ReturnsAsync(1);
            MockSocket.Setup(s => s.ReceiveFromAsync(It.IsAny<ArraySegment<byte>>(), SocketFlags.None, It.IsAny<EndPoint>()))
                .ReturnsAsync(new SocketReceiveFromResult() { ReceivedBytes = 9, RemoteEndPoint = IPEndPoint.Parse("127.0.0.1") });

            Client = new UDPNetworkClient(MockSocket.Object);
            Client.UDPTimeout = 1;
            Client.MaxResends = 2;
            Client.m_SentPackets.TryAdd(1, (new byte[] { 1, 2, 3 }, DateTime.UtcNow.AddSeconds(-2), 1));

            //Act
            Task ResendTask = Client.ResendTimedOutPacketsAsync();
            await Task.Delay(2000);
            Client.m_ResendUnackedCTS.Cancel(); // Cancel the method

            //Assert

            //Verifies that SendToAsync() on the mock socket was called exactly once. If it was not,
            //the test fails. This confirms that ResendTimedoutPacketsAsync() did indeed attempt to resend
            //the packet.
            MockSocket.Verify(x => x.SendToAsync(It.IsAny<ArraySegment<byte>>(), SocketFlags.None, It.IsAny<EndPoint>()), 
                Times.Once()); 
            //Verifies that the count of sent packets in the client is zero.
            //This confirms that the packet was removed from the sent
            //packets list.
            Assert.AreEqual(0, Client.m_SentPackets.Count);
        }

        /// <summary>
        /// Tests whether the client can send an acknowledgement packet.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task TestSendAcknowledgementAsync()
        {
            //Arrange
            Mock<ISocket> MockSocket = new Mock<ISocket>();
            MockSocket.SetupGet(y => y.SockType).Returns(SocketType.Dgram);
            MockSocket.SetupGet(y => y.AFamily).Returns(AddressFamily.InterNetwork);
            MockSocket.SetupGet(y => y.Address).Returns("127.0.0.1");
            MockSocket.SetupGet(y => y.Port).Returns(8080);
            MockSocket.Setup(s => s.SendToAsync(It.IsAny<ArraySegment<byte>>(), SocketFlags.None, It.IsAny<EndPoint>()))
                .ReturnsAsync(1);

            UDPNetworkClient Client = new UDPNetworkClient(MockSocket.Object);
            Client.m_LastAcknowledgedSequenceNumber = 0;
            int TestSeqNum = 5;

            //Act
            await Client.SendAcknowledgementAsync(new IPEndPoint(IPAddress.Loopback, 5000), TestSeqNum);

            //Assert
            MockSocket.Verify(x => x.SendToAsync(It.IsAny<ArraySegment<byte>>(), SocketFlags.None, It.IsAny<EndPoint>()), 
                Times.Once());
            Assert.AreEqual(TestSeqNum, Client.m_LastAcknowledgedSequenceNumber);
        }
    }
}