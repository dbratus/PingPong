using MessagePack;

namespace PingPong.Engine.Messages
{
    [MessagePackObject]
    public sealed class Preamble
    {
        [Key(0)]
        public int InstanceId { get; set; } = -1;

        [Key(1)]
        public MessageIdMapEntry[] MessageIdMap { get; set; } = new MessageIdMapEntry[0];

        [Key(2)]
        public RequestResponseMapEntry[] RequestResponseMap { get; set; } = new RequestResponseMapEntry[0];
    }

    [MessagePackObject]
    public sealed class MessageIdMapEntry
    {
        [Key(0)]
        public long MessageTypeHashLo { get; set; }

        [Key(1)]
        public long MessageTypeHashHi { get; set; }

        [Key(2)]
        public int MessageId { get; set; }
    }

    [MessagePackObject]
    public sealed class RequestResponseMapEntry
    {
        [Key(0)]
        public int RequestId { get; set; }
        
        [Key(1)]
        public int ResponsetId { get; set; }
    }
}