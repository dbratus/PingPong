using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class PriorityRequest
    {
        [Key(0)]
        public int Priority { get; set; }
    }

    [MessagePackObject]
    public class PriorityResponse
    {
        [Key(0)]
        public int Priority { get; set; }
    }
}