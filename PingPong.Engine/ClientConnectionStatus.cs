namespace PingPong.Engine
{
    public enum ClientConnectionStatus : int
    {
        NotConnected,
        Connecting,
        Active,
        Broken,
        Disposed
    }
}