/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using Parlo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Parlo.Packets
{
    /// <summary>
    /// A packet is a container for data that is sent over the network.
    /// </summary>
    public class Packet
    {
        private byte m_ID;
        private byte m_IsCompressed = 0;
        private byte m_IsReliable = 0;
        private ushort m_Length;

        /// <summary>
        /// The packet's data.
        /// </summary>
        protected byte[] m_Data; //Should be serialized.

        private bool m_IsUDP = false;

        /// <summary>
        /// Creates a new packet.
        /// </summary>
        /// <param name="ID">The ID of the packet.</param>
        /// <param name="SerializedData">The serialized data to send in the packet.</param>
        /// <param name="IsPacketCompressed">Is the packet compressed?</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if Length does not match the data's length.</exception>
        /// <exception cref="ArgumentNullException">Thrown if data was null.</exception>
        public Packet(byte ID, byte[] SerializedData, bool IsPacketCompressed = false)
        {
            if (SerializedData == null)
                throw new ArgumentNullException("SerializedData cannot be null!");

            m_ID = ID;
            m_IsCompressed = (byte)((IsPacketCompressed == true) ? 1 : 0);
            m_Length = (ushort)(PacketHeaders.STANDARD + SerializedData.Length);
            m_Data = SerializedData;
        }

        /// <summary>
        /// Creates a new packet for sending over UDP.
        /// </summary>
        /// <param name="ID">The ID of the packet.</param>
        /// <param name="SerializedData">The serialized data to send in the packet.</param>
        /// <param name="IsPacketCompressed">Is the packet compressed?</param>
        /// <param name="IsPacketReliable">Is the packet reliably transmitted?</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if Length does not match the data's length.</exception>
        /// <exception cref="ArgumentNullException">Thrown if data was null.</exception>
        public Packet(byte ID, byte[] SerializedData, bool IsPacketCompressed = false,
            bool IsPacketReliable = false)
        {
            if (SerializedData == null)
                throw new ArgumentNullException("SerializedData cannot be null!");

            m_IsUDP = true;

            m_ID = ID;
            m_IsCompressed = (byte)((IsPacketCompressed == true) ? 1 : 0);
            m_IsReliable = (byte)((IsPacketReliable == true) ? 1 : 0);
            m_Length = (ushort)(PacketHeaders.UDP + SerializedData.Length);
            m_Data = SerializedData;
        }

        /// <summary>
        /// Creates a new packet for received UDP data.
        /// </summary>
        /// <param name="SequenceNumber">The sequence number of the packet.</param>
        /// <param name="ID">The ID of the packet.</param>
        /// <param name="SerializedData">The serialized data to send in the packet.</param>
        /// <param name="IsPacketCompressed">Is the packet compressed?</param>
        /// <param name="IsPacketReliable">Is the packet reliably transmitted?</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if Length does not match the data's length.</exception>
        /// <exception cref="ArgumentNullException">Thrown if data was null.</exception>
        public Packet(int SequenceNumber, byte ID, byte[] SerializedData, bool IsPacketCompressed = false,
            bool IsPacketReliable = false)
        {
            if (SerializedData == null)
                throw new ArgumentNullException("SerializedData cannot be null!");

            m_IsUDP = true;

            m_ID = ID;
            m_IsCompressed = (byte)((IsPacketCompressed == true) ? 1 : 0);
            m_IsReliable = (byte)((IsPacketReliable == true) ? 1 : 0);
            m_Length = (ushort)(PacketHeaders.UDP + SerializedData.Length);
            m_Data = SerializedData;
        }

        /// <summary>
        /// The ID of the packet.
        /// </summary>
        public byte ID
        {
            get { return m_ID; }
        }

        /// <summary>
        /// Is the packet's data compressed?
        /// 1 = Compressed, 0 = Not compressed
        /// </summary>
        public byte IsCompressed
        {
            get { return m_IsCompressed; }
        }

        /// <summary>
        /// The length of the packet. 0 if variable length.
        /// </summary>
        public ushort Length
        {
            get { return m_Length; }
        }

        /// <summary>
        /// The packet's serialized data.
        /// </summary>
        public byte[] Data
        {
            get { return m_Data; }
        }

        /// <summary>
        /// Builds a packet ready for sending.
        /// </summary>
        /// <returns>The packet as an array of bytes.</returns>
        public virtual byte[] BuildPacket()
        {
            if (!m_IsUDP)
            {
                byte[] PacketData = new byte[4 + m_Data.Length];

                PacketData[0] = ID;
                PacketData[1] = IsCompressed;

                byte[] PacketLength = BitConverter.GetBytes(Length);

                PacketData[2] = PacketLength[0];
                PacketData[3] = PacketLength[1];

                Array.Copy(m_Data, 0, PacketData, 4, m_Data.Length);

                return PacketData;
            }
            else
            {
                byte[] PacketData = new byte[5 + m_Data.Length];

                PacketData[0] = ID;
                PacketData[1] = IsCompressed;
                PacketData[2] = m_IsReliable;

                byte[] PacketLength = BitConverter.GetBytes(Length);

                PacketData[3] = PacketLength[0];
                PacketData[4] = PacketLength[1];

                Array.Copy(m_Data, 0, PacketData, 4, m_Data.Length);

                return PacketData;
            }
        }
    }
}
