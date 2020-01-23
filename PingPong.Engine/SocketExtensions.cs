using System.Net;
using System.Net.Sockets;

namespace PingPong.Engine
{
    static class SocketExtension
    {
        public static string GetRemoteAddressName(this Socket socket) =>
            ((IPEndPoint)socket.RemoteEndPoint).Address.ToString();
    }
}