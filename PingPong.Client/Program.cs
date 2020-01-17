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

            int requestsSent = 0;
            int requestsProcessed = 0;
            const double maxPendingRequestsPct = 0.15;
            var started = DateTime.Now;

            Console.WriteLine("Sending requests...");

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

                ++requestsSent;

                if (1.0 - (double)Volatile.Read(ref requestsProcessed)/requestsSent > maxPendingRequestsPct)
                {
                    while (!stop && Volatile.Read(ref requestsProcessed) < requestsSent)
                        Thread.Sleep(1);
                }
                else
                {
                    Thread.Yield();
                }
            }

            updateTask.Wait();

            TimeSpan totalTime = DateTime.Now - started;
            double requestsPerSec = requestsProcessed / totalTime.TotalSeconds;

            Console.WriteLine();
            Console.WriteLine($"{requestsProcessed}/{requestsSent} {100.0*requestsProcessed/requestsSent:f0}% in {totalTime}. {requestsPerSec:f0} requests per second");

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
