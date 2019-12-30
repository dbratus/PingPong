using System;
using System.Threading;
using System.Threading.Tasks;
using PingPong.Engine;

namespace PingPong.Test
{
    public class BaseTest
    {
        protected static readonly TimeSpan MaxTestTime = TimeSpan.FromSeconds(3);

        protected ClusterConnection ConnectCluster() =>
            CreateConnection(new [] { 
                "tcp://localhost:10000",
                "tcp://localhost:10001" 
            }, new SerializerMessagePack());

        protected ClusterConnection ConnectGateway() =>
            CreateConnection(new [] { 
                "tls://localhost:10100",
                "tls://localhost:10101" 
            }, new SerializerJson());

        private ClusterConnection CreateConnection(string[] uris, ISerializer seriaizer)
        {
            var connection = new ClusterConnection(uris, new ClusterConnectionSettings {
                Serializer = seriaizer,
                TlsSettings = {
                    AllowSelfSignedCertificates = true
                }
            });

            connection.Connect().Wait();
            return connection;
        }

        protected void WaitForPendingRequests(ClusterConnection connection, double mult = 1.0)
        {
            var startedAt = DateTime.Now;

            while (connection.HasPendingRequests)
            {
                if (DateTime.Now - startedAt > MaxTestTime * mult)
                    throw new Exception("Test timeout");

                connection.Update();

                Thread.Yield();
            }
        }

        protected void WaitForTaskCompletion(ClusterConnection connection, Task task, double mult = 1.0)
        {
            var startedAt = DateTime.Now;

            while (!task.IsCompleted)
            {
                if (DateTime.Now - startedAt > MaxTestTime * mult)
                    throw new Exception("Test timeout");

                connection.Update();

                Thread.Yield();
            }
        }
    }
}