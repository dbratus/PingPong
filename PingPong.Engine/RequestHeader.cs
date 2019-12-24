using System;
using MessagePack;

namespace PingPong.Engine
{
    [MessagePackObject]
    public sealed class RequestHeader
    {
        [Key(0)]
        public int RequestNo { get; set; }

        [Key(1)]
        public int MessageId { get; set; }

        [Key(2)]
        public RequestFlags Flags { get; set; }
    }

    [Flags]
    public enum RequestFlags
    {
        None = 0,
        NoBody = 1,
        NoResponse = 2
    }
}