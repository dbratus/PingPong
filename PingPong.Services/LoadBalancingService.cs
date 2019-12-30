using System.Threading;
using PingPong.Messages;

namespace PingPong.Services
{
    public class LoadBalancingService
    {
        private volatile int _count;

        public void Init(LoadBalancingInitRequest request) =>
            _count = 0;

        public void Increment(LoadBalancingRequest request) =>
            Interlocked.Increment(ref _count);

        public LoadBalancingGetStatsResponse GetStats(LoadBalancingGetStatsRequest request) =>
            new LoadBalancingGetStatsResponse { Result = _count };
    }
}