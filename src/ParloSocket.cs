/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Parlo
{
    /// <summary>
    /// A wrapper for the Socket class that implements ISocket.
    /// This class exists to enable dependency injection for unit tests.
    /// Supports the TCP and UDP protocols.
    /// </summary>
    public class ParloSocket : ISocket
    {
        private readonly SemaphoreSlim m_SocketSemaphore;

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

        private readonly Socket m_Socket;
        private string m_Address = "127.0.0.1";
        private int m_Port = 8080;

        /// <summary>
        /// The address of the <see cref="ParloSocket"/>.
        /// Defaults to 127.0.0.1 until <see cref="ConnectAsync(string, int)"/> or <see cref="Bind(EndPoint)"/> is called.
        /// </summary>
        public string Address
        {
            get { return m_Address; }
        }

        /// <summary>
        /// The port of the <see cref="ParloSocket"/>.
        /// Defaults to 8080 until <see cref="ConnectAsync(string, int)"/> or <see cref="Bind(EndPoint)"/> is called.
        /// </summary>
        public int Port
        {
            get { return m_Port; }
        }

        /// <summary>
        /// Gets or sets a value that specifies whether the <see cref="ParloSocket"/>
        /// will delay closing a socket in an attempt to send all pending data.
        /// </summary>
        public LingerOption LingerState
        {
            get { return m_Socket.LingerState; }
            set { m_Socket.LingerState = value; }
        }

        /// <summary>
        /// The <see cref="EndPoint"/> with which the <see cref="ParloSocket"/> is communicating.
        /// </summary>
        public EndPoint RemoteEndPoint
        {
            get 
            {
                EndPoint EP;

                m_SocketSemaphore.Wait();

                try
                {
                    EP = m_Socket.RemoteEndPoint;
                }
                finally
                {
                    m_SocketSemaphore.Release();
                }

                return EP; 
            }
        }

        /// <summary>
        /// Gets the address family of the <see cref="ParloSocket"/>.
        /// </summary>
        public AddressFamily AFamily
        {
            get
            {
                AddressFamily AF;

                m_SocketSemaphore.Wait();

                try
                {
                    AF = m_Socket.AddressFamily;
                }
                finally
                {
                    m_SocketSemaphore.Release();
                }

                return AF;
            }
        }

        /// <summary>
        /// Gets the type of the <see cref="ParloSocket"/>
        /// </summary>
        public SocketType SockType
        {
            get
            {
                SocketType ST;

                m_SocketSemaphore.Wait();

                try
                {
                    ST = m_Socket.SocketType;
                }
                finally
                {
                    m_SocketSemaphore.Release();
                }

                return ST;
            }
        }

        /// <summary>
        /// Creates a new ParloSocket.
        /// Only supports IPv4 or IPv6.
        /// </summary>
        /// <param name="KeepAlive">Should the connection be kept alive?</param>
        /// <param name="AFamily">Specifies the addressing scheme that an instance of <see cref="ParloSocket"/> can use.</param>
        /// <param name="SType">Specifies the type of socket that an instance of the <see cref="ParloSocket"/> represents.</param>
        /// <param name="PType">Specifies the protocol that the <see cref="ParloSocket"/> supports. <see cref="ParloSocket"/> 
        /// currently only supports TCP and UDP.</param>
        /// <exception cref="ArgumentException">Thrown if the AddressFamily isn't InterNetwork or InterNetworkV6.</exception>
        /// <exception cref="ArgumentException">Thrown if the ProtocolType of the socket isn't TCP or UDP.</exception>
        /// <exception cref="ArgumentException">Thrown if the SocketType of the socket isn't Stream or Dgram</exception>
        public ParloSocket(bool KeepAlive, AddressFamily AFamily = AddressFamily.InterNetwork,
            SocketType SType = SocketType.Stream, ProtocolType PType = ProtocolType.Tcp)
        {
            if (!(AFamily == AddressFamily.InterNetwork || AFamily == AddressFamily.InterNetworkV6))
                throw new ArgumentException("AFamily must be either InterNetwork or InterNetworkV6!");

            if (!(PType == ProtocolType.Tcp || PType == ProtocolType.Udp))
                throw new ArgumentException("Unsupported ProtocolType!");

            if (!(SType == SocketType.Stream || SType == SocketType.Dgram))
                throw new ArgumentException("Unsupported SocketType!");

            m_NumberOfCores = ProcessorInfo.GetPhysicalCoreCount();

            m_SocketSemaphore = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);

            m_Socket = new Socket(AFamily, SType, PType);

            if (KeepAlive)
                m_Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }

        /// <summary>
        /// Creates a new <see cref="ParloSocket"/>.
        /// </summary>
        /// <param name="Sock">The socket from which to create this <see cref="ParloSocket"/></param>
        /// <exception cref="ArgumentException">Thrown if the AddressFamily of the socket isn't InterNetwork or InternetworkV6.</exception>
        /// <exception cref="ArgumentException">Thrown if the ProtocolType of the socket isn't TCP or UDP.</exception>
        public ParloSocket(Socket Sock)
        {
            if (!(Sock.AddressFamily == AddressFamily.InterNetwork || Sock.AddressFamily == AddressFamily.InterNetworkV6))
                throw new ArgumentException("AFamily must be either InterNetwork or InterNetworkV6!");

            if (!(Sock.ProtocolType == ProtocolType.Tcp || Sock.ProtocolType == ProtocolType.Udp))
                throw new ArgumentException("Unsupported ProtocolType!");

            if (!(Sock.SocketType == SocketType.Stream || Sock.SocketType == SocketType.Dgram))
                throw new ArgumentException("Unsupported SocketType!");

            m_NumberOfCores = ProcessorInfo.GetPhysicalCoreCount();

            m_SocketSemaphore = new SemaphoreSlim((NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors,
                (NumberOfCores > NumberOfLogicalProcessors) ? NumberOfCores : NumberOfLogicalProcessors);

            m_Socket = Sock;
        }

        /// <summary>
        /// Gets a value that indicates whether a <see cref="ParloSocket"/> is connected to a remote 
        /// host as of the last SendAsync or ReceiveAsync operation.
        /// </summary>
        public bool Connected
        {
            get
            {
                m_SocketSemaphore.Wait();
                bool IsConnected = m_Socket.Connected;
                m_SocketSemaphore.Release();

                return IsConnected;
            }
        }

        /// <summary>
        /// Associates a socket with a local endpoint.
        /// </summary>
        /// <param name="LocalEP">The locaL endpoint to associate with the <see cref="ParloSocket"/></param>
        public void Bind(EndPoint LocalEP)
        {
            m_SocketSemaphore.Wait();

            try
            {
                m_Address = ((IPEndPoint)LocalEP).Address.ToString();
                m_Port = ((IPEndPoint)LocalEP).Port;

                m_Socket.Bind(LocalEP);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }
        }

        /// <summary>
        /// Places a <see cref="ParloSocket"/> in a listening state.
        /// </summary>
        /// <param name="Backlog">The maximum length of the pending connections queue.</param>
        public void Listen(int Backlog)
        {
            m_SocketSemaphore.Wait();

            try
            {
                m_Socket.Listen(Backlog);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }
        }

        /// <summary>
        /// Performs an asynchronous operation on a <see cref="ParloSocket"/> 
        /// to accept an incoming connection attempt on the socket.
        /// </summary>
        /// <returns>An asynchronous task that completes with a <see cref="Socket"/> 
        /// to handle communication with the remote host.</returns>
        public async Task<ISocket> AcceptAsync()
        {
            Socket AcceptedSocket;

            await m_SocketSemaphore.WaitAsync();
            try
            {
                AcceptedSocket = await m_Socket.AcceptAsync();
            }
            finally
            {
                m_SocketSemaphore.Release();
            }

            return new ParloSocket(AcceptedSocket);
        }

        /// <summary>
        /// Establishes a connection to a remote host.
        /// The host is specified by an IP address and 
        /// a port number.
        /// </summary>
        /// <param name="Address">The IP address of the remote host.</param>
        /// <param name="Port">The port number of the remote host.</param>
        /// <returns>An asynchronous task.</returns>
        public async Task ConnectAsync(string Address, int Port)
        {
            m_Address = Address;
            m_Port = Port;

            await m_SocketSemaphore.WaitAsync();
            try
            {
                await m_Socket.ConnectAsync(Address, Port);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets the specified Socket option to the specified bool value.
        /// </summary>
        /// <param name="Level">One of the SocletOptionLevel values.</param>
        /// <param name="Name">One of the SocketOptionName values.</param>
        /// <param name="Value">The value of the option, represented as a bool.</param>
        public void SetSocketOption(SocketOptionLevel Level, SocketOptionName Name, bool Value)
        {
            m_SocketSemaphore.Wait();

            try
            {
                m_Socket.SetSocketOption(Level, Name, Value);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }
        }

        /// <summary>
        /// Sends data asynchronously to a specific remote host.
        /// </summary>
        /// <param name="Data">An array that contains the data to send.</param>
        /// <param name="Flags">A bitwise combination of the SocketFlags values.</param>
        /// <param name="RemoteEP">An endpoint that represents the remote device.</param>
        /// <returns>An asynchronous task that completes with number of bytes sent if the operation was successful.
        /// Otherwise, the task will complete with an invalid socket error.</returns>
        public async Task<int> SendToAsync(ArraySegment<byte> Data, SocketFlags Flags, EndPoint RemoteEP)
        {
            int Sent = 0;

            await m_SocketSemaphore.WaitAsync();

            try
            {
                Sent = await m_Socket.SendToAsync(Data, Flags, RemoteEP);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }

            return Sent;
        }

        /// <summary>
        /// Receives data from a specified network device.
        /// </summary>
        /// <param name="Data">An array of type Byte that is the storage location for the received data.</param>
        /// <param name="Flags">A bitwise combination of the SocketFlags values.</param>
        /// <param name="RemoteEP">An endpoint that represents the source of the data.</param>
        /// <returns>An asynchronous <see cref="Task"/> that completes with a <see cref="SocketReceiveFromResult"/> struct.</returns>
        public async Task<SocketReceiveFromResult> ReceiveFromAsync(ArraySegment<byte> Data, SocketFlags Flags, EndPoint RemoteEP)
        {
            SocketReceiveFromResult Received;

            await m_SocketSemaphore.WaitAsync();

            try
            {
                Received = await m_Socket.ReceiveFromAsync(Data, Flags, RemoteEP);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }

            return Received;
        }

        /// <summary>
        /// Sends data to a connected socket.
        /// </summary>
        /// <param name="Data">An array of type byte that contains the data to send.</param>
        /// <param name="Flags">A bitwise combination of the SocketFlags values.</param>
        /// <returns>An awaitable task with the number of bytes received.
        /// Otherwise, it will complete with an invalid socket error.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Data"/> was null.</exception>
        public async Task<int> SendAsync(ArraySegment<byte> Data, SocketFlags Flags)
        {
            if(Data == null)
                throw new ArgumentNullException("Data");

            int Sent = 0;

            await m_SocketSemaphore.WaitAsync();

            try
            {
                Sent = await m_Socket.SendAsync(Data, Flags);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }

            return Sent;
        }

        /// <summary>
        /// Receives data from a connected socket.
        /// </summary>
        /// <param name="Data">An array of type byte that contains the data to send.</param>
        /// <param name="Flags">A bitwise combination of the SocketFlags values.</param>
        /// <returns>A task that represents the asynchronous receive operation. The value 
        /// of the TResult parameter contains the number of bytes received.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="Data"/> was null.</exception>
        public async Task<int> ReceiveAsync(ArraySegment<byte> Data, SocketFlags Flags)
        {
            if(Data == null)
                throw new ArgumentNullException("Data");

            int Received = 0;

            await m_SocketSemaphore.WaitAsync();

            try
            {
                Received = await m_Socket.ReceiveAsync(Data, Flags);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }

            return Received;
        }

        /// <summary>
        /// Disables sends and receives on a <see cref="Socket"/>.
        /// </summary>
        /// <param name="How">One of the <see cref="SocketShutdown"/> values that specifies that the operation will 
        /// no longer be allowed.</param>
        public void Shutdown(SocketShutdown How)
        {
            m_SocketSemaphore.Wait();

            try
            {
                m_Socket.Shutdown(How);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }
        }

        /// <summary>
        /// Closes the <see cref="ParloSocket"/> connection and releases all associated resources.
        /// </summary>
        public void Close()
        {
            m_SocketSemaphore.Wait();
            try
            {
                m_Socket.Close();
            }
            finally
            {
                m_SocketSemaphore.Release();
            }
        }

        /// <summary>
        /// Closes the socket connection and allows reuse of the socket.
        /// </summary>
        /// <param name="ReuseSocket">True if this socket can be reused after the connection is closed; otherwise false.</param>
        public void Disconnect(bool ReuseSocket)
        {
            m_SocketSemaphore.Wait();

            try
            {
                m_Socket.Disconnect(ReuseSocket);
            }
            finally
            {
                m_SocketSemaphore.Release();
            }
        }

        /// <summary>
        /// Releases all resources used by the current instance of the ParloSocket class.
        /// </summary>
        public void Dispose()
        {
            m_SocketSemaphore.Wait();
            try
            {
                m_Socket.Dispose();
            }
            finally
            {
                m_SocketSemaphore.Release();
            }
        }
    }
}