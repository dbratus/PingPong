using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using PingPong.Engine;
using PingPong.HostInterfaces;
using PingPong.Messages;
using Xunit;

namespace PingPong.Test
{
    public class GatewayTest : BaseTest
    {
        [Fact]
        public void Squares()
        {
            using var connection = ConnectGateway();

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
        public void Sum()
        {
            using var connection = ConnectGateway();

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
                    connection.Send(new AddRequestRouted { CounterId = counterId, InstanceId = instanceId, Value = i });
                }
            });

            WaitForPendingRequests(connection);

            var startedAt = DateTime.Now;
            int receivedSumm = 0;

            while (expectedSum != receivedSumm)
            {
                Assert.True(DateTime.Now - startedAt < MaxTestTime);

                connection.Send<GetSumRequestRouted, GetSumResponse>(new GetSumRequestRouted{ CounterId = counterId, InstanceId = instanceId }, (response, result) => {
                    Assert.Equal(RequestResult.OK, result);

                    receivedSumm = response?.Result ?? 0;
                });

                WaitForPendingRequests(connection);
            }
        }

        [Fact]
        public void StreamingTest()
        {
            using var connection = ConnectGateway();

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
    }
}