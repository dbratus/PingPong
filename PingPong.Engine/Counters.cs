using System.Threading;

namespace PingPong.Engine
{
    sealed class ServiceHostCounters
    {
        public Counter PendingProcessing { get; } = new Counter();
        public Counter InProcessing { get; } = new Counter();
        public Counter PendingResponsePropagation { get; } = new Counter();
    }

    sealed class Counter
    {
        private volatile int _count;

        public int Count =>
            _count;

        public void Increment() =>
            Interlocked.Increment(ref _count);

        public void Decrement() =>
            Interlocked.Decrement(ref _count);
    }
}