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
using System.Runtime.Serialization.Formatters.Binary;

namespace Parlo.Packets
{
    /// <summary>
    /// IDs used by internal packets.
    /// These IDs are reserved, I.E
    /// should not be used by a protocol.
    /// </summary>
    public enum ParloIDs
    {
        /// <summary>
        /// The ID for a Heartbeat packet.
        /// </summary>
        Heartbeat = 0xFD,

        /// <summary>
        /// The ID for a Goodbye packet sent by a server.
        /// </summary>
        SGoodbye = 0xFE,

        /// <summary>
        /// The ID for a Goodbye packet sent by a client.
        /// </summary>
        CGoodbye = 0xFF //Should be sufficiently large, no protocol should need this many packets.
    }

    /// <summary>
    /// Number of seconds for server or client to disconnect by default.
    /// </summary>
    public enum ParloDefaultTimeouts : int
    {
        /// <summary>
        /// Server's default timeout.
        /// </summary>
        Server = 60,

        /// <summary>
        /// Client's default timeout.
        /// </summary>
        Client = 5,
    }

    /// <summary>
    /// Internal packet sent by client and server before disconnecting.
    /// </summary>
    [Serializable]
    internal class GoodbyePacket : IPacket
    {
        /// <summary>
        /// The timeout, I.E how many seconds until the sender will disconnect.
        /// </summary>
        public TimeSpan TimeOut { get; }
        
        /// <summary>
        /// The time when this packet was sent.
        /// </summary>
        public DateTime SentTime { get; }

        /// <summary>
        /// Initializes a new GoodbyePacket instance.
        /// </summary>
        /// <param name="timeOut">A value that indicates, in seconds,
        /// how long till the sender disconnects.</param>
        public GoodbyePacket(int timeOut)
        {
            TimeOut = TimeSpan.FromSeconds(timeOut);
            SentTime = DateTime.UtcNow;
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
        /// <returns>A GoodbyePacket instance.</returns>
        public static GoodbyePacket ByteArrayToObject(byte[] arrBytes)
        {
            using (var memStream = new MemoryStream())
            {
                var binForm = new BinaryFormatter();
                memStream.Write(arrBytes, 0, arrBytes.Length);
                memStream.Seek(0, SeekOrigin.Begin);
                var obj = binForm.Deserialize(memStream);
                return (GoodbyePacket)obj;
            }
        }
    }
}
