using System;
using System.Collections.Concurrent;
using System.Threading;
using NLog;
using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.Services
{
    public class SumService
    {
        private static ILogger _logger = LogManager.GetCurrentClassLogger();
        
        private readonly ISession _session;
        private readonly ConcurrentDictionary<int, int> _counters =
            new ConcurrentDictionary<int, int>();

        private int _nextCountertId = 0;

        public SumService(ISession session)
        {
            _session = session;
        }

        public InitSumResponse InitSum(InitSumRequest request)
        {
            int counterId = Interlocked.Increment(ref _nextCountertId);
            _counters.AddOrUpdate(counterId, id => 0, (id, prev) => 0);
            _logger.Info("Sum initialized {0}.", counterId);
            return new InitSumResponse { CounterId = counterId, InstanceId = _session.InstanceId };
        }

        public void Add(AddRequest request)
        {
            int sum = _counters.AddOrUpdate(request.CounterId, id => 0, (id, prev) => prev + request.Value);
            _logger.Info("Value to sum received {0} {1}, current sum {2}", request.CounterId, request.Value, sum);
        }

        public GetSumResponse GetSumm(GetSumRequest request)
        {
            int sum = _counters.GetOrAdd(request.CounterId, id => -1);
            _logger.Info("Sum requested {0}, response {1}", request.CounterId, sum);
            return new GetSumResponse { Result = sum };
        }
    }
}