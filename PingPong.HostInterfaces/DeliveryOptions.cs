namespace PingPong.HostInterfaces
{
    public sealed class DeliveryOptions
    {
        public int InstanceId { get; set; } = -1;
        public MessagePriority Priority { get; set; } = MessagePriority.Normal;
    }
}