/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;
using System.IO;
using Parlo.Packets;
using System.Runtime.Serialization.Formatters.Binary;

namespace Parlo.Packets
{
    [Serializable]
    internal class HeartbeatPacket : IPacket
    {
        private TimeSpan m_TimeSinceLast;
        private DateTime m_SentTimestamp;

        /// <summary>
        /// The time since the last heartbeat packet was sent.
        /// </summary>
        public TimeSpan TimeSinceLast
        {
            get { return m_TimeSinceLast; }
        }

        /// <summary>
        /// The time the packet was sent.
        /// Initialized in the constructor.
        /// </summary>
        public DateTime SentTimestamp
        {
            get { return m_SentTimestamp; }
        }

        public HeartbeatPacket(TimeSpan TimeSinceLast)
        {
            m_TimeSinceLast = TimeSinceLast;
            m_SentTimestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Serializes this class to a byte array.
        /// From: https://gist.github.com/LorenzGit/2cd665b6b588a8bb75c1a53f4d6b240a
        /// </summary>
        /// <returns>A byte array representation of this class.</returns>
        public byte[] ToByteArray()
        {
            BinaryFormatter bf = new BinaryFormatter();
            using (var ms = new MemoryStream())
            {
                bf.Serialize(ms, this);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Deserializes this class from a byte array.
        /// From: https://gist.github.com/LorenzGit/2cd665b6b588a8bb75c1a53f4d6b240a
        /// </summary>
        /// <returns>A Heartbeat packet instance.</returns>
        public static HeartbeatPacket ByteArrayToObject(byte[] arrBytes)
        {
            using (var memStream = new MemoryStream())
            {
                var binForm = new BinaryFormatter();
                memStream.Write(arrBytes, 0, arrBytes.Length);
                memStream.Seek(0, SeekOrigin.Begin);
                var obj = binForm.Deserialize(memStream);
                return (HeartbeatPacket)obj;
            }
        }
    }
}
