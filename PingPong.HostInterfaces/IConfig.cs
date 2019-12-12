namespace PingPong.HostInterfaces
{
    public interface IConfig
    {
        TConfigSection GetConfigForService<TService, TConfigSection>()
            where TConfigSection: new();
    }
}