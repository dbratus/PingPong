#nullable enable

using System;
using System.Net;
using System.Threading;
using PingPong.Engine;
using PingPong.Messages;

namespace PingPong.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            using var connection = 
                new ClientConnection()
                .Connect(IPAddress.Loopback, 9999);

            const int requestCount = 10;
            for (int i = 0; i < requestCount; ++i)
            {
                int arg = i;
                connection.Send(new SquareRequest { Value = arg }, (SquareResponse response, bool isError) => {
                    Console.WriteLine($"Square response received {arg}^2 = {response.Result}");
                });
            }

            for (int i = 0; i < requestCount; ++i)
                connection.Send(new AddRequest { Value = i });

            connection.Send(new GetSummRequest{}, (GetSummResponse response, bool isError) => {
                Console.WriteLine($"Summ {response.Result}");
            });

            for (int i = 0; i < requestCount; ++i)
                connection.Send(new WriteRequest { Message = $"Message {i}" });

            while (connection.HasPendingRequests)
            {
                connection.CheckInbox();
                Thread.Sleep(1);
            }
        }
    }
}
