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

    [MessagePackObject]
    public class SubscriberOneGetMessageRequest
    {
    }

    [MessagePackObject]
    public class SubscriberOneGetMessageResponse
    {
        [Key(0)]
        public string Message { get; set; } = "";
    }

    [MessagePackObject]
    public class SubscriberTwoGetMessageRequest
    {
    }

    [MessagePackObject]
    public class SubscriberTwoGetMessageResponse
    {
        [Key(0)]
        public string Message { get; set; } = "";
    }
}