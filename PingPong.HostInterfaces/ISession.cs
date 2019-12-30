using System.Net;

namespace PingPong.HostInterfaces
{
    public interface ISession
    {
        int InstanceId { get; }
        int ConnectionId { get; }
        int RequestNo { get; }
        IPAddress ClientRemoteAddress { get; }
    }
}