/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using Parlo.Encryption;

namespace Parlo.Encryption
{
    /// <summary>
    /// Represents arguments needed to encrypt a packet.
    /// </summary>
    public class EncryptionArgs
    {
        /// <summary>
        /// The encryption mode to use.
        /// </summary>
        public EncryptionMode Mode;

        /// <summary>
        /// The key used for encryption.
        /// </summary>
        public string Key;

        /// <summary>
        /// The salt used for encryption.
        /// </summary>
        public string Salt;
    }
}
