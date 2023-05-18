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
using System.Linq;
using System.Text;

namespace Parlo
{
    /// <summary>
    /// Size of packet headers.
    /// </summary>
    public enum PacketHeaders
    {
        /// <summary>
        /// Size of a TCP packet header.
        /// </summary>
        STANDARD = 4,

        /// <summary>
        /// Size of a UDP packet header.
        /// </summary>
        UDP = 5
    }
}
