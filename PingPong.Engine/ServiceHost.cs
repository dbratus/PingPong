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

            LogManager.Configuration.Variables["instanceId"] = config.InstanceId.ToString();

            try
            {
                var session = new Session();
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

                    _logger.Info("Client connected {0}.", ((IPEndPoint)connectionSocket.RemoteEndPoint).Address);

                    ServeConnection(new ServerConnection(connectionSocket, dispatcher, config));
                }

                Volatile.Write(ref invokeCallbacks, false);

                await clusterConnectionTask;
                await callbacksInvocationTask;

                async void ServeConnection(ServerConnection connection)
                {
                    var clientRemoteAddress = ((IPEndPoint)connection.Socket.RemoteEndPoint).Address;

                    try
                    {
                        session.SetData(new Session.Data {
                            InstanceId = config.InstanceId,
                            ClientRemoteAddress = clientRemoteAddress
                        });

                        await connection.Serve();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Connection {0} faulted.", clientRemoteAddress);
                    }
                    finally
                    {
                        _logger.Info("Client disconnected {0}.", clientRemoteAddress);
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
                    var serviceConfigsProvider = new ServiceConfigsProvider(config.ServiceConfigs);
                    
                    containerBuilder
                        .RegisterTypes(_serviceTypes.ToArray())
                        .SingleInstance();
                    containerBuilder
                        .RegisterInstance(serviceConfigsProvider)
                        .As<IConfig>();
                    containerBuilder
                        .RegisterInstance(clusterConnection)
                        .As<ICluster>()
                        .ExternallyOwned();
                    containerBuilder
                        .RegisterInstance(session)
                        .As<ISession>();
                    
                    var containerBuilderWrapper = new ContainerBuilderWrapper(containerBuilder);

                    foreach (Type serviceType in _serviceTypes)
                    {
                        try
                        {
                            serviceType
                                .GetMethods()
                                .FirstOrDefault(IsContainerSetupMethod)
                                ?.Invoke(null, new object[] { containerBuilderWrapper, serviceConfigsProvider });
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Service {0} container setup faulted.", serviceType.FullName);
                        }
                    }

                    return containerBuilder.Build();

                    bool IsContainerSetupMethod(MethodInfo methodInfo)
                    {
                        if (!(methodInfo.IsStatic && methodInfo.IsPublic))
                            return false;

                        ParameterInfo[] methodParams = methodInfo.GetParameters();
                        if (methodParams.Length != 2)
                            return false;

                        if (methodParams[0].ParameterType != typeof(IContainerBuilder))
                            return false;
                        
                        if (methodParams[1].ParameterType != typeof(IConfig))
                            return false;

                        return true;
                    }
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

        private sealed class ContainerBuilderWrapper : IContainerBuilder
        {
            private readonly ContainerBuilder _containerBuilder;

            public ContainerBuilderWrapper(ContainerBuilder containerBuilder)
            {
                _containerBuilder = containerBuilder;
            }

            public void Register<T>() =>
                _containerBuilder
                    .RegisterType<T>()
                    .SingleInstance();

            public void Register<T, TInterface>()
                where T: TInterface =>
                _containerBuilder
                    .RegisterType<T>()
                    .As<TInterface>()
                    .SingleInstance();
        }
    }
}
