using System.Threading.Tasks;

namespace PingPong.HostInterfaces
{
    public interface ICluster
    {
        void Send<TRequest>(TRequest request)
            where TRequest: class;
        Task<(TResponse?, RequestResult)> Send<TRequest, TResponse>(TRequest request)
            where TRequest: class 
            where TResponse: class;
        Task<(TResponse?, RequestResult)> Send<TRequest, TResponse>()
            where TRequest: class 
            where TResponse: class;
    }
}