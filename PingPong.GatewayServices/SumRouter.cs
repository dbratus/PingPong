using System;
using System.Threading.Tasks;
using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.GatewayServices
{
    public class SumRouterService
    {
        private readonly ICluster _cluster;

        public SumRouterService(ICluster cluster)
        {
            _cluster = cluster;
        }

        public async Task RouteAdd(AddRequestRouted request)
        {
            RequestResult result = await _cluster.SendAsync(
                request.InstanceId, 
                new AddRequest { CounterId = request.CounterId, Value = request.Value }
            );

            if (result != RequestResult.OK)
                throw new Exception($"Routing failed {result}");
        }

        public async Task<GetSumResponse> RouteGetSum(GetSumRequestRouted request)
        {
            (GetSumResponse? response, RequestResult result) = 
                await _cluster.SendAsync<GetSumRequest, GetSumResponse>(
                    request.InstanceId,
                    new GetSumRequest { CounterId = request.CounterId }
                );

            if (result != RequestResult.OK)
                throw new Exception($"Routing failed {result}");

            return response ?? new GetSumResponse {};
        }
    }
}