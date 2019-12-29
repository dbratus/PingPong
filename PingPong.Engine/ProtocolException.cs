using System;

namespace PingPong.Engine
{
    public sealed class ProtocolException : Exception
    {
        internal ProtocolException(string message) 
            : base(message) 
        {
        }
        
        internal ProtocolException(string message, Exception? innerException) 
            : base(message, innerException) 
        {
        }
    }
}