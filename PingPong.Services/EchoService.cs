using PingPong.Messages;

namespace PingPong.Services
{
    public class EchoService
    {
        public EchoMessage Echo(EchoMessage message) =>
            message;
    }
}