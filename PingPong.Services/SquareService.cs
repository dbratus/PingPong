using System.Threading.Tasks;
using PingPong.Messages;

namespace PingPong.Services
{
    public class SquareService
    {
        public async Task<SquareResponse> Square(SquareRequest request) =>
            await Task.Run(() => new SquareResponse {
                Result = request.Value * request.Value
            });
    }
}