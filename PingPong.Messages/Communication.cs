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