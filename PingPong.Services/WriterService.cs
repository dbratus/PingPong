using System;
using System.Threading.Tasks;
using NLog;
using PingPong.Messages;

namespace PingPong.Services
{
    public class WriterService
    {
        private static ILogger _logger = LogManager.GetCurrentClassLogger();

        public async Task Write(WriteRequest request)
        {
            await Task.Yield();

            _logger.Info(request.Message);
        }
    }
}