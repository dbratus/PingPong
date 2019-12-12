using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using NLog;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    public sealed class ServiceHost
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly List<Type> _serviceTypes = new List<Type>();

        private Socket? _listeningSocket;

        public ServiceHost AddServiceAssembly(Assembly assembly)
        {
            _serviceTypes.AddRange(assembly.ExportedTypes.Where(t => t.Name.EndsWith("Service")));
            return this;
        }

        public async Task Start(ServiceHostConfig config)
        {
            if (_listeningSocket != null)
                throw new InvalidOperationException("Service host is already started.");

            await Task.Yield();

            {
                using IContainer container = BuildContainer();
                using ILifetimeScope lifetimeScope = container.BeginLifetimeScope();
                
                var dispatcher = new ServiceDispatcher(container, _serviceTypes);
                _listeningSocket = new Socket(SocketType.Stream, ProtocolType.IP);

                LogDispatcher();

                _listeningSocket.Bind(new IPEndPoint(IPAddress.Any, config.Port));
                _listeningSocket.Listen(10);

                _logger.Info("Server started. Listening port {0}...", config.Port);

                while (true)
                {
                    Socket connectionSocket;
                    try
                    {
                        connectionSocket = await _listeningSocket.AcceptAsync();
                    }
                    catch (SocketException)
                    {
                        _logger.Info("The listening socket is closed. The host is going to shutdown.");
                        break;
                    }

                    _logger.Info("Client connected {0}.", connectionSocket.RemoteEndPoint.Serialize());

                    ServeConnection(new ServerConnection(connectionSocket, dispatcher));
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

                IContainer BuildContainer()
                {
                    var containerBuilder = new ContainerBuilder();
                    
                    containerBuilder
                        .RegisterTypes(_serviceTypes.ToArray())
                        .InstancePerLifetimeScope();
                    containerBuilder
                        .RegisterInstance(new ServiceConfigsProvider(config.ServiceConfigs))
                        .As<IConfig>();
                    
                    return containerBuilder.Build();
                }
            }

            _logger.Info("Service host stopped.");
        }

        public void Stop()
        {
            Socket? socket = Interlocked.CompareExchange(ref _listeningSocket, null, _listeningSocket);

            if (socket == null)
                return;

            socket.Close();
        }
    }
}
