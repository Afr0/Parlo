/*This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.

The Original Code is the Parlo Library.

The Initial Developer of the Original Code is
Mats 'Afr0' Vederhus. All Rights Reserved.

Contributor(s): ______________________________________.
*/

using System.Diagnostics;

namespace Parlo
{
    /// <summary>
    /// A delegate for subscribing to messages logged by GonzoNet.
    /// </summary>
    /// <param name="Msg">The message that was logged.</param>
    public delegate void MessageLoggedDelegate(LogMessage Msg);

    /// <summary>
    /// The level of a message logged by Parlo.
    /// </summary>
    public enum LogLevel
	{
        /// <summary>
        /// An error message.
        /// </summary>
		error=1,

        /// <summary>
        /// A warning message.
        /// </summary>
		warn,

        /// <summary>
        /// An informational message.
        /// </summary>
		info,
        /// <summary>
        /// A verbose message.
        /// </summary>
		verbose
	}

    /// <summary>
    /// A message logged by Parlo.
    /// </summary>
    public class LogMessage
    {
        /// <summary>
        /// Constructs a new log message.
        /// </summary>
        /// <param name="Msg">The message to log.</param>
        /// <param name="Lvl">The level of the message.</param>
        public LogMessage(string Msg, LogLevel Lvl)
        {
            Message = Msg;
            Level = Lvl;
        }

        /// <summary>
        /// The message that was logged.
        /// </summary>
        public string Message;

        /// <summary>
        /// The level of the message.
        /// </summary>
        public LogLevel Level;
    }

    /// <summary>
    /// A class for subscribing to messages logged by Parlo.
    /// </summary>
    public static class Logger
    {
        /// <summary>
        /// Subscribe to this event to receive messages logged by Parlo.
        /// </summary>
        public static event MessageLoggedDelegate OnMessageLogged;

        /// <summary>
        /// Called by classes in GonzoNet to log a message.
        /// </summary>
        /// <param name="Message">The message to log!</param>
        /// <param name="Lvl">The level of the message.</param>
        public static void Log(string Message, LogLevel Lvl)
        {
#if DEBUG
            LogMessage Msg = new LogMessage(Message, Lvl);

            if (OnMessageLogged != null)
                OnMessageLogged(Msg);
            else
                Debug.WriteLine(Msg.Message);
#endif
        }
    }
}
