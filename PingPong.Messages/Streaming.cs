using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class StreamingRequest
    {
    }

    [MessagePackObject]
    public class StreamingResponse
    {
        [Key(0)]
        public int Value { get; set; }
    }
}