/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;
using System.Collections.Concurrent;
using Parlo.Exceptions;
using Parlo.Packets;

namespace Parlo
{
    /// <summary>
    /// Framework for registering packet handlers with GonzoNet.
    /// </summary>
    public class PacketHandlers
    {
        /**
         * Framework
         */
        private static ConcurrentDictionary<byte, PacketHandler> m_Handlers = new ConcurrentDictionary<byte, PacketHandler>();

        /// <summary>
        /// Registers a PacketHandler with Parlo.
        /// The IDs 0xFE (254) and 0xFF (255) are reserved
        /// for use by Parlo, so can't be used.
        /// </summary>
        /// <param name="ID">The ID of the packet.</param>
        /// <param name="Encrypted">Is the packet encrypted?</param>
        /// <param name="Handler">The handler for the packet.</param>
        public static void Register(byte ID, bool Encrypted,  OnPacketReceived Handler)
        {
            if(ID == (byte)ParloIDs.CGoodbye || ID == (byte)ParloIDs.SGoodbye)
                throw new PacketHandlerException("Tried to register reserved handler!"); //Handler is internal.

            if (!m_Handlers.TryAdd(ID, new PacketHandler(ID, Encrypted, Handler)))
                throw new PacketHandlerException(); //Handler already existed.
        }

        /// <summary>
        /// Gets a handler based on its ID.
        /// </summary>
        /// <param name="ID">The ID of the handler.</param>
        /// <returns>The handler with the specified ID, or null if the handler didn't exist.</returns>
        public static PacketHandler Get(byte ID)
        {
            if (m_Handlers.ContainsKey(ID))
                return m_Handlers[ID];
            else return null;
        }
    }
}
