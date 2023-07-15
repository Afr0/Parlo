/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System.Threading.Tasks;
using Parlo.Packets;

namespace Parlo
{
    /// <summary>
    /// A delegate for a when a Packet instance is received.
    /// </summary>
    /// <param name="Client">The <see cref="NetworkClient"/> that received the packet.</param>
    /// <param name="P">The <see cref="Packet"/> that was received.</param>
    public delegate Task OnPacketReceived(NetworkClient Client, Packet P);

    /// <summary>
    /// A handler for a ProcessedPacket instance.
    /// </summary>
    public class PacketHandler
    {
        private byte m_ID;
        private bool m_Encrypted;
        private OnPacketReceived m_Handler;

        /// <summary>
        /// Constructs a new PacketHandler instance.
        /// </summary>
        /// <param name="id">The ID of the ProcessedPacket instance to handle.</param>
        /// <param name="Encrypted">Is the ProcessedPacket instance encrypted?</param>
        /// <param name="handler">A OnPacketReceive instance.</param>
        public PacketHandler(byte id, bool Encrypted, OnPacketReceived handler)
        {
            this.m_ID = id;
            this.m_Handler = handler;
            this.m_Encrypted = Encrypted;
        }

        /// <summary>
        /// The ID of the ProcessedPacket instance to handle.
        /// </summary>
        public byte ID
        {
            get { return m_ID; }
        }

        /// <summary>
        /// Is the ProcessedPacket instance encrypted?
        /// </summary>
        public bool Encrypted
        {
            get { return m_Encrypted; }
        }

        /// <summary>
        /// A OnPacketReceive instance.
        /// </summary>
        public OnPacketReceived Handler
        {
            get
            {
                return m_Handler;
            }
        }
    }
}
