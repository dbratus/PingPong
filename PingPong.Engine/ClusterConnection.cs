using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    public sealed class ClusterConnection : IDisposable
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly ClusterConnectionSettings _settings;
        public ClusterConnectionSettings Settings =>
            _settings;

        private readonly RequestNoGenerator _requestNoGenerator =
            new RequestNoGenerator();

        private readonly string[] _uris;

        private readonly ClientConnection?[] _connections;
        private readonly ConcurrentDictionary<Type, ConnectionSelector> _routingMap = 
            new ConcurrentDictionary<Type, ConnectionSelector>();

        private volatile ClusterConnectionStatus _status = 
            ClusterConnectionStatus.NotConnected;
        public ClusterConnectionStatus Status =>
            _status;

        public bool HasPendingRequests =>
            _connections.Any(conn => conn?.HasPendingRequests ?? false);

        private readonly ConcurrentQueue<ClientConnection.RequestQueueEntry> _requestsOnHold =
            new ConcurrentQueue<ClientConnection.RequestQueueEntry>();

        public ClusterConnection(string[] uris) :
            this(uris, new ClusterConnectionSettings())
        {
        }

        public ClusterConnection(string[] uris, ClusterConnectionSettings settings)
        {
            _settings = settings;
            _uris = uris;
            _connections = new ClientConnection?[uris.Length];
        }

        public void Dispose()
        {
            foreach (ClientConnection? conn in _connections)
                conn?.Dispose();
        
            _status = ClusterConnectionStatus.Disposed;
        }

        public Task Connect() =>
            Connect(TimeSpan.Zero);

        public async Task Connect(TimeSpan delay)
        {
            ClusterConnectionStatus status = _status;
            if (status != ClusterConnectionStatus.NotConnected)
                throw new InvalidOperationException($"Invalid connection status {status}.");

            _status = ClusterConnectionStatus.Connecting;

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            for (int i = 0; i < _connections.Length; ++i)
            {
                var conn = new ClientConnection(_uris[i]);
                
                try
                {
                    await conn.Connect(TimeSpan.Zero);

                    Volatile.Write(ref _connections[i], conn);
                    RegisterConnection(i);
                }
                catch (Exception)
                {
                    // The connection is broken, but it will be reconnected later.
                    Volatile.Write(ref _connections[i], conn);
                    continue;
                }
            }

            _status = ClusterConnectionStatus.Active;
        }

        private void RegisterConnection(int connSlotIdx)
        {
            ClientConnection? conn = _connections[connSlotIdx];
            if (conn == null)
                return;

            foreach (Type requestType in conn.SupportedRequestTypes)
            {
                _routingMap.AddOrUpdate(
                    requestType, 
                    _ => new ConnectionSelector().AddConnectionIndex(connSlotIdx),
                    (_, selector) => selector.AddConnectionIndex(connSlotIdx)
                );
            }
        }

        public ClusterConnection Send<TRequest>()
            where TRequest: class 
        {
            ClusterConnectionStatus status = _status;
            if (!(status == ClusterConnectionStatus.Active || status == ClusterConnectionStatus.Connecting))
                throw new InvalidOperationException($"Invalid connection status {status}.");

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(typeof(TRequest), null, null));
                return this;
            }

            selector.SelectConnection(_connections)?.Send<TRequest>();

            return this;
        }

        public ClusterConnection Send<TRequest>(TRequest request)
            where TRequest: class 
        {
            ClusterConnectionStatus status = _status;
            if (!(status == ClusterConnectionStatus.Active || status == ClusterConnectionStatus.Connecting))
                throw new InvalidOperationException($"Invalid connection status {status}.");

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(typeof(TRequest), request, null));
                return this;
            }

            selector.SelectConnection(_connections)?.Send(request);

            return this;
        }

        public ClusterConnection Send<TRequest, TResponse>(TRequest request, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ClusterConnectionStatus status = _status;
            if (!(status == ClusterConnectionStatus.Active || status == ClusterConnectionStatus.Connecting))
                throw new InvalidOperationException($"Invalid connection status {status}.");

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    typeof(TRequest), 
                    request, 
                    InvlokeCallback
                ));
                return this;
            }

            selector.SelectConnection(_connections)?.Send(request, callback);

            return this;

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>(TRequest request)
            where TRequest: class 
            where TResponse: class
        {
            var completionSource = new TaskCompletionSource<(TResponse?, RequestResult)>();

            Send<TRequest, TResponse>(request, (response, result) => completionSource.SetResult((response, result)));

            return completionSource.Task;
        }

        public ClusterConnection Send<TRequest, TResponse>(Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ClusterConnectionStatus status = _status;
            if (!(status == ClusterConnectionStatus.Active || status == ClusterConnectionStatus.Connecting))
                throw new InvalidOperationException($"Invalid connection status {status}.");

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    typeof(TRequest), 
                    null,
                    InvlokeCallback
                ));
                return this;
            }

            selector.SelectConnection(_connections)?.Send(callback);

            return this;

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>()
            where TRequest: class 
            where TResponse: class
        {
            var completionSource = new TaskCompletionSource<(TResponse?, RequestResult)>();
            
            Send<TRequest, TResponse>((response, result) => completionSource.SetResult((response, result)));
            
            return completionSource.Task;
        }

        public void Update()
        {
            if (_status != ClusterConnectionStatus.Active)
                return;

            // Updating connections replacing the broken ones in the process.
            for (int i = 0; i < _connections.Length; ++i)
            {
                ClientConnection? connection = _connections[i];
                if (connection == null)
                    continue;

                bool isConnectionBroken = connection.Status == ClientConnectionStatus.Broken;

                if (isConnectionBroken)
                {
                    var newConnection = new ClientConnection(_requestNoGenerator, connection.Uri);
                    Task connectionTask = newConnection.Connect(_settings.ReconnectionDelay);
                    
                    Volatile.Write(ref _connections[i], newConnection);

                    int connIdx = i;
                    connectionTask.ContinueWith(task => ReconnectionComplete(task, newConnection, connIdx));
                }
                
                // Connection needs to be updated even if its broken. By updating broken connection,
                // we complete requests whose responses has been delivered just before disconnect.
                connection.Update();

                if (isConnectionBroken)
                {
                    if (connection.HasPendingRequests)
                    {
                        foreach (ClientConnection.RequestQueueEntry request in connection.ConsumePendingRequests())
                        {
                            // At this point we can be sure that the routing map contains
                            // entry for the request type. The broken connection got these
                            // requests, hence they were routed through the map before.
                            // In worst case the requests will be routed to the new connection
                            // replacing the broken one.
                            _routingMap[request.Type]
                                .SelectConnection(_connections)
                                ?.Send(request);
                        }
                    }

                    connection.Dispose();
                }
            }

            // Handlig requests on hold.
            // Trying to deliver requests or complete them if expired. Requests which
            // are still nowhere to deliver are enqueued back to be processed later.
            var now = DateTime.Now;
            int nRequestsNoPrecess = _requestsOnHold.Count;

            while (nRequestsNoPrecess-- > 0)
            {
                _requestsOnHold.TryDequeue(out ClientConnection.RequestQueueEntry request);

                if (now - request.CreationTime > _settings.MaxRequestHoldTime)
                {
                    request.Callback?.Invoke(null, RequestResult.DeliveryTimeout);
                    continue;
                }

                bool delivered = false;
                if (_routingMap.TryGetValue(request.Type, out ConnectionSelector selector))
                {
                    ClientConnection? conn = selector.SelectConnection(_connections);
                    
                    if (conn != null)
                    {
                        conn.Send(request);
                        delivered = true;
                    }
                }

                if (!delivered)
                    _requestsOnHold.Enqueue(request);
            }

            void ReconnectionComplete(Task connectionTask, ClientConnection connection, int connSlotIdx)
            {
                if (connectionTask.IsFaulted)
                {
                    _logger.Error(connectionTask.Exception, "Reconnection to '{0}' failed.", connection.Uri);
                    return;
                }
                
                _logger.Info("Successfully reconnected to '{0}'.", connection.Uri);

                // After every reconnection the connection should be registered,
                // because it may start to support new request types.
                RegisterConnection(connSlotIdx);
            }
        }

        private sealed class ConnectionSelector
        {
            private readonly List<int> _connectionsIdx = new List<int>();

            private readonly Random _random = new Random();

            public ConnectionSelector AddConnectionIndex(int i)
            {
                if (!_connectionsIdx.Contains(i))
                    _connectionsIdx.Add(i);

                return this;
            }

            public ClientConnection? SelectConnection(ClientConnection?[] connections)
            {
                int connectionsCount = _connectionsIdx.Count;

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