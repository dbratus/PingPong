using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using PingPong.Engine;
using PingPong.HostInterfaces;
using PingPong.Messages;
using Xunit;

namespace PingPong.Test
{
    public class ClusterTest : BaseTest
    {
        [Fact]
        public void Squares()
        {
            using var connection = ConnectCluster();

            const int requestCount = 10;
            int responseCount = 0;

            for (int i = 0; i < requestCount; ++i)
            {
                int arg = i;
                connection.Send(new SquareRequest { Value = arg }, (SquareResponse? response, RequestResult result) => {
                    Assert.Equal(RequestResult.OK, result);
                    Assert.Equal(arg * arg, response?.Result ?? 0);

                    ++responseCount;
                });
            }

            WaitForPendingRequests(connection);

            Assert.Equal(requestCount, responseCount);
        }

        [Fact]
        public void SquaresAsync()
        {
            using var connection = ConnectCluster();

            Task testTask = SquaresAsyncTask(connection);
            WaitForTaskCompletion(connection, testTask);

            testTask.Wait();
        }

        private async Task SquaresAsyncTask(ClusterConnection connection)
        {
            const int requestCount = 10;

            for (int i = 0; i < requestCount; ++i)
            {
                var response = await connection
                    .SendAsync<SquareRequest, SquareResponse>(new SquareRequest { Value = i });

                Assert.Equal(i * i, response?.Result ?? 0);
            }
        }

        [Fact]
        public void Sum()
        {
            using var connection = ConnectCluster();

            const int requestCount = 10;
            int expectedSum = -1;
            int instanceId = -1;
            int counterId = -1;

            connection.Send<InitSumRequest, InitSumResponse>((response, result) => {
                Assert.Equal(RequestResult.OK, result);

                instanceId = response?.InstanceId ?? -1;
                counterId = response?.CounterId ?? -1;

                expectedSum = 0;
                for (int i = 0; i < requestCount; ++i)
                {
                    expectedSum += i;
                    connection.Send(
                        new DeliveryOptions {
                            InstanceId = instanceId
                        }, 
                        new AddRequest { 
                            CounterId = counterId, 
                            Value = i 
                        }
                    );
                }
            });

            WaitForPendingRequests(connection);

            var startedAt = DateTime.Now;
            int receivedSumm = 0;

            while (expectedSum != receivedSumm)
            {
                Assert.True(DateTime.Now - startedAt < MaxTestTime);

                var options = new DeliveryOptions { InstanceId = instanceId };
                connection.Send<GetSumRequest, GetSumResponse>(options, new GetSumRequest { CounterId = counterId }, (response, result) => {
                    Assert.Equal(RequestResult.OK, result);

                    receivedSumm = response?.Result ?? 0;
                });

                WaitForPendingRequests(connection);
            }
        }

        [Fact]
        public void SequentialSum()
        {
            using var connection = ConnectCluster();

            Task testTask = SequentialSumTask(connection);
            WaitForTaskCompletion(connection, testTask);

            testTask.Wait();
        }

        private async Task SequentialSumTask(ClusterConnection connection)
        {
            const int requestCount = 10;
            int expectedSum = 0;

            var initResponse = 
                await connection.SendAsync<InitSumRequest, InitSumResponse>();

            int instanceId = initResponse?.InstanceId ?? -1;
            int counterId = initResponse?.CounterId ?? -1;

            for (int i = 0; i < requestCount; ++i)
            {
                expectedSum += i;
                await connection.SendAsync(new DeliveryOptions { InstanceId = instanceId }, new AddRequest { CounterId = counterId, Value = i });
            }

            var getResponse = 
                await connection.SendAsync<GetSumRequest, GetSumResponse>(
                    new DeliveryOptions { InstanceId = instanceId }, 
                    new GetSumRequest { CounterId = counterId }
                );

            Assert.Equal(expectedSum, getResponse?.Result ?? 0);
        }

        [Fact]
        public void PublishSubscribe()
        {
            using var connection = ConnectCluster();

            Task testTask = PublishSubscribeTask(connection);
            WaitForTaskCompletion(connection, testTask);

            testTask.Wait();
        }

        private async Task PublishSubscribeTask(ClusterConnection connection)
        {
            const string expectedMessage = "Test message";

            await connection.SendAsync<PublishRequest>(new PublishRequest { Message = expectedMessage });
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            for (int instanceId = 0; instanceId < 2; ++instanceId)
            {
                var subscriberOneResponse = 
                    await connection.SendAsync<SubscriberOneGetMessageRequest, SubscriberOneGetMessageResponse>();

                Assert.Equal(expectedMessage, subscriberOneResponse?.Message ?? "");

                var subscriberTwoResponse = 
                    await connection.SendAsync<SubscriberTwoGetMessageRequest, SubscriberTwoGetMessageResponse>();

                Assert.Equal(expectedMessage, subscriberTwoResponse?.Message ?? "");
            }
        }

        [Fact]
        public void StreamingTest()
        {
            using var connection = ConnectCluster();

            Task testTask = StreamingTestTask(connection);
            WaitForTaskCompletion(connection, testTask);

            testTask.Wait();
        }

        private async Task StreamingTestTask(ClusterConnection connection)
        {
            int responsesCount = 10;

            ChannelReader<(StreamingResponse?, RequestResult)> channel = 
                connection.OpenChannelAsync<StreamingRequest, StreamingResponse>(
                    new StreamingRequest { Count = responsesCount }
                );

            for (int i = responsesCount; i >= 0; --i)
            {
                (StreamingResponse? response, RequestResult result) =
                    await channel.ReadAsync();

                Assert.Equal(RequestResult.OK, result);
                Assert.Equal(i, response?.Value ?? -1);
            }

            await Assert.ThrowsAsync<ChannelClosedException>(async ()=> {
                await channel.ReadAsync();
            });
        }

        [Fact]
        public void ConfigValue()
        {
            using var connection = ConnectCluster();

            connection.Send<GetConfigValueRequest, GetConfigValueResponse>((response, result) => {
                Assert.Equal(RequestResult.OK, result);
                Assert.Equal("Value from config", response?.Value ?? "");
            });

            WaitForPendingRequests(connection);
        }

        [Fact]
        public void LoadBalancing()
        {
            using var connection = ConnectCluster();

            Task testTask = LoadBalancingTask(connection);
            WaitForTaskCompletion(connection, testTask, 10);

            testTask.Wait();
        }

        private async Task LoadBalancingTask(ClusterConnection connection)
        {
            for (int instanceId = 0; instanceId < 2; ++instanceId)
                await connection.SendAsync<LoadBalancingInitRequest>(new DeliveryOptions { InstanceId = instanceId });

            const int requestsCount = 100;

            for (int i = 0; i < requestsCount; ++i)
                await connection.SendAsync<LoadBalancingRequest>();

            var requestsHandled = new List<int>();

            for (int instanceId = 0; instanceId < 2; ++instanceId)
            {
                var response = await connection
                    .SendAsync<LoadBalancingGetStatsRequest, LoadBalancingGetStatsResponse>(new DeliveryOptions { InstanceId = instanceId });

                int value = response?.Result ?? 0;

                Assert.True(value > 0);

                requestsHandled.Add(value);
            }

            Assert.Equal(2, requestsHandled.Count);
            Assert.Equal(requestsCount, requestsHandled.Sum());
            Assert.True((double)Math.Abs(requestsHandled[0] - requestsHandled[1]) / requestsCount < 0.25);
        }

        [Fact]
        public void ServiceCommunication()
        {
            using var connection = ConnectCluster();

            Task testTask = ServiceCommunicationTask(connection);
            WaitForTaskCompletion(connection, testTask, 10);

            testTask.Wait();
        }

        private async Task ServiceCommunicationTask(ClusterConnection connection)
        {
            const string expectedMessage = "Test message";

            var transferResponse = await connection
                .SendAsync<TransferMessageRequest, TransferMessageResponse>(new TransferMessageRequest {
                    Message = expectedMessage
                });

            var options = new DeliveryOptions {
                InstanceId = transferResponse?.ServedByInstance ?? -1
            };
            var getResponse = await connection
                .SendAsync<GetTransferredMessageRequest, GetTransferredMessageResponse>(options);

            Assert.Equal(expectedMessage, getResponse?.Message ?? "");
        }

        [Fact]
        public void Priorities()
        {
            using var connection = ConnectCluster();

            var priorities = new MessagePriority[] {
                MessagePriority.Highest,
                MessagePriority.High,
                MessagePriority.Normal,
                MessagePriority.Low,
                MessagePriority.Lowest
            };
            var rng = new Random();
            var responses = new List<int>();

            for (int i = 0; i < 100; ++i)
            {
                var options = new DeliveryOptions {
                    Priority = priorities[rng.Next(priorities.Length)]
                };
                connection.Send(options, new PriorityRequest { Priority = 2 + (int)options.Priority }, (PriorityResponse ?response, RequestResult result) => {
                    responses.Add(response?.Priority ?? -1);
                });
            }

            WaitForPendingRequests(connection);

            Assert.Equal(100, responses.Count);
        }
    }
}
