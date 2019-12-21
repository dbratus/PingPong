using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class TransferMessageRequest
    {
        [Key(0)]
        public string Message { get; set; } = "";
    }

    [MessagePackObject]
    public class TransferMessageResponse
    {
        [Key(0)]
        public int ServedByInstance { get; set; }
    }

    [MessagePackObject]
    public class ReceiveMessageRequest
    {
        [Key(0)]
        public string Message { get; set; } = "";
    }

    [MessagePackObject]
    public class ReceiveMessageResponse
    {
        [Key(0)]
        public int ServedByInstance { get; set; }
    }

    [MessagePackObject]
    public class GetTransferredMessageRequest
    {
    }

    [MessagePackObject]
    public class GetTransferredMessageResponse
    {
        [Key(0)]
        public string Message { get; set; } = "";
    }
}