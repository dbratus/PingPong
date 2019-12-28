using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using NLog;
using NLog.Config;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    public sealed class ServiceHost
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly List<Type> _serviceTypes = new List<Type>();

        private Socket? _listeningSocket;
        private volatile bool _updateClusterConnection = true;
        private readonly ManualResetEvent _serviceHostStopped = new ManualResetEvent(false);

        public ServiceHost AddServiceAssembly(Assembly assembly)
        {
            _serviceTypes.AddRange(assembly.ExportedTypes.Where(t => t.Name.EndsWith("Service")));
            return this;
        }

        public async Task Start(ServiceHostConfig config)
        {
            if (_listeningSocket != null)
                throw new InvalidOperationException("Service host is already started.");

            try
            {
                ConfigureLogger(config);
                LoadMessageAssemblies(config.MessageAssemblies);
                LoadServiceAssemblies(config.ServiceAssemblies);
                InitSignalHandlers();

                X509Certificate? certificate = await LoadCertificate(config);
                var session = new Session();
                var counters = new ServiceHostCounters();

                using ClusterConnection clusterConnection = CreateClusterConnection(config);
                using IContainer container = BuildContainer(config, clusterConnection, session);
                
                var dispatcher = new ServiceDispatcher(container, _serviceTypes, clusterConnection);

                _listeningSocket = new Socket(SocketType.Stream, ProtocolType.IP);
                _listeningSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                _listeningSocket.Bind(new IPEndPoint(IPAddress.Any, config.Port));
                _listeningSocket.Listen(128);

                if (config.Gateway)
                    _logger.Info("Server started as gateway. Listening port {0}...", config.Port);
                else
                    _logger.Info("Server started. Listening port {0}...", config.Port);

                Task clusterConnectionTask = ConnectCluster(config, clusterConnection);
                Task callbacksInvocationTask = UpdateClusterConnection(config, clusterConnection);

                // If it is a gateway, the host waits for the cluster connection to obtain
                // message maps, initializes routes and only then accepts connections.
                //
                // The first connection attempt tries to connect only once, so it is assumed
                // that the cluster itself is completely up before all its gateways.
                if (config.Gateway)
                {
                    await clusterConnectionTask;
                    dispatcher.InitGatewayRouts();
                }

                LogDispatcher(dispatcher);

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

                    ServeConnection(new ServerConnection(connectionSocket, dispatcher, config, counters, certificate), config, session);
                }

                _updateClusterConnection = false;

                await clusterConnectionTask;
                await callbacksInvocationTask;
            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, "Service host faulted.");
            }
            finally
            {
                _listeningSocket?.Dispose();
                _listeningSocket = null;

                _logger.Info("Service host stopped.");
                
                LogManager.Flush();

                _serviceHostStopped.Set();
            }
        }

        private void InitSignalHandlers()
        {
            // Handling SIGINT
            Console.CancelKeyPress += (sender, args) => {
                Stop();
                args.Cancel = true;
            };

            // Handling SIGTERM
            AssemblyLoadContext.Default.Unloading += delegate {
                Stop();
                _serviceHostStopped.WaitOne();
            };
        }

        private async void ServeConnection(ServerConnection connection, ServiceHostConfig config, Session session)
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

        private static void LogDispatcher(ServiceDispatcher dispatcher)
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

        private static async Task<X509Certificate?> LoadCertificate(ServiceHostConfig config)
        {
            if (config.TlsSettings == null || string.IsNullOrEmpty(config.TlsSettings.CertificateFile) || string.IsNullOrEmpty(config.TlsSettings.PasswordFile))
                return null;

            byte[] certificateData = await File.ReadAllBytesAsync(config.TlsSettings.CertificateFile);
            string password = await File.ReadAllTextAsync(config.TlsSettings.PasswordFile);

            var certificate = new X509Certificate(certificateData, password);

            _logger.Info("Loaded TLS certificate issued by '{0}' for '{1}' SN:'{2}' expires: {3}", 
                certificate.Issuer, 
                certificate.Subject, 
                certificate.GetSerialNumberString(),
                certificate.GetExpirationDateString()
            );

            return certificate;
        }

        private static ClusterConnection CreateClusterConnection(ServiceHostConfig config)
        {
            return new ClusterConnection(config.KnownHosts, new ClusterConnectionSettings {
                ReconnectionDelay = TimeSpan.FromSeconds(config.ClusterConnectionSettings.ReconnectionDelay),
                MaxRequestHoldTime = TimeSpan.FromSeconds(config.ClusterConnectionSettings.MaxRequestHoldTime),
                TlsSettings = {
                    AllowSelfSignedCertificates = config.TlsSettings?.AllowSelfSignedCertificates ?? false
                }
            });
        }

        private static async Task ConnectCluster(ServiceHostConfig config, ClusterConnection clusterConnection)
        {
            _logger.Info("Connecting cluster after {0} sec.", config.ClusterConnectionSettings.ConnectionDelay);

            try
            {
                await clusterConnection.Connect(TimeSpan.FromSeconds(config.ClusterConnectionSettings.ConnectionDelay));

                _logger.Info("Cluster connected.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to connect cluster. Interservice communication is not available.");
            }
        }

        private async Task UpdateClusterConnection(ServiceHostConfig config, ClusterConnection clusterConnection)
        {
            try
            {
                while (_updateClusterConnection)
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

        private IContainer BuildContainer(ServiceHostConfig config, ClusterConnection clusterConnection, ISession session)
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

        public void Stop()
        {
            Socket? socket = Interlocked.CompareExchange(ref _listeningSocket, null, _listeningSocket);

            if (socket == null)
                return;

            socket.Close();
        }

        private void ConfigureLogger(ServiceHostConfig config)
        {
            LogManager.Configuration = new XmlLoggingConfiguration(config.NLogConfigFile);
            LogManager.Configuration.Variables["instanceId"] = config.InstanceId.ToString();
        }

        private void LoadMessageAssemblies(string[] assemblies)
        {
            foreach (string assemblyPath in assemblies)
                Assembly.LoadFrom(assemblyPath);
        }

        private void LoadServiceAssemblies(string[] assemblies)
        {
            foreach (string assemblyPath in assemblies)
                AddServiceAssembly(Assembly.LoadFrom(assemblyPath));
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
