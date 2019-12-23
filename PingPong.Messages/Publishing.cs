using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class PublishRequest
    {
        [Key(0)]
        public string Message { get; set; } = "";
    }

    [MessagePackObject]
    public class PublisherEvent
    {
        [Key(0)]
        public string Message { get; set; } = "";
    }
}