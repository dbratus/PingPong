using MessagePack;

namespace PingPong.Engine
{
    [MessagePackObject]
    public class Preamble
    {
        [Key(0)]
        public MessageIdMapEntry[] MessageIdMap { get; set; }

        [Key(1)]
        public RequestResponseMapEntry[] RequestResponseMap { get; set; }
    }

    [MessagePackObject]
    public class MessageIdMapEntry
    {
        [Key(0)]
        public string MessageType { get; set; }

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