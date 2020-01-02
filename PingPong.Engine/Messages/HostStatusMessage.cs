using MessagePack;

namespace PingPong.Engine.Messages
{
    [MessagePackObject]
    public sealed class HostStatusMessage
    {
        [Key(0)]
        public int PendingProcessing { get; set; }

        [Key(1)]
        public int InProcessing { get; set; }

        [Key(2)]
        public int PendingResponsePropagation { get; set; }
    }
}