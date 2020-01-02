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
                (SquareResponse? response, RequestResult result) = await connection
                    .SendAsync<SquareRequest, SquareResponse>(new SquareRequest { Value = i });

                Assert.Equal(RequestResult.OK, result);
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
                    connection.Send(instanceId, new AddRequest { CounterId = counterId, Value = i });
                }
            });

            WaitForPendingRequests(connection);

            var startedAt = DateTime.Now;
            int receivedSumm = 0;

            while (expectedSum != receivedSumm)
            {
                Assert.True(DateTime.Now - startedAt < MaxTestTime);

                connection.Send<GetSumRequest, GetSumResponse>(instanceId, new GetSumRequest { CounterId = counterId }, (response, result) => {
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

            (InitSumResponse? initResponse, RequestResult initResult) = 
                await connection.SendAsync<InitSumRequest, InitSumResponse>();

            int instanceId = initResponse?.InstanceId ?? -1;
            int counterId = initResponse?.CounterId ?? -1;

            Assert.Equal(RequestResult.OK, initResult);

            for (int i = 0; i < requestCount; ++i)
            {
                expectedSum += i;
                RequestResult addResult = await connection.SendAsync(instanceId, new AddRequest { CounterId = counterId, Value = i });
                Assert.Equal(RequestResult.OK, addResult);
            }

            (GetSumResponse? getResponse, RequestResult getResult) = 
                await connection.SendAsync<GetSumRequest, GetSumResponse>(instanceId, new GetSumRequest { CounterId = counterId });

            Assert.Equal(RequestResult.OK, getResult);
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

            RequestResult publishResult = 
                await connection.SendAsync<PublishRequest>(new PublishRequest { Message = expectedMessage });

            Assert.Equal(RequestResult.OK, publishResult);

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            for (int instanceId = 0; instanceId < 2; ++instanceId)
            {
                (SubscriberOneGetMessageResponse? subscriberOneResponse, RequestResult subscriberOneResult) = 
                    await connection.SendAsync<SubscriberOneGetMessageRequest, SubscriberOneGetMessageResponse>();

                Assert.Equal(RequestResult.OK, subscriberOneResult);
                Assert.Equal(expectedMessage, subscriberOneResponse?.Message ?? "");

                (SubscriberTwoGetMessageResponse? subscriberTwoResponse, RequestResult subscriberTwoResult) = 
                    await connection.SendAsync<SubscriberTwoGetMessageRequest, SubscriberTwoGetMessageResponse>();

                Assert.Equal(RequestResult.OK, subscriberTwoResult);
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
            {
                RequestResult result = await connection.SendAsync<LoadBalancingInitRequest>(instanceId);
                Assert.Equal(RequestResult.OK, result);
            }

            const int requestsCount = 100;

            for (int i = 0; i < requestsCount; ++i)
            {
                RequestResult result = await connection.SendAsync<LoadBalancingRequest>();
                Assert.Equal(RequestResult.OK, result);
            }

            var requestsHandled = new List<int>();

            for (int instanceId = 0; instanceId < 2; ++instanceId)
            {
                (LoadBalancingGetStatsResponse? response, RequestResult result) = 
                    await connection.SendAsync<LoadBalancingGetStatsRequest, LoadBalancingGetStatsResponse>(instanceId);

                int value = response?.Result ?? 0;

                Assert.Equal(RequestResult.OK, result);
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

            (TransferMessageResponse? transferResponse, RequestResult transferResult) = 
                await connection.SendAsync<TransferMessageRequest, TransferMessageResponse>(new TransferMessageRequest {
                    Message = expectedMessage
                });

            Assert.Equal(RequestResult.OK, transferResult);

            (GetTransferredMessageResponse? getResponse, RequestResult getResult) =
                await connection.SendAsync<GetTransferredMessageRequest, GetTransferredMessageResponse>(transferResponse?.ServedByInstance ?? -1);

            Assert.Equal(RequestResult.OK, getResult);
            Assert.Equal(expectedMessage, getResponse?.Message ?? "");
        }
    }
}
