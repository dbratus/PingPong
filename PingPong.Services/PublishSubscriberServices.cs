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

            await _cluster.PublishAsync(new PublisherEvent {
                Message = request.Message
            });
        }
    }

    public class SubscriberOneService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        public void ReceiveEvent(PublisherEvent ev)
        {
            _logger.Info("Event handled '{0}'.", ev.Message);
        }
    }

    public class SubscriberTwoService
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        public void ReceiveEvent(PublisherEvent ev)
        {
            _logger.Info("Event handled '{0}'.", ev.Message);
        }
    }
}