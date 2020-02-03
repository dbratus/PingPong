using System;
using MessagePack;
using PingPong.HostInterfaces;

namespace PingPong.Engine.Messages
{
    [MessagePackObject]
    public sealed class RequestHeader
    {
        [Key(0)]
        public long RequestNo { get; set; }

        [Key(1)]
        public int MessageId { get; set; }

        [Key(2)]
        public RequestFlags Flags { get; set; }

        [Key(3)]
        public MessagePriority Priority { get; set; }
    }

    [Flags]
    public enum RequestFlags
    {
        None = 0,
        NoBody = 1,
        NoResponse = 1 << 1,
        OpenChannel = 1 << 2
    }
}