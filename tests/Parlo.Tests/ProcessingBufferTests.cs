﻿using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Parlo.Exceptions;

namespace Parlo.Tests
{
    [TestClass]
    public class ProcessingBufferTests
    {
        /// <summary>
        /// Checks that adding data to the processing buffer 
        /// works correctly by adding some data to the buffer 
        /// and checking that the count of the internal buffer 
        /// matches the expected value.
        /// </summary>
        [TestMethod]
        public void TestAddingData()
        {
            var processingBuffer = new ProcessingBuffer();
            byte[] data = new byte[] { 1, 2, 3, 4 };
            processingBuffer.AddData(data);
            try
            {
                Assert.AreEqual(4, processingBuffer.InternalBuffer.Count);
                if (processingBuffer.InternalBuffer.TryTake(out byte peekedValue, TimeSpan.FromSeconds(1)))
                {
                    Assert.AreEqual(data[0], peekedValue);
                    processingBuffer.InternalBuffer.Add(peekedValue); // Add the taken element back to the buffer
                }
                else
                {
                    Assert.Fail("Failed to take an element from the buffer.");
                }
            }
            catch (Exception E)
            {
                Debug.WriteLine(E.ToString());
            }
        }

        /// <summary>
        /// Tests that it is not possible to add more data than the maximum packet size.
        /// </summary>
        [TestMethod]
        public void TestAddingTooMuchData()
        {
            var processingBuffer = new ProcessingBuffer();
            byte[] data = new byte[ProcessingBuffer.MAX_PACKET_SIZE + 1];
            
            Assert.ThrowsException<BufferOverflowException>(() => processingBuffer.AddData(data));
        }

        /// <summary>
        /// Tests that the processing buffer fired an event when a packet was processed.
        /// </summary>
        [TestMethod]
        public void TestProcessingPacket()
        {
            var processingBuffer = new ProcessingBuffer();
            bool eventFired = false;
            processingBuffer.OnProcessedPacket += async (packet) =>
            {
                eventFired = true;
            };
            byte[] data = new byte[] { 1, 0, 4, 5, 6, 7 };
            processingBuffer.AddData(data);

            try
            {
                Assert.IsFalse(eventFired);
                processingBuffer.AddData(new byte[] { 8, 9, 10 });
                Assert.IsTrue(eventFired);
            }
            catch (Exception E)
            {
                Debug.WriteLine(E.ToString());
            }
        }
    }
}