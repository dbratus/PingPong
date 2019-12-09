using System;

namespace PingPong.Engine
{
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) {}
        
        public ProtocolException(string message, Exception innerException) : base(message, innerException) {}
    }
}