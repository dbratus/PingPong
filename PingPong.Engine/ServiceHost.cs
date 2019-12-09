#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using Autofac;
using NLog;

namespace PingPong.Engine
{
    public sealed class ServiceHost
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly List<Type> _serviceTypes = new List<Type>();

        public ServiceHost AddServiceAssembly(Assembly assembly)
        {
            _serviceTypes.AddRange(assembly.ExportedTypes.Where(t => t.Name.EndsWith("Service")));
            return this;
        }

        public async Task Start(int port)
        {
            using IContainer container = BuildContainer();
            using ILifetimeScope lifetimeScope = container.BeginLifetimeScope();
            
            var dispatcher = new ServiceDispatcher(container, _serviceTypes);
            var listenerSocket = new Socket(SocketType.Stream, ProtocolType.IP);

            LogDispatcher();

            listenerSocket.Bind(new IPEndPoint(IPAddress.Any, port));
            listenerSocket.Listen(10);

            _logger.Info("Server started. Listening {0}...", listenerSocket.LocalEndPoint.Serialize());

            while (true)
            {
                Socket connectionSocket = await listenerSocket.AcceptAsync();

                _logger.Info("Client connected {0}.", connectionSocket.RemoteEndPoint.Serialize());

                var connection = new ServerConnection(connectionSocket, dispatcher);
                
                ServeConnection(connection);
            }

            async void ServeConnection(ServerConnection connection)
            {
                try
                {
                    await connection.Serve();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Connection {0} faulted.", connection.Socket.RemoteEndPoint.Serialize());
                }
                finally
                {
                    _logger.Info("Client disconnected {0}.", connection.Socket.RemoteEndPoint.Serialize());
                    connection.Dispose();
                }
            }

            void LogDispatcher()
            {
                _logger.Info("The following message handlers found:");
                foreach ((int requestId, int responseId) in dispatcher.GetRequestResponseMap())
                {
                    Type requestType = dispatcher.MessageMap.GetMessageTypeById(requestId);
                    Type? responseType = responseId > 0 ? dispatcher.MessageMap.GetMessageTypeById(responseId) : null;

                    if (responseType != null)
                        _logger.Info("  {0} -> {1}", requestType.Name, responseType.Name);
                    else
                        _logger.Info("  {0}", requestType.Name);
                }
            }
        }

        private IContainer BuildContainer()
        {
            var containerBuilder = new ContainerBuilder();
            
            containerBuilder
                .RegisterTypes(_serviceTypes.ToArray())
                .InstancePerLifetimeScope();
            
            return containerBuilder.Build();
        }
    }
}
