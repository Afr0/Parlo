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
using System.Collections.Generic;
using Parlo.Encryption;
using System.Security.Cryptography;

namespace Parlo.Packets
{
    /// <summary>
    /// Represents an encrypted packet.
    /// </summary>
    public class EncryptedPacket : Packet
    {
        EncryptionArgs m_Args = new EncryptionArgs();

        /// <summary>
        /// Creates a new instance of EncryptedPacket.
        /// </summary>
        /// <param name="Args">The <see cref="EncryptionArgs"/> used for this packet's encryption.</param>
        /// <param name="ID">The ID of the packet.</param>
        /// <param name="SerializedData">The serialized data to send.</param>
        public EncryptedPacket(EncryptionArgs Args, byte ID, byte[] SerializedData) : 
            base(ID, SerializedData, false)
        {
            if (Args == null)
                throw new ArgumentException("Args");
            if (SerializedData == null)
                throw new ArgumentException("SerializedData");

            m_Args = Args;
        }

        /// <summary>
        /// Decrypts the serialized data of a packet.
        /// </summary>
        /// <returns>The serialied data as an array of bytes.</returns>
        public byte[] DecryptPacket()
        {
            switch (m_Args.Mode)
            {
                case EncryptionMode.AES:
                default:
                    AES Enc = new AES(m_Args.Key, HexStringToByteArray(m_Args.Salt));
                    return Enc.Decrypt(m_Data);
                case EncryptionMode.Twofish:
                    Twofish Fish = new Twofish()
                    {
                        KeySize = 256,
                        Mode = CipherMode.CBC,
                        Padding = PaddingMode.PKCS7
                    };

                    Rfc2898DeriveBytes KeyDerivator = new Rfc2898DeriveBytes(m_Args.Key, HexStringToByteArray(m_Args.Salt),
                        10000);
                    Fish.Key = KeyDerivator.GetBytes(32); //256 bits
                    Fish.IV = KeyDerivator.GetBytes(16);  //128 bits
                    ICryptoTransform Transformer = Fish.CreateDecryptor(Fish.Key, Fish.IV);

                    using (MemoryStream EncryptedStream = new MemoryStream(m_Data))
                    {
                        using (CryptoStream CSEncrypt = new CryptoStream(EncryptedStream, Transformer, CryptoStreamMode.Read))
                        {
                            using (BinaryReader BWDecrypt = new BinaryReader(CSEncrypt))
                            {
                                m_Data = BWDecrypt.ReadBytes(m_Data.Length);
                            }
                        }
                    }

                    KeyDerivator.Dispose();
                    Transformer.Dispose();
                    Fish.Dispose();

                    return m_Data;
            }
        }

        /// <summary>
        /// Builds a encrypted packet ready for sending.
        /// An encrypted packet has an extra byte after 
        /// the length indicating that it is encrypted.
        /// </summary>
        /// <returns>The packet as an array of bytes.</returns>
        public override byte[] BuildPacket()
        {
            byte[] EncryptedData;

            switch (m_Args.Mode)
            {
                case EncryptionMode.AES:
                default:
                    AES Enc = new AES(m_Args.Key, HexStringToByteArray(m_Args.Salt));
                    EncryptedData = Enc.Encrypt(m_Data);
                    break;
                case EncryptionMode.Twofish:
                    Twofish Fish = new Twofish()
                    {
                        KeySize = 256,
                        Mode = CipherMode.CBC,
                        Padding = PaddingMode.PKCS7
                    };

                    Rfc2898DeriveBytes KeyDerivator = new Rfc2898DeriveBytes(m_Args.Key, HexStringToByteArray(m_Args.Salt),
                        10000);
                    Fish.Key = KeyDerivator.GetBytes(32); //256 bits
                    Fish.IV = KeyDerivator.GetBytes(16);  //128 bits
                    ICryptoTransform Transformer = Fish.CreateEncryptor(Fish.Key, Fish.IV);

                    using (MemoryStream EncryptedStream = new MemoryStream())
                    {
                        using (CryptoStream CSEncrypt = new CryptoStream(EncryptedStream, Transformer, CryptoStreamMode.Write))
                        {
                            using (BinaryWriter SWEncrypt = new BinaryWriter(CSEncrypt))
                            {
                                SWEncrypt.Write(m_Data);
                                SWEncrypt.Flush();
                            }

                            EncryptedData = EncryptedStream.ToArray();
                        }
                    }

                    KeyDerivator.Dispose();
                    Transformer.Dispose();
                    Fish.Dispose();

                    break;
            }

            List<byte> PacketData = new List<byte>
            {
                ID,
                //Use the default value here,
                //because the packet will be adaptively
                //compressed when sending.
                IsCompressed
            };

            byte[] PacketLength = BitConverter.GetBytes((ushort)(EncryptedData.Length + PacketHeaders.STANDARD));
            PacketData.AddRange(PacketLength);
            PacketData.AddRange(EncryptedData);

            return PacketData.ToArray();
        }

        /// <summary>
        /// Converts a hex string to a byte array.
        /// </summary>
        /// <param name="Hex">The hex string to convert.</param>
        /// <returns>A byte array containing the converted string.</returns>
        private byte[] HexStringToByteArray(string Hex)
        {
            if (Hex == string.Empty)
                throw new ArgumentException("Hex");

            int Length = Hex.Length;
            byte[] Bytes = new byte[Length / 2];

            for (int i = 0; i < Length; i += 2)
                Bytes[i / 2] = Convert.ToByte(Hex.Substring(i, 2), 16);

            return Bytes;
        }

        /// <summary>
        /// Creates a new EncryptedPacket instance from a Packet instance.
        /// </summary>
        /// <param name="Args">Arguments used for encryption/decryption.</param>
        /// <param name="P">The packet from which to create an EncryptedPacket instance.</param>
        /// <returns>An encryptedPacket instance.</returns>
        public static EncryptedPacket FromPacket(EncryptionArgs Args, Packet P)
        {
            if(Args == null) 
                throw new ArgumentNullException("Args");
            if (P == null) 
                throw new ArgumentNullException("Packet");

            return new EncryptedPacket(Args, P.ID, P.Data);
        }
    }
}
