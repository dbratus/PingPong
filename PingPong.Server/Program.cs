using System;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NLog.Config;
using NLog.Targets;
using PingPong.Engine;

namespace PingPong.Server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var config = new LoggingConfiguration();

            var consoleErrorTarget = new ConsoleTarget("consoleInfo");
            consoleErrorTarget.Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}\n${exception:format=tostring}";

            config.AddRule(LogLevel.Info, LogLevel.Fatal, new ConsoleTarget("consoleInfo"));
            config.AddRule(LogLevel.Error, LogLevel.Fatal, consoleErrorTarget);

            LogManager.Configuration = config;

            var serviceHost = new ServiceHost();
            var serviceHostStopped = new ManualResetEvent(false);

            // Handling SIGINT
            Console.CancelKeyPress += (_, args) => {
                serviceHost.Stop();
                args.Cancel = true;
            };

            // Handling SIGTERM
            AssemblyLoadContext.Default.Unloading += delegate {
                serviceHost.Stop();
                serviceHostStopped.WaitOne();
            };

            try
            {
                await serviceHost
                    .AddServiceAssembly(typeof(PingPong.Services.ContainerPivot).Assembly)
                    .Start(9999);
            }
            finally
            {
                serviceHostStopped.Set();
            }
        }
    }
}
