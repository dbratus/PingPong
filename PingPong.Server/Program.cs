using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
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

            await new ServiceHost().Start(config);
            return 0;

            async Task<ServiceHostConfig> LoadConfig()
            {
                if (args.Length < 1)
                    throw new ArgumentException("No configuration file specified");

                using FileStream configFile = File.OpenRead(args[0]);

                return await JsonSerializer.DeserializeAsync<ServiceHostConfig>(configFile);
            }
        }
    }
}
