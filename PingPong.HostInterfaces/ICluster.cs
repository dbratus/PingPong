using System.Threading.Channels;
using System.Threading.Tasks;

namespace PingPong.HostInterfaces
{
    public interface ICluster
    {
        void Send<TRequest>(TRequest request)
            where TRequest: class;
        Task<TResponse?> SendAsync<TRequest, TResponse>(TRequest request)
            where TRequest: class 
            where TResponse: class;
        Task<TResponse?> SendAsync<TRequest, TResponse>()
            where TRequest: class 
            where TResponse: class;
        Task SendAsync<TRequest>(TRequest request)
            where TRequest: class;
        Task SendAsync<TRequest>()
            where TRequest: class;
        void Send<TRequest>(int instanceId, TRequest request)
            where TRequest: class;
        Task<TResponse?> SendAsync<TRequest, TResponse>(int instanceId, TRequest request)
            where TRequest: class 
            where TResponse: class;
        Task<TResponse?> SendAsync<TRequest, TResponse>(int instanceId)
            where TRequest: class 
            where TResponse: class;
        Task SendAsync<TRequest>(int instanceId, TRequest request)
            where TRequest: class;
        Task SendAsync<TRequest>(int instanceId)
            where TRequest: class;

        void Publish<TEvent>(TEvent ev)
            where TEvent: class;
        Task PublishAsync<TEvent>(TEvent ev)
            where TEvent: class;

        ChannelReader<(TResponse?, RequestResult)> OpenChannelAsync<TRequest, TResponse>()
            where TRequest: class
            where TResponse: class;
        ChannelReader<(TResponse?, RequestResult)> OpenChannelAsync<TRequest, TResponse>(TRequest request)
            where TRequest: class 
            where TResponse: class;
        ChannelReader<(TResponse?, RequestResult)> OpenChannelAsync<TRequest, TResponse>(int instanceId)
            where TRequest: class 
            where TResponse: class;
        ChannelReader<(TResponse?, RequestResult)> OpenChannelAsync<TRequest, TResponse>(int instanceId, TRequest request)
            where TRequest: class 
            where TResponse: class;
    }
}