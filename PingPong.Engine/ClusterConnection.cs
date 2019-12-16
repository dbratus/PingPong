using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace PingPong.Engine
{
    public sealed class ClusterConnection : IDisposable
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly string[] _uris;
        public string[] Uris =>
            _uris;

        private readonly ClusterConnectionSettings _settings;
        public ClusterConnectionSettings Settings =>
            _settings;

        private readonly RequestNoGenerator _requestNoGenerator =
            new RequestNoGenerator();
        private readonly ClientConnection?[] _connections;
        private readonly Dictionary<Type, ConnectionSelector> _routingMap = 
            new Dictionary<Type, ConnectionSelector>();

        private volatile ClusterConnectionStatus _status = 
            ClusterConnectionStatus.NotConnected;
        public ClusterConnectionStatus Status =>
            _status;

        public bool HasPendingRequests =>
            _connections.Any(conn => conn?.HasPendingRequests ?? false);

        public ClusterConnection(string[] uris) :
            this(uris, new ClusterConnectionSettings())
        {
        }

        public ClusterConnection(string[] uris, ClusterConnectionSettings settings)
        {
            _uris = uris;
            _settings = settings;
            _connections = new ClientConnection[uris.Length];
        }

        public void Dispose()
        {
            foreach (ClientConnection? connection in _connections)
                connection?.Dispose();
        }

        public async Task Connect()
        {
            if (_status != ClusterConnectionStatus.NotConnected)
                throw new InvalidOperationException("Invalid connection status.");

            _status = ClusterConnectionStatus.Connecting;

            var clientConnectionTasks = _uris.Select(CreateClientConnection).ToArray();
            var routingMap = new Dictionary<Type, List<int>>();

            for (int connIdx = 0; connIdx < clientConnectionTasks.Length; ++connIdx)
            {
                ClientConnection? conn = await clientConnectionTasks[connIdx];
                if (conn == null)
                    continue;
                
                foreach (Type requestType in conn.SupportedRequestTypes)
                {
                    if (!routingMap.TryGetValue(requestType, out List<int> connections))
                    {
                        connections = new List<int>();
                        routingMap.Add(requestType, connections);
                    }

                    connections.Add(connIdx);
                }

                _connections[connIdx] = conn;
            }

            if (routingMap.Count == 0)
            {
                _status = ClusterConnectionStatus.NotConnected;
                throw new CommunicationException("Failed to establish any connection");
            }

            foreach (KeyValuePair<Type, List<int>> mapEntry in routingMap)
                _routingMap.Add(mapEntry.Key, new ConnectionSelector(mapEntry.Value.ToArray()));

            _status = ClusterConnectionStatus.Active;
        }

        // This should be a local function of Connect, but due to a nasty bug in vscode it breaks
        // the systex highlighting if defined like that. To be more precise, the cause is the '?' character
        // in the return type.
        private async Task<ClientConnection?> CreateClientConnection(string uri)
        {
            int retriesLeft = _settings.MaxConnectionRetries;

            while (true)
            {
                try
                {
                    var connection = new ClientConnection(_requestNoGenerator, uri);
                    await connection.Connect(TimeSpan.Zero);
                    return connection;
                }
                catch (Exception ex)
                {
                    if (retriesLeft == 0)
                    {
                        _logger.Error(ex, "Connection to '{0}' failed after {1} retries. URI ignored.", uri, _settings.MaxConnectionRetries);
                        throw;
                    }

                    --retriesLeft;
                }

                await Task.Delay(_settings.ConnectionRetryDelay);
            }
        }

        public ClusterConnection Send<TRequest>()
            where TRequest: class 
        {
            if (_status != ClusterConnectionStatus.Active)
                throw new InvalidOperationException("Invalid connection status.");

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
                throw new ProtocolException($"Request type '{typeof(TRequest).FullName}' is not supported.");

            selector.SelectConnection(_connections)?.Send<TRequest>();

            return this;
        }

        public ClusterConnection Send<TRequest>(TRequest request)
            where TRequest: class 
        {
            if (_status != ClusterConnectionStatus.Active)
                throw new InvalidOperationException("Invalid connection status.");

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
                throw new ProtocolException($"Request type '{typeof(TRequest).FullName}' is not supported.");

            selector.SelectConnection(_connections)?.Send(request);

            return this;
        }

        public ClusterConnection Send<TRequest, TResponse>(TRequest request, Action<TResponse, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            if (_status != ClusterConnectionStatus.Active)
                throw new InvalidOperationException("Invalid connection status.");

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
                throw new ProtocolException($"Request type '{typeof(TRequest).FullName}' is not supported.");

            selector.SelectConnection(_connections)?.Send(request, callback);

            return this;
        }

        public Task<(TResponse, RequestResult)> SendAsync<TRequest, TResponse>(TRequest request)
            where TRequest: class 
            where TResponse: class
        {
            var completionSource = new TaskCompletionSource<(TResponse, RequestResult)>();

            Send<TRequest, TResponse>(request, (response, result) => completionSource.SetResult((response, result)));

            return completionSource.Task;
        }

        public ClusterConnection Send<TRequest, TResponse>(Action<TResponse, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            if (_status != ClusterConnectionStatus.Active)
                throw new InvalidOperationException("Invalid connection status.");

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
                throw new ProtocolException($"Request type '{typeof(TRequest).FullName}' is not supported.");

            selector.SelectConnection(_connections)?.Send(callback);

            return this;
        }

        public Task<(TResponse, RequestResult)> SendAsync<TRequest, TResponse>()
            where TRequest: class 
            where TResponse: class
        {
            var completionSource = new TaskCompletionSource<(TResponse, RequestResult)>();
            
            Send<TRequest, TResponse>((response, result) => completionSource.SetResult((response, result)));
            
            return completionSource.Task;
        }

        public void InvokeCallbacks()
        {
            for (int i = 0; i < _connections.Length; ++i)
            {
                ClientConnection? connection = Volatile.Read(ref _connections[i]);
                if (connection == null)
                    continue;

                bool isConnectionBroken = connection.Status == ClientConnectionStatus.Broken;

                if (isConnectionBroken)
                {
                    var newConnection = new ClientConnection(_requestNoGenerator, connection.Uri);
                    
                    newConnection
                        .Connect(_settings.ReconnectionDelay)
                        .ContinueWith(task => ReconnectionComplete(task, newConnection));

                    Volatile.Write(ref _connections[i], newConnection);
                }

                connection.InvokeCallbacks();

                if (isConnectionBroken)
                {
                    if (connection.HasPendingRequests)
                    {
                        foreach (ClientConnection.RequestQueueEntry request in connection.ConsumePendingRequests())
                        {
                            try
                            {
                                _routingMap[request.Type]
                                    .SelectConnection(_connections)
                                    ?.Send(request);
                            }
                            catch (CommunicationException)
                            {
                                request.Callback?.Invoke(null, RequestResult.NotDelivered);
                            }
                        }
                    }

                    connection.Dispose();
                }
            }

            void ReconnectionComplete(Task connectionTask, ClientConnection connection)
            {
                if (connectionTask.IsFaulted)
                    _logger.Error(connectionTask.Exception, "Reconnection to '{0}' failed.", connection.Uri);
                
                _logger.Info("Successfully reconnected to '{0}'.", connection.Uri);
            }
        }

        private sealed class ConnectionSelector
        {
            private readonly int[] _connectionsIdx;

            private readonly Random _random = new Random();

            public ConnectionSelector(int[] connectionIdx)
            {
                _connectionsIdx = connectionIdx;
            }

            public ClientConnection? SelectConnection(ClientConnection?[] connections)
            {
                int connectionsCount = _connectionsIdx.Length;

                Span<int> active = stackalloc int[connectionsCount];
                Span<int> connecting = stackalloc int[connectionsCount];
                Span<int> broken = stackalloc int[connectionsCount];

                int activeCount = 0;
                int connectingCount = 0;
                int brokenCount = 0;

                foreach(int idx in _connectionsIdx)
                {
                    switch (connections[idx]?.Status ?? ClientConnectionStatus.Disposed)
                    {
                        case ClientConnectionStatus.Active:
                        active[activeCount++] = idx;
                        break;
                        case ClientConnectionStatus.Connecting:
                        connecting[connectingCount++] = idx;
                        break;
                        case ClientConnectionStatus.Broken:
                        broken[brokenCount++] = idx;
                        break;
                    }
                }

                if (activeCount == 1)
                    return Volatile.Read(ref connections[active[0]]);

                if (activeCount > 1)
                    return Volatile.Read(ref connections[active[_random.Next(activeCount)]]);

                if (connectingCount == 1)
                    return Volatile.Read(ref connections[connecting[0]]);

                if (connectingCount > 1)
                    return Volatile.Read(ref connections[connecting[_random.Next(connectingCount)]]);

                if (brokenCount == 1)
                    return Volatile.Read(ref connections[broken[0]]);

                if (brokenCount > 1)
                    return Volatile.Read(ref connections[broken[_random.Next(brokenCount)]]);

                throw new CommunicationException("No connection available");
            }
        }
    }
}