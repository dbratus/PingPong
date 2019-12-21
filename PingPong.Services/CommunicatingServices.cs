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

            (var response, RequestResult result) = await _cluster.Send<ReceiveMessageRequest, ReceiveMessageResponse>(new ReceiveMessageRequest {
                Message = request.Message
            });

            if (result != RequestResult.OK)
                _logger.Error("Failed to transfer the message");

            return new TransferMessageResponse {};
        } 
    }

    public class ReceiverService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private string _lastMessageReceived = "";

        public ReceiverService()
        {
            _logger.Info("Instance created");
        }

        public ReceiveMessageResponse ReceiveMessage(ReceiveMessageRequest request)
        {
            _logger.Info("Transferred message received '{0}'.", request.Message);

            _lastMessageReceived = request.Message;
            return new ReceiveMessageResponse {};
        }

        public GetTransferredMessageResponse GetTransferredMessage(GetTransferredMessageRequest request)
        {
            _logger.Info("Transferred message requested. '{0}' is available.", _lastMessageReceived);

            return new GetTransferredMessageResponse { Message = _lastMessageReceived };
        }
    }
}