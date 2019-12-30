using MessagePack;

namespace PingPong.Messages
{
    [MessagePackObject]
    public class LoadBalancingInitRequest
    {
    }

    [MessagePackObject]
    public class LoadBalancingRequest
    {
    }

    [MessagePackObject]
    public class LoadBalancingGetStatsRequest
    {
    }

    [MessagePackObject]
    public class LoadBalancingGetStatsResponse
    {
        [Key(0)]
        public int Result { get; set; }
    }
}