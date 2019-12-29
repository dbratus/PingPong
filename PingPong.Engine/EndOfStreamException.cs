using System;

namespace PingPong.Engine
{
    public sealed class EndOfStreamException : Exception
    {
        internal EndOfStreamException() 
            : base("End of stream.")
        {
        }
    }
}