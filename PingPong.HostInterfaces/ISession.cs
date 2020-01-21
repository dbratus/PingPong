using System.Net;

namespace PingPong.HostInterfaces
{
    public interface ISession
    {
        int InstanceId { get; }
        int ConnectionId { get; }
        long RequestNo { get; }
        IPAddress ClientRemoteAddress { get; }
    }
}