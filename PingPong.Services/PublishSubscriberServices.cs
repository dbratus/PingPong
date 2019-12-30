using System;
using System.Threading.Tasks;
using NLog;
using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.Services
{
    public class PublisherService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly ICluster _cluster;

        public PublisherService(ICluster cluster)
        {
            _cluster = cluster;
        }

        public async Task Publish(PublishRequest request)
        {
            _logger.Info("Publishing request received '{0}'.", request.Message);

            RequestResult result = await _cluster.PublishAsync(new PublisherEvent {
                Message = request.Message
            });

            if (result != RequestResult.OK)
                throw new Exception($"Publish failed {result}");
        }
    }

    public class SubscriberOneService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private string _lastMessage = "";

        public void ReceiveEvent(PublisherEvent ev)
        {
            _lastMessage = ev.Message;
            _logger.Info("Event handled '{0}'.", ev.Message);
        }

        public SubscriberOneGetMessageResponse GetMessage(SubscriberOneGetMessageRequest request)
        {
            _logger.Info("Message requested. '{0}' is available.", _lastMessage);
            return new SubscriberOneGetMessageResponse { Message = _lastMessage };
        }
    }

    public class SubscriberTwoService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private string _lastMessage = "";

        public void ReceiveEvent(PublisherEvent ev)
        {
            _lastMessage = ev.Message;
            _logger.Info("Event handled '{0}'.", ev.Message);
        }

        public SubscriberTwoGetMessageResponse GetMessage(SubscriberTwoGetMessageRequest request)
        {
            _logger.Info("Message requested. '{0}' is available.", _lastMessage);
            return new SubscriberTwoGetMessageResponse { Message = _lastMessage };
        }
    }
}