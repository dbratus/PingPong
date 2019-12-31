using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class EchoMessage
    {
        [Key(0)]
        public string Message { get; set; } = "";
    }
}