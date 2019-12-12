using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class GetConfigValueRequest
    {
    }

    [MessagePackObject]
    public class GetConfigValueResponse
    {
        [Key(0)]
        public string Value { get; set; } = "";
    }
}