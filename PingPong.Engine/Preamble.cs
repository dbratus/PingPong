using MessagePack;

namespace PingPong.Engine
{
    [MessagePackObject]
    public class Preamble
    {
        [Key(0)]
        public int InstanceId { get; set; } = -1;

        [Key(1)]
        public MessageIdMapEntry[] MessageIdMap { get; set; } = new MessageIdMapEntry[0];

        [Key(2)]
        public RequestResponseMapEntry[] RequestResponseMap { get; set; } = new RequestResponseMapEntry[0];
    }

    [MessagePackObject]
    public class MessageIdMapEntry
    {
        [Key(0)]
        public string MessageType { get; set; } = "";

        [Key(1)]
        public int MessageId { get; set; }
    }

    [MessagePackObject]
    public class RequestResponseMapEntry
    {
        [Key(0)]
        public int RequestId { get; set; }
        
        [Key(1)]
        public int ResponsetId { get; set; }
    }
}