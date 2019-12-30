using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class StreamingRequest
    {
        [Key(0)]
        public int Count { get; set; }
    }

    [MessagePackObject]
    public class StreamingResponse
    {
        [Key(0)]
        public int Value { get; set; }
    }
}