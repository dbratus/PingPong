using System.Collections.Generic;
using System.Text.Json;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    sealed class ServiceConfigsProvider : IConfig
    {
        private readonly Dictionary<string, JsonElement> _serviceConfigs;

        public ServiceConfigsProvider(Dictionary<string, JsonElement> serviceConfigs)
        {
            _serviceConfigs = serviceConfigs;
        }

        public TConfigSection GetConfigForService<TService, TConfigSection>() 
            where TConfigSection : new()
        {
            if (_serviceConfigs.TryGetValue(typeof(TService).FullName, out JsonElement config))
                return System.Text.Json.JsonSerializer.Deserialize<TConfigSection>(config.GetRawText());

            return new TConfigSection();
        }
    }
}