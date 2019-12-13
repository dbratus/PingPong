using System.Threading;

namespace PingPong.Engine
{
    sealed class RequestNoGenerator
    {
        private int _nextRequestId;

        public int GetNext() =>
            Interlocked.Increment(ref _nextRequestId);
    }
}