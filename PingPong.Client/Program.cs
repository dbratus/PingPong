using System;
using System.Threading;
using System.Threading.Tasks;
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

            string[] uris;
            ISerializer serializer;

            if (args.Length < 1 || args[0] == "cluster")
            {
                uris = new [] { 
                    "tcp://localhost:10000",
                    "tcp://localhost:10001" 
                };
                serializer = new SerializerMessagePack();
            }
            else if (args[0] == "gateway")
            {
                uris = new [] { 
                    "tls://localhost:10100",
                    "tls://localhost:10101" 
                };
                serializer = new SerializerJson();
            }
            else
            {
                return;
            }

            using var connection = new ClusterConnection(uris, new ClusterConnectionSettings {
                Serializer = serializer,
                TlsSettings = {
                    AllowSelfSignedCertificates = true
                }
            });

            connection.Connect().Wait();

            bool stop = false;

            Console.CancelKeyPress += (sender, args) => {
                Volatile.Write(ref stop, true);
                args.Cancel = true;
            };

            Task updateTask = Task.Run(() => {
                while (!stop)
                {
                    connection.Update();
                    Thread.Yield();
                }
            });

            int requestsProcessed = 0;
            var started = DateTime.Now;

            while (!stop)
            {
                connection.Send<EchoMessage, EchoMessage>(new EchoMessage { Message = "Message" }, (response, result) => {
                    if (result != RequestResult.OK)
                    {
                        Console.WriteLine($"Error {result}.");
                        return;
                    }

                    Interlocked.Increment(ref requestsProcessed);
                });
                Thread.Yield();
            }

            updateTask.Wait();

            TimeSpan totalTime = DateTime.Now - started;
            double requestsPerSec = requestsProcessed / totalTime.TotalSeconds;

            Console.WriteLine();
            Console.WriteLine($"{requestsProcessed} in {totalTime}. {requestsPerSec} requests per second");

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
