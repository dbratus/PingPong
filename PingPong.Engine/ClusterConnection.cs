using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;
using PingPong.Engine.Messages;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    public sealed class ClusterConnection : IDisposable, ICluster
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

        public IEnumerable<(Type RequestType, Type? ResponseType)> RequestResponseMap =>
            _connections
                .Where(conn => conn != null)
                .SelectMany(conn => conn?.RequestResponseMap)
                .Distinct();

        private volatile ClusterConnectionStatus _status = 
            ClusterConnectionStatus.NotConnected;
        public ClusterConnectionStatus Status =>
            _status;

        public bool HasPendingRequests =>
            _connections.Any(conn => conn?.HasPendingRequests ?? false);

        private readonly ConcurrentQueue<ClientConnection.RequestQueueEntry> _requestsOnHold =
            new ConcurrentQueue<ClientConnection.RequestQueueEntry>();

        private class EventCallbackJob
        {
            public readonly Action<RequestResult> Callback;
            public readonly Channel<RequestResult> ResultsChannel;
            public int ResultsToWait;

            public EventCallbackJob(Action<RequestResult> callback, Channel<RequestResult> resultsChannel, int resultsToWait)
            {
                Callback = callback;
                ResultsChannel = resultsChannel;
                ResultsToWait = resultsToWait;
            }
        }
        private readonly ConcurrentQueue<EventCallbackJob> _eventCallbackJobs =
            new ConcurrentQueue<EventCallbackJob>();

        private readonly Dictionary<(long, long), Type> _messageHashMap;

        public ClusterConnection(string[] uris) :
            this(uris, new ClusterConnectionSettings())
        {
        }

        public ClusterConnection(string[] uris, ClusterConnectionSettings settings)
        {
            _settings = settings;
            _uris = uris;
            _connections = new ClientConnection?[uris.Length];
            _messageHashMap = MessageName.FindMessageTypes();
        }

        public void Dispose()
        {
            if (_status == ClusterConnectionStatus.Disposed)
                throw new InvalidOperationException("Connection already disposed.");

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
                var conn = new ClientConnection(_requestNoGenerator, _settings.TlsSettings, _settings.Serializer, _messageHashMap, _uris[i]);
                
                try
                {
                    await conn.Connect(TimeSpan.Zero);

                    Volatile.Write(ref _connections[i], conn);
                    RegisterConnection(i, conn.InstanceId);
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

        private void RegisterConnection(int connIdx, int instanceId)
        {
            ClientConnection? conn = _connections[connIdx];
            if (conn == null)
                return;

            foreach (Type requestType in conn.SupportedRequestTypes)
            {
                _routingMap.AddOrUpdate(
                    requestType, 
                    _ => 
                        new ConnectionSelector()
                            .AddConnectionIndex(connIdx)
                            .AddInstance(connIdx, instanceId),
                    (_, selector) => 
                    {
                        ClientConnection? existingInstance = selector.GetConnectionByInstanceId(_connections, instanceId);
                        if (existingInstance != null)
                        {
                            _logger.Error("Hosts at '{0}' and '{1}' have the same instance id. '{0}' is ignored.", conn.Uri, existingInstance.Uri);
                            return selector;
                        }

                        return selector
                            .AddConnectionIndex(connIdx)
                            .AddInstance(connIdx, instanceId);
                    }
                );
            }
        }

        public void Send<TRequest>()
            where TRequest: class 
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(-1, RequestFlags.None, typeof(TRequest), null, null));
                return;
            }

            selector.SelectConnection(_connections)?.Send<TRequest>(false);
        }

        public void Send<TRequest>(int instanceId)
            where TRequest: class 
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(instanceId, RequestFlags.None, typeof(TRequest), null, null));
                return;
            }

            ClientConnection? conn = selector.GetConnectionByInstanceId(_connections, instanceId);
            if (conn == null)
                throw new ProtocolException($"No connection for the instance {instanceId} and request type {typeof(TRequest).FullName}.");

            conn.Send<TRequest>(true);
        }

        public void Send<TRequest>(TRequest request)
            where TRequest: class 
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(-1, RequestFlags.None, typeof(TRequest), request, null));
                return;
            }

            selector.SelectConnection(_connections)?.Send(false, request);
        }

        internal void Send(object? request, Type requestType)
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(requestType, out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(-1, RequestFlags.None, requestType, request, null));
                return;
            }

            selector.SelectConnection(_connections)?.Send(
                new ClientConnection.RequestQueueEntry(-1, RequestFlags.None, requestType, request, null)
            );
        }

        public void Send<TRequest>(int instanceId, TRequest request)
            where TRequest: class 
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(instanceId, RequestFlags.None, typeof(TRequest), request, null));
                return;
            }

            ClientConnection? conn = selector.GetConnectionByInstanceId(_connections, instanceId);
            if (conn == null)
                throw new ProtocolException($"No connection for the instance {instanceId} and request type {typeof(TRequest).FullName}.");

            conn.Send(true, request);
        }

        public void Send<TRequest, TResponse>(TRequest request, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    -1,
                    RequestFlags.None, 
                    typeof(TRequest), 
                    request, 
                    InvlokeCallback
                ));
                return;
            }

            selector.SelectConnection(_connections)?.Send(false, request, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public void OpenChannel<TRequest, TResponse>(TRequest request, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    -1,
                    RequestFlags.OpenChannel, 
                    typeof(TRequest), 
                    request, 
                    InvlokeCallback
                ));
                return;
            }

            selector.SelectConnection(_connections)?.OpenChannel(false, request, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        internal void Send(object? request, Type requestType, RequestFlags flags, Action<object?, RequestResult> callback)
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(requestType, out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    -1,
                    flags, 
                    requestType, 
                    request, 
                    callback
                ));
                return;
            }

            selector.SelectConnection(_connections)?.Send(new ClientConnection.RequestQueueEntry(
                -1,
                flags, 
                requestType,
                request, 
                callback
            ));
        }

        public void Send<TRequest>(TRequest request, Action<RequestResult> callback)
            where TRequest: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    -1,
                    RequestFlags.None, 
                    typeof(TRequest), 
                    request, 
                    InvlokeCallback
                ));
                return;
            }

            selector.SelectConnection(_connections)?.Send(false, request, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback(result);
            };
        }

        public void Send<TRequest, TResponse>(int instanceId, TRequest request, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    instanceId,
                    RequestFlags.None, 
                    typeof(TRequest), 
                    request, 
                    InvlokeCallback
                ));
                return;
            }

            ClientConnection? conn = selector.GetConnectionByInstanceId(_connections, instanceId);
            if (conn == null)
                throw new ProtocolException($"No connection for the instance {instanceId} and request type {typeof(TRequest).FullName}.");

            conn.Send(true, request, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public void OpenChannel<TRequest, TResponse>(int instanceId, TRequest request, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    instanceId,
                    RequestFlags.OpenChannel, 
                    typeof(TRequest), 
                    request, 
                    InvlokeCallback
                ));
                return;
            }

            ClientConnection? conn = selector.GetConnectionByInstanceId(_connections, instanceId);
            if (conn == null)
                throw new ProtocolException($"No connection for the instance {instanceId} and request type {typeof(TRequest).FullName}.");

            conn.OpenChannel(true, request, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public void Send<TRequest>(int instanceId, TRequest request, Action<RequestResult> callback)
            where TRequest: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    instanceId,
                    RequestFlags.None, 
                    typeof(TRequest), 
                    request, 
                    InvlokeCallback
                ));
                return;
            }

            ClientConnection? conn = selector.GetConnectionByInstanceId(_connections, instanceId);
            if (conn == null)
                throw new ProtocolException($"No connection for the instance {instanceId} and request type {typeof(TRequest).FullName}.");

            conn.Send(true, request, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback(result);
            };
        }

        public Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>(TRequest request)
            where TRequest: class 
            where TResponse: class
        {
            var completionSource = new TaskCompletionSource<(TResponse?, RequestResult)>();

            Send<TRequest, TResponse>(
                request, 
                (response, result) => completionSource.SetResult((response, result))
            );

            return completionSource.Task;
        }

        public ChannelReader<(TResponse?, RequestResult)> OpenChannelAsync<TRequest, TResponse>(TRequest request)
            where TRequest: class 
            where TResponse: class
        {
            var channel = Channel.CreateUnbounded<(TResponse?, RequestResult)>();

            OpenChannel<TRequest, TResponse>(
                request, 
                (response, result) => 
                { 
                    if (response == null)
                    {
                        if (result != RequestResult.OK)
                            channel.Writer.TryWrite((null, result));
                        
                        channel.Writer.Complete();
                    }
                    else
                    {
                        channel.Writer.TryWrite((response, result));
                    }
                }
            );

            return channel.Reader;
        }

        internal Task<(object?, RequestResult)> SendAsync(object? request, Type requestType)
        {
            var completionSource = new TaskCompletionSource<(object?, RequestResult)>();

            Send(
                request, 
                requestType,
                RequestFlags.None,
                (response, result) => completionSource.SetResult((response, result))
            );

            return completionSource.Task;
        }

        internal ChannelReader<(object?, RequestResult)> OpenChannelAsync(object? request, Type requestType)
        {
            var channel = Channel.CreateUnbounded<(object?, RequestResult)>();

            Send(
                request, 
                requestType,
                RequestFlags.OpenChannel,
                (response, result) => 
                { 
                    if (response == null)
                    {
                        if (result != RequestResult.OK)
                            channel.Writer.TryWrite((null, result));
                        
                        channel.Writer.Complete();
                    }
                    else
                    {
                        channel.Writer.TryWrite((response, result));
                    }
                }
            );

            return channel.Reader;
        }

        public Task<RequestResult> SendAsync<TRequest>(TRequest request)
            where TRequest: class
        {
            var completionSource = new TaskCompletionSource<RequestResult>();

            Send<TRequest>(
                request, 
                result => completionSource.SetResult(result)
            );

            return completionSource.Task;
        }

        public Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>(int instanceId, TRequest request)
            where TRequest: class 
            where TResponse: class
        {
            var completionSource = new TaskCompletionSource<(TResponse?, RequestResult)>();

            Send<TRequest, TResponse>(
                instanceId, 
                request, 
                (response, result) => completionSource.SetResult((response, result))
            );

            return completionSource.Task;
        }

        public ChannelReader<(TResponse?, RequestResult)> OpenChannelAsync<TRequest, TResponse>(int instanceId, TRequest request)
            where TRequest: class 
            where TResponse: class
        {
            var channel = Channel.CreateUnbounded<(TResponse?, RequestResult)>();

            OpenChannel<TRequest, TResponse>(
                instanceId,
                request, 
                (response, result) => 
                { 
                    if (response == null)
                    {
                        if (result != RequestResult.OK)
                            channel.Writer.TryWrite((null, result));
                        
                        channel.Writer.Complete();
                    }
                    else
                    {
                        channel.Writer.TryWrite((response, result));
                    }
                }
            );

            return channel.Reader;
        }

        public Task<RequestResult> SendAsync<TRequest>(int instanceId, TRequest request)
            where TRequest: class
        {
            var completionSource = new TaskCompletionSource<RequestResult>();

            Send<TRequest>(
                instanceId, 
                request, 
                result => completionSource.SetResult(result)
            );

            return completionSource.Task;
        }

        public void Send<TRequest, TResponse>(Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    -1,
                    RequestFlags.None, 
                    typeof(TRequest), 
                    null,
                    InvlokeCallback
                ));
                return;
            }

            selector.SelectConnection(_connections)?.Send<TRequest, TResponse>(false, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public void OpenChannel<TRequest, TResponse>(Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    -1,
                    RequestFlags.OpenChannel, 
                    typeof(TRequest), 
                    null,
                    InvlokeCallback
                ));
                return;
            }

            selector.SelectConnection(_connections)?.OpenChannel<TRequest, TResponse>(false, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public void Send<TRequest>(Action<RequestResult> callback)
            where TRequest: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    -1,
                    RequestFlags.None, 
                    typeof(TRequest), 
                    null,
                    InvlokeCallback
                ));
                return;
            }

            selector.SelectConnection(_connections)?.Send<TRequest>(false, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback(result);
            };
        }

        public void Send<TRequest, TResponse>(int instanceId, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    instanceId,
                    RequestFlags.None, 
                    typeof(TRequest), 
                    null,
                    InvlokeCallback
                ));
                return;
            }

            ClientConnection? conn = selector.GetConnectionByInstanceId(_connections, instanceId);
            if (conn == null)
                throw new ProtocolException($"No connection for the instance {instanceId} and request type {typeof(TRequest).FullName}.");

            conn.Send<TRequest, TResponse>(true, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public void OpenChannel<TRequest, TResponse>(int instanceId, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    instanceId,
                    RequestFlags.OpenChannel, 
                    typeof(TRequest), 
                    null,
                    InvlokeCallback
                ));
                return;
            }

            ClientConnection? conn = selector.GetConnectionByInstanceId(_connections, instanceId);
            if (conn == null)
                throw new ProtocolException($"No connection for the instance {instanceId} and request type {typeof(TRequest).FullName}.");

            conn.OpenChannel<TRequest, TResponse>(true, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public void Send<TRequest>(int instanceId, Action<RequestResult> callback)
            where TRequest: class
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TRequest), out ConnectionSelector selector))
            {
                _requestsOnHold.Enqueue(new ClientConnection.RequestQueueEntry(
                    instanceId,
                    RequestFlags.None, 
                    typeof(TRequest), 
                    null,
                    InvlokeCallback
                ));
                return;
            }

            ClientConnection? conn = selector.GetConnectionByInstanceId(_connections, instanceId);
            if (conn == null)
                throw new ProtocolException($"No connection for the instance {instanceId} and request type {typeof(TRequest).FullName}.");

            conn.Send<TRequest>(true, callback);

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback(result);
            };
        }

        public Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>()
            where TRequest: class
            where TResponse: class
        {
            var completionSource = new TaskCompletionSource<(TResponse?, RequestResult)>();
            
            Send<TRequest, TResponse>(
                (response, result) => completionSource.SetResult((response, result))
            );

            return completionSource.Task;
        }

        public ChannelReader<(TResponse?, RequestResult)> OpenChannelAsync<TRequest, TResponse>()
            where TRequest: class
            where TResponse: class
        {
            var channel = Channel.CreateUnbounded<(TResponse?, RequestResult)>();

            OpenChannel<TRequest, TResponse>(
                (response, result) => 
                { 
                    if (response == null)
                    {
                        if (result != RequestResult.OK)
                            channel.Writer.TryWrite((null, result));
                        
                        channel.Writer.Complete();
                    }
                    else
                    {
                        channel.Writer.TryWrite((response, result));
                    }
                }
            );

            return channel.Reader;
        }

        public Task<RequestResult> SendAsync<TRequest>()
            where TRequest: class
        {
            var completionSource = new TaskCompletionSource<RequestResult>();
            
            Send<TRequest>(
                result => completionSource.SetResult(result)
            );

            return completionSource.Task;
        }

        public Task<(TResponse?, RequestResult)> SendAsync<TRequest, TResponse>(int instanceId)
            where TRequest: class 
            where TResponse: class
        {
            var completionSource = new TaskCompletionSource<(TResponse?, RequestResult)>();
            
            Send<TRequest, TResponse>(
                instanceId,
                (response, result) => completionSource.SetResult((response, result))
            );
            
            return completionSource.Task;
        }

        public ChannelReader<(TResponse?, RequestResult)> OpenChannelAsync<TRequest, TResponse>(int instanceId)
            where TRequest: class 
            where TResponse: class
        {
            var channel = Channel.CreateUnbounded<(TResponse?, RequestResult)>();

            OpenChannel<TRequest, TResponse>(
                instanceId,
                (response, result) => 
                { 
                    if (response == null)
                    {
                        if (result != RequestResult.OK)
                            channel.Writer.TryWrite((null, result));
                        
                        channel.Writer.Complete();
                    }
                    else
                    {
                        channel.Writer.TryWrite((response, result));
                    }
                }
            );

            return channel.Reader;
        }

        public Task<RequestResult> SendAsync<TRequest>(int instanceId)
            where TRequest: class
        {
            var completionSource = new TaskCompletionSource<RequestResult>();
            
            Send<TRequest>(
                instanceId,
                result => completionSource.SetResult(result)
            );
            
            return completionSource.Task;
        }

        public void Publish<TEvent>(TEvent ev)
            where TEvent: class 
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TEvent), out ConnectionSelector selector))
                return;

            foreach (ClientConnection? conn in selector.AllConnections(_connections))
                conn?.Send(false, ev);
        }

        public void Publish<TEvent>(TEvent ev, Action<RequestResult> callback)
            where TEvent: class 
        {
            ValidateSend();

            if (!_routingMap.TryGetValue(typeof(TEvent), out ConnectionSelector selector))
                return;

            var resultsChannel = Channel.CreateUnbounded<RequestResult>(new UnboundedChannelOptions {
                SingleReader = true
            });

            int totalConfirmationsToWait = 0;
            foreach (ClientConnection? conn in selector.AllConnections(_connections))
            {
                conn?.Send(false, ev, result => resultsChannel.Writer.TryWrite(result));
                ++totalConfirmationsToWait;
            }

            if (totalConfirmationsToWait == 0)
                return;

            _eventCallbackJobs.Enqueue(new EventCallbackJob(callback, resultsChannel, totalConfirmationsToWait));
        }

        public Task<RequestResult> PublishAsync<TEvent>(TEvent ev)
            where TEvent: class
        {
            var completionSource = new TaskCompletionSource<RequestResult>();
            
            Publish<TEvent>(
                ev,
                result => completionSource.SetResult(result)
            );

            return completionSource.Task;
        }

        private void ValidateSend()
        {
            ClusterConnectionStatus status = _status;
            if (!(status == ClusterConnectionStatus.Active || status == ClusterConnectionStatus.Connecting))
                throw new InvalidOperationException($"Invalid connection status {status}.");
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
                    var newConnection = new ClientConnection(_requestNoGenerator, _settings.TlsSettings, _settings.Serializer, _messageHashMap, connection.Uri);
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

            // Completing event callback jobs.
            int completedCount = 0;
            int totalCount = _eventCallbackJobs.Count;

            foreach (EventCallbackJob job in _eventCallbackJobs)
            {
                if (job.ResultsToWait == 0)
                {
                    ++completedCount;
                    continue;
                }

                while (job.ResultsChannel.Reader.TryRead(out RequestResult nextResult))
                {
                    if (nextResult != RequestResult.OK)
                    {
                        job.ResultsToWait = 0;
                        ++completedCount;
                        job.Callback(RequestResult.PartialEventDelivery);
                        break;
                    }

                    --job.ResultsToWait;

                    if (job.ResultsToWait == 0)
                    {
                        job.Callback(RequestResult.OK);
                        ++completedCount;
                    }
                }
            }

            // Cleanup event callback jobs queue if at least half of the jobs are complete.
            if (totalCount - completedCount < totalCount / 2)
            {
                while (completedCount-- > 0)
                {
                    if (_eventCallbackJobs.TryDequeue(out EventCallbackJob job) && job.ResultsToWait > 0)
                        _eventCallbackJobs.Enqueue(job);
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
                    ClientConnection? conn;
                    if (request.InstanceId < 0)
                        conn = selector.SelectConnection(_connections);
                    else
                        conn = selector.GetConnectionByInstanceId(_connections, request.InstanceId);

                    if (conn != null)
                    {
                        conn.Send(request);
                        delivered = true;
                    }
                }

                if (!delivered)
                    _requestsOnHold.Enqueue(request);
            }

            // Updating connection selectors.
            foreach (ConnectionSelector selector in _routingMap.Values)
                selector.UpdateWeights(_connections);

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
                RegisterConnection(connSlotIdx, connection.InstanceId);
            }
        }

        private sealed class ConnectionSelector
        {
            private readonly List<int> _connectionsIdx = new List<int>();
            private readonly Dictionary<int, int> _instanceMap = new Dictionary<int, int>();
            private readonly Dictionary<int, double> _weights = new Dictionary<int, double>();

            private readonly Random _random = new Random();

            public ConnectionSelector AddConnectionIndex(int connIdx)
            {
                if (!_connectionsIdx.Contains(connIdx))
                    _connectionsIdx.Add(connIdx);

                return this;
            }

            public ConnectionSelector AddInstance(int connIdx, int instanceId)
            {
                _instanceMap.Add(instanceId, connIdx);
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
                    return Volatile.Read(ref connections[SelectRandomIndex(active)]);

                if (connectingCount == 1)
                    return Volatile.Read(ref connections[connecting[0]]);

                if (connectingCount > 1)
                    return Volatile.Read(ref connections[SelectRandomIndex(connecting)]);

                if (brokenCount == 1)
                    return Volatile.Read(ref connections[broken[0]]);

                if (brokenCount > 1)
                    return Volatile.Read(ref connections[SelectRandomIndex(broken)]);

                throw new CommunicationException("No connection available");
            }

            private int SelectRandomIndex(Span<int> connIdx)
            {
                Span<double> weights = stackalloc double[connIdx.Length];

                for (int i = 0; i < connIdx.Length; ++i)
                    weights[i] = _weights.TryGetValue(connIdx[i], out double val) ? val : 1.0;

                return connIdx[WeightedRandom.GetIndex(_random, weights)];
            }

            public ClientConnection? GetConnectionByInstanceId(ClientConnection?[] connections, int instanceId) =>
                _instanceMap.TryGetValue(instanceId, out int connIdx) ? connections[connIdx] : null;

            public IEnumerable<ClientConnection?> AllConnections(ClientConnection?[] connections)
            {
                foreach (int idx in _connectionsIdx)
                    yield return connections[idx];
            }

            public void UpdateWeights(ClientConnection?[] connections)
            {
                foreach (int connIdx in _connectionsIdx)
                {
                    ClientConnection? conn = connections[connIdx];
                    if (conn == null)
                        continue;

                    int totalLoad = 
                        conn.HostStatus.PendingProcessing + 
                        conn.HostStatus.InProcessing + 
                        conn.HostStatus.PendingResponsePropagation;
                    double weight = totalLoad > 0 ? 1.0 / totalLoad : 1.0;

                    _weights[connIdx] = weight;
                }
            }
        }
    }
}