using System.Threading;

namespace PingPong.Engine
{
    sealed class RequestNoGenerator
    {
        private long _nextRequestId = 1;

        public long GetNext() =>
            Interlocked.Increment(ref _nextRequestId);
    }
}