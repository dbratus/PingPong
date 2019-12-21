namespace PingPong.HostInterfaces
{
    public interface IContainerBuilder
    {
        void Register<T>();
        void Register<T, TInterface>()
            where T: TInterface;
    }
}