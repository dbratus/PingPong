using System;

namespace PingPong.Engine
{
    public sealed class ClusterConnectionSettings
    {
        public TimeSpan ReconnectionDelay { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan MaxRequestHoldTime { get; set; } = TimeSpan.FromMinutes(10);
        public ClientTlsSettings TlsSettings { get; set; } = new ClientTlsSettings();
        public ISerializer Serializer { get; set; } = new SerializerMessagePack();
    }
}