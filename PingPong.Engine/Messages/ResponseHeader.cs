using System;
using MessagePack;

namespace PingPong.Engine.Messages
{
    [MessagePackObject]
    public sealed class ResponseHeader
    {
        [Key(0)]
        public int RequestNo { get; set; }

        [Key(1)]
        public int MessageId { get; set; }

        [Key(2)]
        public ResponseFlags Flags { get; set; }
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