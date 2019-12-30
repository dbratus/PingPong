using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.Services
{
    public class StreamingService
    {
        private const int StreamLength = 10;
        private static ConcurrentDictionary<(int, int), int> _counters = 
            new ConcurrentDictionary<(int, int), int>();

        private readonly ISession _session;

        public StreamingService(ISession session)
        {
            _session = session;
        }

        public StreamingResponse? Stream(StreamingRequest request)
        {
            var uniqueRequestId = (_session.ConnectionId, _session.RequestNo);
            int count = _counters.GetOrAdd(uniqueRequestId, reqNo => 0);

            if (count == StreamLength)
            {
                _counters.TryRemove(uniqueRequestId, out count);
                return null;
            }

            _counters.TryUpdate(uniqueRequestId, count + 1, count);

            return new StreamingResponse { Value = count };
        }
    }
}