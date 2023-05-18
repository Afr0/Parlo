/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Parlo.Packets;
using Parlo.Exceptions;

[assembly: InternalsVisibleTo("Parlo.Tests")]
namespace Parlo
{
    delegate void ProcessedPacketDelegate(Packet Packet);

    /// <summary>
    /// A buffer for processing received data, turning it into individual PacketStream instances.
    /// </summary>
    internal class ProcessingBuffer : IDisposable
    {
        /// <summary>
        /// Gets or sets the maximum packet size that can be processed by the buffer at once.
        /// </summary>
        public static int MAX_PACKET_SIZE = 1024;

        private BlockingCollection<byte> m_InternalBuffer = new BlockingCollection<byte>();
        private CancellationTokenSource m_CancellationTokenSource = new CancellationTokenSource();

        /// <summary>
        /// Gets, but does NOT set the internal buffer. Used by tests.
        /// </summary>
        public BlockingCollection<byte> InternalBuffer
        {
            get { return m_InternalBuffer; }
        }

        private bool m_HasReadHeader = false;
        private byte m_CurrentID;       //ID of current packet.
        private byte m_IsCompressed;    //Whether or not the current packet contains compressed data.
        private ushort m_CurrentLength; //Length of current packet.

        public event ProcessedPacketDelegate OnProcessedPacket;
        private Task ProcessingTask;

        /// <summary>
        /// Creates a new ProcessingBuffer instance.
        /// </summary>
        public ProcessingBuffer()
        {
            CancellationToken Token = m_CancellationTokenSource.Token;

            ProcessingTask = Task.Run(async () =>
            {
                while (true)
                {
                    if (Token.IsCancellationRequested)
                        return;

                    if (m_InternalBuffer.Count >= (int)PacketHeaders.STANDARD)
                    {
                        if (!m_HasReadHeader)
                        {
                            m_CurrentID = m_InternalBuffer.Take();

                            m_IsCompressed = m_InternalBuffer.Take();

                            byte[] LengthBuf = new byte[2];

                            for (int i = 0; i < LengthBuf.Length; i++)
                                LengthBuf[i] = m_InternalBuffer.Take();

                            m_CurrentLength = BitConverter.ToUInt16(LengthBuf, 0);

                            m_HasReadHeader = true;
                        }
                    }

                    if (m_HasReadHeader == true)
                    {
                        //Hurray, enough shit (data) was shoveled into the buffer that we have a new packet!
                        if (m_InternalBuffer.Count >= (ushort)(m_CurrentLength - PacketHeaders.STANDARD))
                        {
                            byte[] PacketData = new byte[(ushort)(m_CurrentLength - PacketHeaders.STANDARD)];

                            for (int i = 0; i < PacketData.Length; i++)
                                PacketData[i] = m_InternalBuffer.Take();

                            m_HasReadHeader = false;

                            Packet P;
                            P = new Packet(m_CurrentID, PacketData, (m_IsCompressed == 1) ? true : false);

                            OnProcessedPacket(P);
                        }
                    }

                    await Task.Delay(10); //DON'T HOG THE PROCESSOR
                }
            }, Token);
        }

        private void StopProcessing()
        {
            m_CancellationTokenSource.Cancel(); // Request cancellation
        }

        /// <summary>
        /// Shovels shit (data) into the buffer.
        /// </summary>
        /// <param name="Data">The data to add. Needs to be no bigger than <see cref="MAX_PACKET_SIZE"/>!</param>
        /// <exception cref="BufferOverflowException">Thrown if <paramref name="Data"/> was larger than 
        /// <see cref="MAX_PACKET_SIZE"/>.</exception>
        public void AddData(byte[] Data)
        {
            //This protects us from attacks with malicious packets that specify a header size of several gigs...
            if (Data.Length > MAX_PACKET_SIZE)
            {
                Logger.Log("Tried adding too much data to ProcessingBuffer!", LogLevel.error);
                throw new BufferOverflowException();
            }

            for (int i = 0; i < Data.Length; i++)
                m_InternalBuffer.Add(Data[i]);
        }

        ~ProcessingBuffer()
        {
            Dispose(false);
        }

        /// <summary>
        /// Disposes of the resources used by this ProcessingBuffer instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Disposes of the resources used by this ProcessingBuffer instance.
        /// <param name="Disposed">Was this resource disposed explicitly?</param>
        /// </summary>
        protected virtual void Dispose(bool Disposed)
        {
            if (Disposed)
            {
                StopProcessing();

                // Prevent the finalizer from calling ~ProcessingBuffer, since the object is already disposed at this point.
                GC.SuppressFinalize(this);
            }
            else
                Logger.Log("ProcessingBuffer not explicitly disposed!", LogLevel.error);
        }
    }
}
