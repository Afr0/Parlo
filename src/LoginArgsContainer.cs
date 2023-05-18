/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;

namespace Parlo
{
    /// <summary>
    /// Container for arguments supplied when logging in,
    /// to the OnConnected delegate in NetworkClient.cs.
    /// AT A MINIMUM, you need to provide an address and a
    /// port when connecting to a remote host.
    /// This acts as a base class that can be inherited
    /// from to accommodate more/different arguments.
    /// </summary>
    public class LoginArgsContainer
    {
        /// <summary>
        /// The address of the remote host to connect to.
        /// </summary>
        public string Address;

        /// <summary>
        /// The port of the remote host to connect to.
        /// </summary>
        public int Port;
        
        /// <summary>
        /// The client that is connecting.
        /// </summary>
        public NetworkClient Client;

        /// <summary>
        /// The username of the client that is connecting.
        /// This is optional.
        /// </summary>
        public string Username;

        /// <summary>
        /// The password of the client that is connecting.
        /// This is optional.
        /// </summary>
        public string Password;
    }
}
