using System;

namespace PingPong.Engine
{
    public sealed class ClusterConnectionSettings
    {
        public int MaxConnectionRetries { get; set; } = 0;
        public TimeSpan ConnectionRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan ReconnectionDelay { get; set; } = TimeSpan.FromSeconds(15);
    }
}