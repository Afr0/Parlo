using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parlo.Packets;
using Parlo;
using System.Net;
using Moq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Parlo.Tests
{
    internal class TestListener : Listener
    {
        public TestListener(ISocket Socket) : base(Socket)
        {
        }

        protected override async Task AcceptAsync()
        {
            while (true)
            {
                if (m_AcceptCTS.IsCancellationRequested)
                    break;

                ISocket AcceptedSocketInterface = await m_ListenerSock.AcceptAsync();

                if (AcceptedSocketInterface != null)
                {
                    Logger.Log("\nNew client connected!\r\n", LogLevel.info);

                    //Set max missed heartbeats to 0, for testing purposes.
                    NetworkClient NewClient = new NetworkClient(AcceptedSocketInterface, this, 5, 0, null, 
                        NewClient_OnConnectionLostWrapper);
                    NewClient.OnClientDisconnected += async (Client) => await NewClient_OnClientDisconnected(Client);
                    NewClient.OnConnectionLost += async (Client) => await NewClient_OnConnectionLost(Client);

                    m_NetworkClients.Add(NewClient);
                }
            }
        }

        /// <summary>
        /// A wrapper for the protected <see cref="Listener.NewClient_OnClientDisconnected(NetworkClient)"/> method.
        /// </summary>
        /// <param name="Sender"></param>
        protected void NewClient_OnConnectionLostWrapper(NetworkClient Sender)
        {
            //This will attempt to fire the listener's OnDisconnected event.
            _ = NewClient_OnConnectionLost(Sender);
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
            // Arrange
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

            // Act
            await Client.ConnectAsync(LoginArgs);
            await HeartbeatReceivedTcs.Task; // Wait for the heartbeat to be received

            // Assert
            Assert.IsTrue(HeartbeatReceived);

            // Cleanup
            Client.Dispose();
        }

        [TestMethod]
        public async Task Test_MissedHeartbeat()
        {
            // Arrange
            Mock<ISocket> MockServerSocket = new Mock<ISocket>();
            TestListener Server = new TestListener(MockServerSocket.Object);
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
                    Cts.Cancel();
                    Mock<ISocket> AcceptedSocket = new Mock<ISocket>();
                    AcceptedSocket.SetupGet(y => y.SockType).Returns(SocketType.Stream);

                    AcceptedSocket.Setup(y => y.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<SocketFlags>()))
                    .ReturnsAsync(0); //Return 0 bytes because it shouldn't matter for this test.

                    return Task.FromResult(AcceptedSocket.Object);
                });
                MockServerSocket.Setup(x => x.Bind(It.IsAny<IPEndPoint>())).Callback(() => { });
                MockServerSocket.Setup(x => x.Listen(It.IsAny<int>())).Callback(() => { });

                // Act
                Task ServerTask = Server.InitializeAsync(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8080), 1024, Cts);

                //await Task.WhenAll(ServerTask, ClientTask); // Wait for server and client initialization to complete
                await ServerTask;

                //Heartbeat interval on the accepted socket is 5 seconds.
                await Task.Delay(TimeSpan.FromSeconds(5)); // Wait for heartbeat packets to be exchanged
            }

            // Assert
            Assert.IsTrue(MissedHeartbeatDetected);

            // Cleanup
            Server.Dispose();
        }
    }
}