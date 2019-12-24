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

        public string NLogConfigFile { get; set; } = "";

        public int StatusMessageInterval { get; set; } = 5;

        public ClusterConnectionSettingsSection ClusterConnectionSettings { get; set; } =
            new ClusterConnectionSettingsSection();

        public string[] KnownHosts { get; set; } = 
            new string[0];

        public Dictionary<string, JsonElement> ServiceConfigs { get; set; } = 
            new Dictionary<string, JsonElement>();

        public class ClusterConnectionSettingsSection
        {
            public int ConnectionDelay { get; set; } = 3;
            public int ReconnectionDelay { get; set; } = 15;
            public int InvokeCallbacksPeriodMs { get; set; } = 100;
        }
    }
}