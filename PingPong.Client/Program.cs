using System;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;
using PingPong.Engine;
using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            InitLogging();

            using var connection = new ClusterConnection(new [] { 
                "tcp://localhost:10000",
                "tcp://localhost:10001" 
            }, new ClusterConnectionSettings {
                TlsSettings = {
                    AllowSelfSignedCertificates = true
                }
            });

            connection.Connect().Wait();

            const int requestCount = 10;
            for (int i = 0; i < requestCount; ++i)
            {
                int arg = i;
                connection.Send(new SquareRequest { Value = arg }, (SquareResponse? response, RequestResult result) => {
                    Console.WriteLine($"Square response received {arg}^2 = {response?.Result}");
                });
            }

            for (int i = 0; i < requestCount; ++i)
                connection.Send(0, new AddRequest { Value = i });

            connection.Send(0, new GetSummRequest{}, (GetSummResponse? response, RequestResult result) => {
                Console.WriteLine($"Summ {response?.Result}");
            });

            for (int i = 0; i < requestCount; ++i)
                connection.Send(new WriteRequest { Message = $"Message {i}" });

            connection.Send(new GetConfigValueRequest{}, (GetConfigValueResponse? response, RequestResult result) => {
                Console.WriteLine($"Configurable service returned '{response?.Value}'.");
            });

            connection.Send(new TransferMessageRequest { Message = "Transferred message" }, (TransferMessageResponse? response, RequestResult result) => {
                if (result == RequestResult.OK)
                {
                    connection.Send(response?.ServedByInstance ?? -1, new GetTransferredMessageRequest {}, (GetTransferredMessageResponse? response, RequestResult result) => {
                        if (result == RequestResult.OK)
                        {
                            Console.WriteLine($"Transferred message returned '{response?.Message}'.");
                        }
                    });
                }
            });

            connection.Send(new PublishRequest {
                Message = "Published message"
            }, result => {});

            while (connection.HasPendingRequests)
            {
                connection.Update();
                Thread.Sleep(1);
            }

            using var gateway = new ClusterConnection(new [] { 
                "tls://localhost:10100",
                "tls://localhost:10101" 
            }, new ClusterConnectionSettings {
                TlsSettings = {
                    AllowSelfSignedCertificates = true
                }
            });

            gateway.Connect().Wait();

            for (int i = 0; i < requestCount; ++i)
            {
                int arg = i;
                gateway.Send(new SquareRequest { Value = arg }, (SquareResponse? response, RequestResult result) => {
                    Console.WriteLine($"Square response received from the gateway {arg}^2 = {response?.Result}");
                });
            }

            while (gateway.HasPendingRequests)
            {
                gateway.Update();
                Thread.Sleep(1);
            }

            void InitLogging()
            {
                var config = new LoggingConfiguration();

                var consoleTarget = new ConsoleTarget("consoleInfo");
                consoleTarget.Layout = "${message}";

                var consoleErrorTarget = new ConsoleTarget("consoleInfo");
                consoleErrorTarget.Layout = "${message}\n${exception:format=tostring}";

                config.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget);
                config.AddRule(LogLevel.Error, LogLevel.Fatal, consoleErrorTarget);

                LogManager.Configuration = config;
            }
        }
    }
}
