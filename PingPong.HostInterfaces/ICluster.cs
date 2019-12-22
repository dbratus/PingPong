using System.Threading.Tasks;

namespace PingPong.HostInterfaces
{
    public interface ICluster
    {
        void Send<TRequest>(TRequest request)
            where TRequest: class;
        Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>(TRequest request)
            where TRequest: class 
            where TResponse: class;
        Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>()
            where TRequest: class 
            where TResponse: class;
        Task<RequestResult> SendAsync<TRequest>(TRequest request)
            where TRequest: class;
        Task<RequestResult> SendAsync<TRequest>()
            where TRequest: class;
        void Send<TRequest>(int instanceId, TRequest request)
            where TRequest: class;
        Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>(int instanceId, TRequest request)
            where TRequest: class 
            where TResponse: class;
        Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>(int instanceId)
            where TRequest: class 
            where TResponse: class;
        Task<RequestResult> SendAsync<TRequest>(int instanceId, TRequest request)
            where TRequest: class;
        Task<RequestResult> SendAsync<TRequest>(int instanceId)
            where TRequest: class;
    }
}