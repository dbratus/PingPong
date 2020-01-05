using System.Threading.Tasks;
using NLog;
using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.Services
{
    public class SenderService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly ICluster _cluster;

        public SenderService(ICluster cluster)
        {
            _cluster = cluster;
        }

        public async Task<TransferMessageResponse> TransferMessage(TransferMessageRequest request)
        {
            _logger.Info("Transferring message '{0}'.", request.Message);

            var response = await _cluster.SendAsync<ReceiveMessageRequest, ReceiveMessageResponse>(new ReceiveMessageRequest {
                Message = request.Message
            });

            return new TransferMessageResponse {
                ServedByInstance = response?.ServedByInstance ?? -1
            };
        } 
    }

    public class ReceiverService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly IReceiverServiceDatabase _db;
        private readonly ISession _session;

        public ReceiverService(IReceiverServiceDatabase db, ISession session)
        {
            _db = db;
            _session = session;
        }

        public ReceiveMessageResponse ReceiveMessage(ReceiveMessageRequest request)
        {
            _logger.Info("Transferred message received '{0}'.", request.Message);

            _db.Store(request.Message);

            return new ReceiveMessageResponse {
                ServedByInstance = _session.InstanceId
            };
        }

        public GetTransferredMessageResponse GetTransferredMessage(GetTransferredMessageRequest request)
        {
            string message = _db.Get();

            _logger.Info("Transferred message requested. '{0}' is available.", message);

            return new GetTransferredMessageResponse { Message = message };
        }

        public static void SetupContainer(IContainerBuilder containerBuilder, IConfig config)
        {
            containerBuilder.Register<ReceiverServiceDatabase, IReceiverServiceDatabase>();
        }
    }

    public interface IReceiverServiceDatabase
    {
        void Store(string message);
        string Get();
    }

    public class ReceiverServiceDatabase : IReceiverServiceDatabase
    {
        private string _storedMessage = "";

        public void Store(string message) =>
            _storedMessage = message;

        public string Get() =>
            _storedMessage;
    }
}