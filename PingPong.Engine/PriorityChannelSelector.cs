using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    sealed class PriorityChannelSelector<T>
    {
        private const int PrioritiesCount = 5;
        private const int PriorityTrafficLimit = 3;

        private readonly Channel<T>[] _channels = 
            new Channel<T>[PrioritiesCount];
        private readonly int[] _trafficLimits =
            new int[PrioritiesCount];
        private readonly Channel<bool> _notificationsChannel = 
            Channel.CreateUnbounded<bool>(new UnboundedChannelOptions {
                SingleReader = true
            });

        public PriorityChannelSelector()
        {
            for (int i = 0; i < PrioritiesCount; ++i)
            {
                _channels[i] = Channel.CreateUnbounded<T>(new UnboundedChannelOptions {
                    SingleReader = true
                });
                _trafficLimits[i] = PriorityTrafficLimit;
            }
        }

        public void WriteComplete() =>
            _notificationsChannel.Writer.Complete();

        public void Write(MessagePriority priority, T item)
        {
            _channels[PriorityToIndex(priority)].Writer.TryWrite(item);
            _notificationsChannel.Writer.TryWrite(true);
        }

        public async ValueTask WriteAsync(MessagePriority priority, T item)
        {
            await _channels[PriorityToIndex(priority)].Writer.WriteAsync(item);
            await _notificationsChannel.Writer.WriteAsync(true);
        }

        public bool TryRead(out T item)
        {
            #pragma warning disable CS8601, CS8653
            item = default(T);
            #pragma warning restore CS8601, CS8653

            if (!_notificationsChannel.Reader.TryRead(out bool notification))
                return false;

            item = ReadWithPriority();
            return true;
        }

        public async Task<T> ReadAsync()
        {
            await _notificationsChannel.Reader.ReadAsync();
            return ReadWithPriority();
        }

        private T ReadWithPriority()
        {
            bool trafficLimitReached;

            do
            {
                trafficLimitReached = false;

                for (int i = 0; i < PrioritiesCount; ++i)
                {
                    if (_trafficLimits[i] == 0)
                    {
                        _trafficLimits[i] = PriorityTrafficLimit;
                        trafficLimitReached = true;
                        continue;
                    }

                    T result;
                    if (!_channels[i].Reader.TryRead(out result))
                        continue;

                    --_trafficLimits[i];
                    return result;
                }
            }
            while (trafficLimitReached);

            throw new InvalidOperationException("It is expected that at least one channel is not empty.");
        }

        private static int PriorityToIndex(MessagePriority priority) =>
            (PrioritiesCount - 1) / 2 - (int)priority;
    }
}