using System.Net;
using System.Threading;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    sealed class Session : ISession
    {
        private readonly AsyncLocal<Data> _data = new AsyncLocal<Data>();

        public int InstanceId =>
            _data.Value.InstanceId;

        public IPAddress ClientRemoteAddress =>
            _data.Value.ClientRemoteAddress;

        public void SetData(Data data) =>
            _data.Value = data;

        public class Data
        {
            public int InstanceId { get; set; } = -1;
            public IPAddress ClientRemoteAddress { get; set; } = IPAddress.Any;
        }
    }
}