/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s):
*/

namespace Parlo.Events
{
    /// <summary>
    /// Event codes for Parlo. Indicates the type of event that has occurred.
    /// </summary>
    public enum EventCodes
    {
        /// <summary>
        /// Received a faulty packet that couldn't be processed.
        /// </summary>
        PACKET_PROCESSING_ERROR = 0x01,

        /// <summary>
        /// Received a faulty packet that couldn't be decrypted.
        /// </summary>
        PACKET_DECRYPTION_ERROR = 0x02,

        /// <summary>
        /// The buffer overflowed when receiving data.
        /// </summary>
        BUFFER_OVERFLOW_ERROR = 0x03,

        /// <summary>
        /// A packet handler already exists.
        /// </summary>
        PACKET_HANDLER_ALREADY_EXISTS = 0x04
    }
}
