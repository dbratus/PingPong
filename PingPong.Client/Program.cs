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

            using var gateway = new ClusterConnection(new [] { 
                "tls://localhost:10100",
                "tls://localhost:10101" 
            }, new ClusterConnectionSettings {
                Serializer = new SerializerJson(),
                TlsSettings = {
                    AllowSelfSignedCertificates = true
                }
            });

            gateway.Connect().Wait();

            for (int i = 0; i < 10; ++i)
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
