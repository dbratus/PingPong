using System.Threading.Tasks;
using PingPong.Messages;

namespace PingPong.Services
{
    public class PriorityService
    {
        public async Task<PriorityResponse> HandleRequest(PriorityRequest request)
        {
            await Task.Delay(30);

            return new PriorityResponse {
                Priority = request.Priority
            };
        }
    }
}