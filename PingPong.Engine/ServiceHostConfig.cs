using System.Collections.Generic;
using System.Text.Json;

namespace PingPong.Engine
{
    public sealed class ServiceHostConfig
    {
        public int Port { get; set; }

        public int InstanceId { get; set; }

        public string[] ServiceAssemblies { get; set; } = 
            new string[0];

        public string[] MessageAssemblies { get; set; } = 
            new string[0];

        public string NLogConfigFile { get; set; } = "";

        public int StatusMessageInterval { get; set; } = 5;

        public bool Gateway { get; set; }

        public string Serializer { get; set; } = "PingPong.Engine.SerializerMessagePack";

        public TlsSettingsSection? TlsSettings { get; set; }

        public ClusterConnectionSettingsSection ClusterConnectionSettings { get; set; } =
            new ClusterConnectionSettingsSection();

        public string[] KnownHosts { get; set; } = 
            new string[0];

        public Dictionary<string, JsonElement> ServiceConfigs { get; set; } = 
            new Dictionary<string, JsonElement>();

        public sealed class ClusterConnectionSettingsSection
        {
            public int ConnectionDelay { get; set; } = 3;
            public int ReconnectionDelay { get; set; } = 15;
            public int UpdatePeriodMs { get; set; } = 1;
            public int MaxRequestHoldTime { get; set; } = 600;
            public string Serializer { get; set; } = "PingPong.Engine.SerializerMessagePack";
        }

        public sealed class TlsSettingsSection
        {
            public string CertificateFile { get; set; } = "";
            public string PasswordFile { get; set; } = "";
            public bool AllowSelfSignedCertificates { get; set; }
        }
    }
}