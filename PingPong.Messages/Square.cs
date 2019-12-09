
using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public sealed class SquareRequest
    {
        [Key(0)]
        public int Value { get; set; }
    }

    [MessagePackObject]
    public sealed class SquareResponse
    {
        [Key(0)]
        public int Result { get; set; }
    }
}