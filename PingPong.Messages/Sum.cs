using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class InitSumRequest
    {
    }

    [MessagePackObject]
    public class InitSumResponse
    {
        [Key(0)]
        public int InstanceId { get; set; }

        [Key(1)]
        public int CounterId { get; set; }
    }

    [MessagePackObject]
    public class AddRequest
    {
        [Key(0)]
        public int CounterId { get; set; }

        [Key(1)]
        public int Value { get; set; }
    }

    [MessagePackObject]
    public class AddRequestRouted : AddRequest
    {
        [Key(100)]
        public int InstanceId { get; set; }
    }

    [MessagePackObject]
    public class GetSumRequest
    {
        [Key(0)]
        public int CounterId { get; set; }
    }

    [MessagePackObject]
    public class GetSumRequestRouted : GetSumRequest
    {
        [Key(100)]
        public int InstanceId { get; set; }
    }

    [MessagePackObject]
    public class GetSumResponse
    {
        [Key(0)]
        public int Result { get; set; }
    }
}