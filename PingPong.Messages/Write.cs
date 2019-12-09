using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class WriteRequest
    {
        [Key(0)]
        public string Message { get; set; }
    }
}