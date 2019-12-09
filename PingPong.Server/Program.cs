using System;
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

            await new ServiceHost()
                .AddServiceAssembly(typeof(PingPong.Services.ContainerPivot).Assembly)
                .Start(9999);
        }
    }
}
