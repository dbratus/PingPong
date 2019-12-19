using System;

namespace PingPong.Engine
{
    public sealed class ClusterConnectionSettings
    {
        public TimeSpan ReconnectionDelay { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan MaxRequestHoldTime { get; set; } = TimeSpan.FromMinutes(10);
    }
}