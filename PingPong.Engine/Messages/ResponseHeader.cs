using System;
using MessagePack;
using PingPong.HostInterfaces;

namespace PingPong.Engine.Messages
{
    [MessagePackObject]
    public sealed class ResponseHeader
    {
        [Key(0)]
        public long RequestNo { get; set; }

        [Key(1)]
        public int MessageId { get; set; }

        [Key(2)]
        public ResponseFlags Flags { get; set; }

        [Key(3)]
        public MessagePriority Priority { get; set; }
    }

    [Flags]
    public enum ResponseFlags
    {
        None = 0,
        Error = 1,
        NoBody = 1 << 1,
        Termination = 1 << 2,
        HostStatus = 1 << 3
    }
}