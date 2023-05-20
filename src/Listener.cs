/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Parlo.Packets;

namespace Parlo
{
    /// <summary>
    /// Occurs when a client connected or disconnected from a Listener.
    /// </summary>
    /// <param name="Client">The NetworkClient instance that connected or disconnected.</param>
    public delegate Task ClientDisconnectedFromListenerDelegate(NetworkClient Client);

    /// <summary>
    /// Represents a listener that listens for incoming clients.
    /// </summary>
    public class Listener : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Internal list of <see cref="NetworkClient"/ instances connected to this Listener.>
        /// </summary>
        protected BlockingCollection<NetworkClient> m_NetworkClients;

        /// <summary>
        /// The <see cref="ISocket"/> used for listening and accepting clients.
        /// </summary>
        protected ISocket m_ListenerSock;
        private IPEndPoint m_LocalEP;

        /// <summary>
        /// The <see cref="CancellationTokenSource"/> that can be used for stopping the AcceptAsync() task.
        /// </summary>
        protected CancellationTokenSource m_AcceptCTS;

        /// <summary>
        /// Fired when a client disconnected, or when a client lost its connection.
        /// </summary>
        public event ClientDisconnectedFromListenerDelegate OnDisconnected;

        /// <summary>
        /// Fired when a client connected.
        /// </summary>
		public event ClientDisconnectedFromListenerDelegate OnConnected;

        /// <summary>
        /// All of the clients connected to this listener.
        /// </summary>
		public BlockingCollection<NetworkClient> Clients
        {
            get { return m_NetworkClients; }
        }

        /// <summary>
        /// Should this Listener adaptively apply compression to outgoing packets?
        /// Defaults to true.
        /// </summary>
        public bool ApplyCompresssion
        {
            get;
            set;
        } = true;

        /// <summary>
        /// The local endpoint that this listener is listening to.
        /// </summary>
        public IPEndPoint LocalEP
        {
            get { return m_LocalEP; }
        }

        /// <summary>
        /// Initializes a new instance of Listener.
        /// </summary>
        public Listener(ISocket Sock)
        {
            m_ListenerSock = Sock;
            m_NetworkClients = new BlockingCollection<NetworkClient>();
        }

        /// <summary>
        /// Starts listening for incoming clients on the specified endpoint if the socket
        /// was set to <see cref="SocketType.Stream"/>. 
        /// If the socket was set to <see cref="SocketType.Dgram"/>, the socket will be bound
        /// to the specified endpoint, but no listening will be done.
        /// </summary>
        /// <param name="LocalEP">The endpoint to listen on.</param>
        /// <param name="MaxPacketSize">Maximum packet size. Defaults to 1024 bytes.</param>
        /// <param name="AcceptCToken">Optional parameter that can be used to cancel the Accept() task.
        /// that starts running when calling this method. Used for testing.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="LocalEP"/> was null.</exception>
        /// <exception cref="ArgumentException">Thrown if the <see cref="ISocket"/> used to
        /// initialize this Listener wasn't set to <see cref="SocketType.Stream"/>.</exception>
        /// <exception cref="SocketException">Thrown if the socket used to construct this Listener couldn't
        /// Bind() to or Listen() on <paramref name="LocalEP"/>.</exception>
        public virtual async Task InitializeAsync(IPEndPoint LocalEP, int MaxPacketSize = 1024, 
            CancellationTokenSource AcceptCToken = default)
        {
            if(m_ListenerSock.SockType != SocketType.Stream && m_ListenerSock.SockType != SocketType.Dgram)
                throw new ArgumentException("Socket must be of type SocketType.Stream or SocketType.DGram!");

            m_LocalEP = LocalEP ?? throw new ArgumentNullException("LocalEP!");
            m_AcceptCTS = AcceptCToken ?? new CancellationTokenSource();

            if (MaxPacketSize != 1024)
                ProcessingBuffer.MAX_PACKET_SIZE = MaxPacketSize;

            try
            {
                m_ListenerSock.Bind(LocalEP);

                if(m_ListenerSock.SockType == SocketType.Stream)
                    m_ListenerSock.Listen(10000);
            }
            catch (SocketException E)
            {
                Logger.Log("Exception occured in Listener.InitializeAsync(): " + E.ToString(), LogLevel.error);
                throw E;
            }

            await AcceptAsync();
        }

        /// <summary>
        /// Asynchronously accepts clients.
        /// </summary>
        /// <returns>An awaitable task.</returns>
        protected virtual async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    if (m_AcceptCTS.IsCancellationRequested)
                        break;

                    ISocket AcceptedSocketInterface = await m_ListenerSock.AcceptAsync();
                    ParloSocket AcceptedSocket = (ParloSocket)AcceptedSocketInterface;

                    if (AcceptedSocket != null)
                    {
                        Logger.Log("\nNew client connected!\r\n", LogLevel.info);

                        AcceptedSocket.LingerState = new LingerOption(true, 5);
                        NetworkClient NewClient = new NetworkClient(AcceptedSocket, this, ProcessingBuffer.MAX_PACKET_SIZE);
                        NewClient.OnClientDisconnected += async (Client) => await NewClient_OnClientDisconnected(Client);
                        NewClient.OnConnectionLost += async (Client) => await NewClient_OnConnectionLost(Client);

                        if (!ApplyCompresssion)
                            NewClient.ApplyCompresssion = false;

                        m_NetworkClients.Add(NewClient);
                        if (OnConnected != null)
                            await OnConnected(NewClient);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                Logger.Log("AcceptAsync task cancelled", LogLevel.info);
            }
        }

        /// <summary>
        /// The client missed too many heartbeats, so assume it's disconnected.
        /// </summary>
        /// <param name="Sender">The client in question.</param>
        protected async Task NewClient_OnConnectionLost(NetworkClient Sender)
        {
            Logger.Log("Client connection lost!", LogLevel.info);
            OnDisconnected?.Invoke(Sender);
            m_NetworkClients.TryTake(out Sender);
            await Sender.DisposeAsync();
            Sender.Dispose();
        }

        /// <summary>
        /// Gets a connected client based on its remote IP and remote port.
        /// </summary>
        /// <param name="RemoteIP">The remote IP of the client.</param>
        /// <param name="RemotePort">The remote port of the client.</param>
        /// <returns>A NetworkClient instance. Null if not found.</returns>
        public NetworkClient GetClient(string RemoteIP, int RemotePort)
        {
            if (RemoteIP == null)
                throw new ArgumentNullException("RemoteIP!");
            if (RemoteIP == string.Empty)
                throw new ArgumentException("RemoteIP must be specified!");

            lock (Clients)
            {
                foreach (NetworkClient PlayersClient in Clients)
                {
                    if (RemoteIP.Equals(PlayersClient.RemoteIP, StringComparison.CurrentCultureIgnoreCase))
                    {
                        if (RemotePort == PlayersClient.RemotePort)
                            return PlayersClient;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// A connected client disconnected from the Listener.
        /// </summary>
        /// <param name="Sender">The NetworkClient instance that disconnected.</param>
        protected async Task NewClient_OnClientDisconnected(NetworkClient Sender)
        {
            Logger.Log("Client disconnected!", LogLevel.info);
            await Sender.DisposeAsync();
            Sender.Dispose();
        }

        /// <summary>
        /// The number of clients that are connected to this Listener instance.
        /// </summary>
        public int NumConnectedClients
        {
            get { return m_NetworkClients.Count; }
        }

        /// <summary>
        /// Deconstructs this instance.
        /// </summary>
        ~Listener()
        {
            Dispose(false);
        }

        /// <summary>
        /// Disposes of the resources used by this Listener instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Disposes of the resources used by this Listener instance.
        /// <param name="Disposed">Was this resource disposed explicitly?</param>
        /// </summary>
        protected virtual void Dispose(bool Disposed)
        {
            if (Disposed)
            {
                if (m_ListenerSock != null)
                {
                    // After all clients have disconnected, shutdown the listener socket.
                    m_ListenerSock.Shutdown(SocketShutdown.Both);
                    m_ListenerSock.Close();
                    m_ListenerSock.Dispose();
                }

                if (m_NetworkClients != null)
                    m_NetworkClients.Dispose();

                // Prevent the finalizer from calling ~Listener, since the object is already disposed at this point.
                GC.SuppressFinalize(this);
            }
            else
                Logger.Log("Listener not explicitly disposed!", LogLevel.error);
        }

        /// <summary>
        /// Disposes of the async resources used by this Listener instance.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            // First, we wait for all clients to disconnect.
            var DisconnectTasks = new List<Task>();
            foreach (NetworkClient Client in m_NetworkClients)
                DisconnectTasks.Add(Client.DisconnectAsync());

            if (DisconnectTasks.Count > 0)
            {
                await Task.WhenAll(DisconnectTasks);
                await Task.Delay(TimeSpan.FromSeconds((double)ParloDefaultTimeouts.Server));
            }

            m_AcceptCTS.Cancel();

            // Dispose of the clients.
            foreach (NetworkClient Client in m_NetworkClients)
            {
                Client.Dispose();
                await Client.DisposeAsync();
            }
        }
    }
}