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

        public int ConnectionId =>
            _data.Value.ConnectionId;

        public long RequestNo =>
            _data.Value.RequestNo;

        public IPAddress ClientRemoteAddress =>
            _data.Value.ClientRemoteAddress;

        public void SetData(Data data) =>
            _data.Value = data;

        public void SetRequestNo(long requestId) =>
            _data.Value.RequestNo = requestId;

        public class Data
        {
            public int InstanceId { get; set; } = -1;
            public IPAddress ClientRemoteAddress { get; set; } = IPAddress.Any;
            public int ConnectionId { get; set; }
            public long RequestNo { get; set; }
        }
    }
}