/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using Parlo.Events;
using System;

namespace Parlo.Exceptions
{
    /// <summary>
    /// An exception that is thrown when a Packethandler already exists.
    /// </summary>
    public class PacketHandlerException : Exception
    {
        /// <summary>
        /// The error code for the exception.
        /// </summary>
        public EventCodes ErrorCode = EventCodes.PACKET_HANDLER_ALREADY_EXISTS;

        /// <summary>
        /// Constructs a new PacketHandlerException.
        /// </summary>
        /// <param name="Description">The description of the exception, optional.</param>
        public PacketHandlerException(string Description = "Packethandler already existed!")
            : base(Description)
        {

        }
    }
}
