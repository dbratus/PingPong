using System.Net;

namespace PingPong.HostInterfaces
{
    public interface ISession
    {
        int InstanceId { get; }
        IPAddress ClientRemoteAddress { get; }
    }
}