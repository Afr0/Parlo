using Parlo.Events;
using System;
using System.Collections.Generic;
using System.Text;

namespace Parlo.Exceptions
{
    /// <summary>
    /// Exception that occurs when a buffer overflows.
    /// </summary>
    public class BufferOverflowException : Exception
    {
        /// <summary>
        /// The error code for this exception.
        /// </summary>
        public EventCodes ErrorCode = EventCodes.BUFFER_OVERFLOW_ERROR;

        /// <summary>
        /// Creates a new DecryptionException.
        /// </summary>
        /// <param name="Description">The description of what happened.</param>
        public BufferOverflowException(string Description = "Buffer overflow occured when receiving data!")
            : base(Description)
        {

        }
    }
}
