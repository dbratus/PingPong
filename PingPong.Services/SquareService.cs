using System.Threading.Tasks;
using NLog;
using PingPong.Messages;

namespace PingPong.Services
{
    public class SquareService
    {
        private static ILogger _logger = LogManager.GetCurrentClassLogger();
        
        public async Task<SquareResponse> Square(SquareRequest request)
        {
            int result = request.Value * request.Value;

            _logger.Info("Square request received {0}, response {1}", request.Value, result);

            return await Task.Run(() => new SquareResponse {
                Result = result
            });
        }
    }
}