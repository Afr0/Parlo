/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Parlo
{
    /// <summary>
    /// An interface for a socket.
    /// Used to enable mocking of sockets in unit tests.
    /// </summary>
    public interface ISocket
    {
        /// <summary>
        /// Associates a socket with a local endpoint.
        /// </summary>
        /// <param name="LocalEP">The locaL endpoint to associate with the <see cref="ParloSocket"/></param>
        void Bind(EndPoint LocalEP);

        /// <summary>
        /// Places a socket in a listening state.
        /// </summary>
        /// <param name="Backlog">The maximum length of the pending connections queue.</param>
        void Listen(int Backlog);

        /// <summary>
        /// Performs an asynchronous operation on a <see cref="ParloSocket"/> 
        /// to accept an incoming connection attempt on the socket.
        /// </summary>
        /// <returns>An asynchronous task that completes with a <see cref="Socket"/> 
        /// to handle communication with the remote host.</returns>
        public Task<ISocket> AcceptAsync();

        /// <summary>
        /// Establishes a connection to a remote host.
        /// The host is specified by an IP address and 
        /// a port number.
        /// </summary>
        /// <param name="Address">The IP address of the remote host.</param>
        /// <param name="Port">The port number of the remote host.</param>
        /// <returns>An asynchronous task.</returns>
        Task ConnectAsync(string Address, int Port);


        /// <summary>
        /// Sends data asynchronously to a specific remote host.
        /// </summary>
        /// <param name="Data">An array that contains the data to send.</param>
        /// <param name="Flags">A bitwise combination of the SocketFlags values.</param>
        /// <param name="RemoteEP">An endpoint that represents the remote device.</param>
        /// <returns>An asynchronous task that completes with number of bytes sent if the operation was successful.
        /// Otherwise, the task will complete with an invalid socket error.</returns>
        public Task<int> SendToAsync(ArraySegment<byte> Data, SocketFlags Flags, EndPoint RemoteEP);

        /// <summary>
        /// Receives data from a specified network device.
        /// </summary>
        /// <param name="Data">An array of type Byte that is the storage location for the received data.</param>
        /// <param name="Flags">A bitwise combination of the SocketFlags values.</param>
        /// <param name="RemoteEP">An endpoint that represents the source of the data.</param>
        /// <returns>An asynchronous <see cref="Task"/> that completes with a <see cref="SocketReceiveFromResult"/> struct.</returns>
        public Task<SocketReceiveFromResult> ReceiveFromAsync(ArraySegment<byte> Data, SocketFlags Flags, EndPoint RemoteEP);

        /// <summary>
        /// Sends data to a connected socket.
        /// </summary>
        /// <param name="Data">An array of type byte that contains the data to send.</param>
        /// <param name="SocketFlags">The flags to use when sending.</param>
        /// <returns>An asynchronous task that completes with the number of bytes sent if the operation was successful.
        /// Otherwise, it will complete with an invalid socket error.</returns>
        Task<int> SendAsync(ArraySegment<byte> Data, SocketFlags SocketFlags);

        /// <summary>
        /// Receives data from a connected socket.
        /// </summary>
        /// <param name="Data">An array of type byte that contains the data to send.</param>
        /// <param name="Flags">A bitwise combination of the SocketFlags values.</param>
        /// <returns>A task that represents the asynchronous receive operation. The value 
        /// of the TResult parameter contains the number of bytes received.</returns>
        Task<int> ReceiveAsync(ArraySegment<byte> Data, SocketFlags Flags);

        /// <summary>
        /// Sets the specified Socket option to the specified bool value.
        /// </summary>
        /// <param name="Level">One of the SocletOptionLevel values.</param>
        /// <param name="Name">One of the SocketOptionName values.</param>
        /// <param name="Value">The value of the option, represented as a bool.</param>
        public void SetSocketOption(SocketOptionLevel Level, SocketOptionName Name, bool Value);

        /// <summary>
        /// Disables sends and receives on a <see cref="Socket"/>.
        /// </summary>
        /// <param name="How">One of the <see cref="SocketShutdown"/> values that specifies that the operation will 
        /// no longer be allowed.</param>
        void Shutdown(SocketShutdown How);

        /// <summary>
        /// Closes the socket connection and allows reuse of the socket.
        /// </summary>
        /// <param name="ReuseSocket">True if this socket can be reused after the connection is closed; otherwise false.</param>
        void Disconnect(bool ReuseSocket);

        /// <summary>
        /// Closes the <see cref="ParloSocket"/> connection and releases all associated resources.
        /// </summary>
        public void Close();

        /// <summary>
        /// Releases all resources used by the current instance of the ParloSocket class.
        /// </summary>
        public void Dispose();

        /// <summary>
        /// Thw address of the <see cref="ISocket"/>.
        /// </summary>
        public string Address { get; }

        /// <summary>
        /// The port of the <see cref="ISocket"/>.
        /// </summary>
        public int Port { get; }

        /// <summary>
        /// Gets a value that indicates whether a <see cref="ISocket"/> is connected to a remote 
        /// host as of the last SendAsync or ReceiveAsync operation.
        /// </summary>
        public bool Connected { get; }

        /// <summary>
        /// The <see cref="EndPoint"/> with which the <see cref="ISocket"/> is communicating.
        /// </summary>
        public EndPoint RemoteEndPoint { get ; }

        /// <summary>
        /// Gets the address family of the <see cref="ISocket"/>.
        /// </summary>
        public AddressFamily AFamily { get; }

        /// <summary>
        /// Gets the type of the <see cref="ISocket"/>
        /// </summary>
        public SocketType SockType { get; }

        /// <summary>
        /// Gets or sets a value that specifies whether the <see cref="ISocket"/>
        /// will delay closing a socket in an attempt to send all pending data.
        /// </summary>
        public LingerOption LingerState { get; set; }
    }
}
