using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.Services
{
    public class ConfigurableService
    {
        private readonly ConfigSection _config;

        public ConfigurableService(IConfig config)
        {
            _config = config.GetConfigForService<ConfigurableService, ConfigSection>();
        }

        public GetConfigValueResponse GetConfigValue(GetConfigValueRequest request) =>
            new GetConfigValueResponse {
                Value = _config.ConfigValue
            };

        public class ConfigSection
        {
            public string ConfigValue { get; set; } = "";
        }
    }
}