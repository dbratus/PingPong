using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.Services
{
    public class StreamingService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

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
            int count = _counters.GetOrAdd(uniqueRequestId, reqNo => Math.Max(request.Count, 0));

            if (count < 0)
            {
                _logger.Info("Channel closed.");
                _counters.TryRemove(uniqueRequestId, out count);
                return null;
            }

            _counters.TryUpdate(uniqueRequestId, count - 1, count);

            _logger.Info("Value {0} streamed.", count);
            return new StreamingResponse { Value = count };
        }
    }
}