using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class AddRequest
    {
        [Key(0)]
        public int Value { get; set; }
    }

    [MessagePackObject]
    public class GetSummRequest
    {
    }

    [MessagePackObject]
    public class GetSummResponse
    {
        [Key(0)]
        public int Result { get; set; }
    }
}