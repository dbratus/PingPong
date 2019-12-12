using System.Collections.Generic;
using System.Text.Json;

namespace PingPong.Engine
{
    public class ServiceHostConfig
    {
        public int Port { get; set; }

        public string[] KnownHosts { get; set; } 
            = new string[0];

        public Dictionary<string, JsonElement> ServiceConfigs { get; set; } 
            = new Dictionary<string, JsonElement>();
    }
}