using System;

namespace PingPong.Engine
{
    public sealed class CommunicationException : Exception
    {
        internal CommunicationException(string message)
            : base (message)
        {
        }

        internal CommunicationException(string message, Exception? innerException)
            : base (message, innerException)
        {
        }
    }
}