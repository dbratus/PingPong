using System;
using NLog;
using PingPong.Messages;

namespace PingPong.Services
{
    public class SummService
    {
        private static ILogger _logger = LogManager.GetCurrentClassLogger();
        
        private int _summ = 0;

        public void Add(AddRequest request)
        {
            _summ += request.Value;
            _logger.Info("Value to sum received {0}, current sum {1}", request.Value, _summ);
        }

        public GetSummResponse GetSumm(GetSummRequest request)
        {
            _logger.Info("Summ requested, response {0}", _summ);
            return new GetSummResponse { Result = _summ };
        }
    }
}