using System;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
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
        static async Task<int> Main(string[] args)
        {
            ServiceHostConfig config;
            try 
            {
                 config = await LoadConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load config\n{ex}");
                return 1;
            };

            InitLogging();

            var serviceHost = new ServiceHost();
            var serviceHostStopped = new ManualResetEvent(false);

            InitSignalHandlers();

            try
            {
                await serviceHost
                    .AddServiceAssembly(typeof(PingPong.Services.ContainerPivot).Assembly)
                    .Start(config);
            }
            finally
            {
                LogManager.Flush();

                serviceHostStopped.Set();
            }

            return 0;

            async Task<ServiceHostConfig> LoadConfig()
            {
                if (args.Length < 1)
                    throw new ArgumentException("No configuration file specified");

                using FileStream configFile = File.OpenRead(args[0]);

                return await JsonSerializer.DeserializeAsync<ServiceHostConfig>(configFile);
            }

            void InitLogging()
            {
                var config = new LoggingConfiguration();

                var consoleErrorTarget = new ConsoleTarget("consoleInfo");
                consoleErrorTarget.Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}";

                config.AddRule(LogLevel.Trace, LogLevel.Info, new ConsoleTarget("consoleInfo"));
                config.AddRule(LogLevel.Error, LogLevel.Fatal, consoleErrorTarget);

                LogManager.Configuration = config;
            }

            void InitSignalHandlers()
            {
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
            }
        }
    }
}
