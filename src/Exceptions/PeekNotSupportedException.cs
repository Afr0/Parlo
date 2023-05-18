/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s):
*/

using System;

namespace Parlo.Exceptions
{
    /// <summary>
    /// Thrown when trying to peek from a stream that doesn't support it.
    /// </summary>
    public class PeekNotSupportedException : Exception
    {
        /// <summary>
        /// Constructs a new PeekNotSupportedException instance.
        /// </summary>
        /// <param name="Description">The description of the exception.</param>
        public PeekNotSupportedException(string Description = "Peek not supported!")
            : base(Description)
        {
        }
    }
}
