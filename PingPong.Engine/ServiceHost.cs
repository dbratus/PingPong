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

            try
            {
                using ClusterConnection clusterConnection = CreateClusterConnection();
                using IContainer container = BuildContainer();
                
                var dispatcher = new ServiceDispatcher(container, _serviceTypes);
                _listeningSocket = new Socket(SocketType.Stream, ProtocolType.IP);
                _listeningSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                LogDispatcher();

                _listeningSocket.Bind(new IPEndPoint(IPAddress.Any, config.Port));
                _listeningSocket.Listen(128);

                _logger.Info("Server started. Listening port {0}...", config.Port);

                bool invokeCallbacks = true;

                Task clusterConnectionTask = ConnectCluster();
                Task callbacksInvocationTask = UpdateClusterConnection();

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

                Volatile.Write(ref invokeCallbacks, false);

                await clusterConnectionTask;
                await callbacksInvocationTask;

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

                ClusterConnection CreateClusterConnection() =>
                    new ClusterConnection(config.KnownHosts, new ClusterConnectionSettings {
                        ReconnectionDelay = TimeSpan.FromSeconds(config.ClusterConnectionSettings.ReconnectionDelay)
                    });

                async Task ConnectCluster()
                {
                    _logger.Info("Connecting cluster after {0} sec.", config.ClusterConnectionSettings.ConnectionDelay);

                    try
                    {
                        await clusterConnection.Connect(TimeSpan.FromSeconds(config.ClusterConnectionSettings.ConnectionDelay));
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to connect cluster. Interservice communication is not available.");
                    }
                }

                async Task UpdateClusterConnection()
                {
                    try
                    {
                        while (Volatile.Read(ref invokeCallbacks))
                        {
                            clusterConnection.Update();

                            await Task.Delay(TimeSpan.FromMilliseconds(config.ClusterConnectionSettings.InvokeCallbacksPeriodMs));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Server callbacks are just async completions. They are not expected to throw exceptions.
                        _logger.Fatal(ex, "Callbacks invocation faulted. No more callbacks will be invoked.");
                    }
                }

                IContainer BuildContainer()
                {
                    var containerBuilder = new ContainerBuilder();
                    
                    containerBuilder
                        .RegisterTypes(_serviceTypes.ToArray())
                        .SingleInstance();
                    containerBuilder
                        .RegisterInstance(new ServiceConfigsProvider(config.ServiceConfigs))
                        .As<IConfig>();
                    containerBuilder
                        .RegisterInstance(new ClusterConnectionWrapper(clusterConnection))
                        .As<ICluster>();
                    
                    return containerBuilder.Build();
                }
            }
            finally
            {
                _listeningSocket?.Dispose();
                _listeningSocket = null;
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

        private sealed class ClusterConnectionWrapper : ICluster
        {
            private readonly ClusterConnection _connection;

            public ClusterConnectionWrapper(ClusterConnection connection)
            {
                _connection = connection;
            }

            public void Send<TRequest>(TRequest request) 
                where TRequest : class =>
                _connection.Send(request);

            public Task<(TResponse?, RequestResult)> Send<TRequest, TResponse>(TRequest request)
                where TRequest : class
                where TResponse : class =>
                _connection.SendAsync<TRequest, TResponse>(request);

            public Task<(TResponse?, RequestResult)> Send<TRequest, TResponse>()
                where TRequest : class
                where TResponse : class =>
                _connection.SendAsync<TRequest, TResponse>();
        }
    }
}
