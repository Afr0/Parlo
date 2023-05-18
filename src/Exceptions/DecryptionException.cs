/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;
using Parlo.Events;

namespace Parlo.Exceptions
{
    /// <summary>
    /// Exception that occurs when a packet couldn't be decrypted.
    /// </summary>
    public class DecryptionException : Exception
    {
        /// <summary>
        /// The error code for this exception.
        /// </summary>
        public EventCodes ErrorCode = EventCodes.PACKET_DECRYPTION_ERROR;

        /// <summary>
        /// Creates a new DecryptionException.
        /// </summary>
        /// <param name="Description">The description of what happened.</param>
        public DecryptionException(string Description = "Couldn't decrypt packet!")
            : base(Description)
        {

        }
    }
}
