using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;

namespace PingPong.Engine
{
    sealed class ClientConnection : IDisposable
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly string _uri;
        public string Uri =>
            _uri;

        private readonly Socket _socket;
        private readonly Lazy<DelimitedMessageReader> _messageReader;
        private readonly Lazy<DelimitedMessageWriter> _messageWriter;
        private readonly MessageMap _messageMap = new MessageMap();
        private readonly Dictionary<int, int> _reqestResponseMap 
            = new Dictionary<int, int>();
        private readonly RequestNoGenerator _requestNoGenerator;

        private Task? _requestPropagatorTask;

        public IEnumerable<Type> SupportedRequestTypes =>
            _reqestResponseMap.Keys.Select(_messageMap.GetMessageTypeById);

        internal struct RequestQueueEntry
        {
            public readonly Type Type;
            public readonly object? Body;
            public readonly Action<object?, RequestResult>? Callback;

            public RequestQueueEntry(Type type, object? body, Action<object?, RequestResult>? callback)
            {
                Type = type;
                Body = body;
                Callback = callback;
            }
        }
        private readonly Channel<RequestQueueEntry> _requestChan 
            = Channel.CreateUnbounded<RequestQueueEntry>(new UnboundedChannelOptions {
                SingleReader = true
            });

        private Task? _responseReceiverTask;

        private struct ResponseQueueEntry
        {
            public readonly int RequestNo;
            public readonly object? Body;
            public readonly RequestResult Result;

            public ResponseQueueEntry(int requestNo, object? body, RequestResult result)
            {
                RequestNo = requestNo;
                Body = body;
                Result = result;
            }
        }
        private readonly Channel<ResponseQueueEntry> _responseChan 
            = Channel.CreateUnbounded<ResponseQueueEntry>(new UnboundedChannelOptions {
                SingleWriter = true,
                SingleReader = true
            });

        private readonly ConcurrentDictionary<int, RequestQueueEntry> _requestsWaitingForResponse =
            new ConcurrentDictionary<int, RequestQueueEntry>();

        private int _pendingRequestsCount;
        public bool HasPendingRequests => 
            _pendingRequestsCount > 0;

        private volatile int _status = 
            (int)ClientConnectionStatus.NotConnected;
        public ClientConnectionStatus Status
        {
            get { return (ClientConnectionStatus)_status; }
            private set { _status = (int)value; }
        }

        public ClientConnection(string uri) :
            this(new RequestNoGenerator(), uri)
        {
        }

        internal ClientConnection(RequestNoGenerator requestNoGenerator, string uri)
        {
            _uri = uri;
            _socket = new Socket(SocketType.Stream, ProtocolType.IP);
            _socket.NoDelay = true;

            _messageReader = new Lazy<DelimitedMessageReader>(() => new DelimitedMessageReader(new NetworkStream(_socket, System.IO.FileAccess.Read, false)));
            _messageWriter = new Lazy<DelimitedMessageWriter>(() => new DelimitedMessageWriter(new NetworkStream(_socket, System.IO.FileAccess.Write, false)));
            _requestNoGenerator = requestNoGenerator;
        }

        public void Dispose()
        {
            if (Status == ClientConnectionStatus.Disposed)
                throw new InvalidOperationException("Connection already disposed.");

            if (_requestPropagatorTask != null)
            {
                _requestChan.Writer.Complete();
                _requestPropagatorTask.Wait();
            }

            if (_responseReceiverTask != null)
                _responseReceiverTask.Wait();

            if (_messageReader.IsValueCreated)
                _messageReader.Value.Dispose();
            
            if (_messageWriter.IsValueCreated)
                _messageWriter.Value.Dispose();

            _socket.Dispose();
            Status = ClientConnectionStatus.Disposed;
        }

        public async Task Connect(TimeSpan delay)
        {
            {
                int status = _status;

                if (
                    status != (int)ClientConnectionStatus.NotConnected &&
                    Interlocked.CompareExchange(ref _status, (int)ClientConnectionStatus.Connecting, status) != status
                )
                    throw new InvalidOperationException("Invalid connection status.");
            }

            _logger.Info("Connecting to '{0}'", _uri);

            try
            {
                var uriBuilder = new UriBuilder(_uri);
                
                await _socket.ConnectAsync(uriBuilder.Host, uriBuilder.Port);

                var preamble = await _messageReader.Value.Read<Preamble>();

                foreach (MessageIdMapEntry ent in preamble.MessageIdMap)
                {
                    Type messageType = Type.GetType(ent.MessageType);
                    if (messageType == null)
                        throw new ProtocolException($"Message type not found '{ent.MessageType}'");

                    _messageMap.Add(messageType, ent.MessageId);
                }

                foreach (RequestResponseMapEntry ent in preamble.RequestResponseMap)
                    _reqestResponseMap.Add(ent.RequestId, ent.ResponsetId);

                _requestPropagatorTask = PropagateRequests();
                _responseReceiverTask = ReceiveResponses();
            }
            catch (Exception ex)
            {
                int status = _status;
                if (status != (int)ClientConnectionStatus.Disposed)
                {
                    Interlocked.CompareExchange(ref _status, (int)ClientConnectionStatus.Broken, status);

                    if (Status != ClientConnectionStatus.Disposed)
                        _logger.Error(ex, "Failed to connect to '{0}'", _uri);
                }
            }
        }

        public ClientConnection Send<TRequest>() 
            where TRequest: class
        {
            if (!(Status == ClientConnectionStatus.Connecting || Status == ClientConnectionStatus.Active || Status == ClientConnectionStatus.Broken))
                throw new InvalidOperationException("Invalid connection status.");

            _requestChan.Writer.TryWrite(new RequestQueueEntry(typeof(TRequest), null, null));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;
        }

        public ClientConnection Send<TRequest>(TRequest request)
            where TRequest: class
        {
            if (!(Status == ClientConnectionStatus.Connecting || Status == ClientConnectionStatus.Active || Status == ClientConnectionStatus.Broken))
                throw new InvalidOperationException("Invalid connection status.");

            _requestChan.Writer.TryWrite(new RequestQueueEntry(typeof(TRequest), request, null));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;
        }

        public ClientConnection Send<TRequest, TResponse>(TRequest request, Action<TResponse, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            if (!(Status == ClientConnectionStatus.Connecting || Status == ClientConnectionStatus.Active || Status == ClientConnectionStatus.Broken))
                throw new InvalidOperationException("Invalid connection status.");

            _requestChan.Writer.TryWrite(new RequestQueueEntry(typeof(TRequest), request, InvlokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvlokeCallback(object? responseBody, RequestResult result) {
                if (responseBody == null)
                    throw new ProtocolException("Response is not expected to be null");

                callback((TResponse)responseBody, result);
            };
        }

        public ClientConnection Send<TRequest, TResponse>(Action<TResponse, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            if (!(Status == ClientConnectionStatus.Connecting || Status == ClientConnectionStatus.Active || Status == ClientConnectionStatus.Broken))
                throw new InvalidOperationException("Invalid connection status.");

            _requestChan.Writer.TryWrite(new RequestQueueEntry(typeof(TRequest), null, InvokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvokeCallback(object? responseBody, RequestResult result) {
                if (responseBody == null)
                    throw new ProtocolException("Response is not expected to be null");

                callback((TResponse)responseBody, result);
            };
        }

        internal ClientConnection Send(RequestQueueEntry request)
        {
            if (!(Status == ClientConnectionStatus.Connecting || Status == ClientConnectionStatus.Active || Status == ClientConnectionStatus.Broken))
                throw new InvalidOperationException("Invalid connection status.");

            _requestChan.Writer.TryWrite(request);
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;
        }

        public void InvokeCallbacks()
        {
            if (!(Status == ClientConnectionStatus.Connecting || Status == ClientConnectionStatus.Active || Status == ClientConnectionStatus.Broken))
                throw new InvalidOperationException("Invalid connection status.");

            while (_responseChan.Reader.TryRead(out ResponseQueueEntry response))
            {
                if (_requestsWaitingForResponse.TryRemove(response.RequestNo, out RequestQueueEntry request))
                {
                    if (request.Callback != null)
                        request.Callback(response.Body, response.Result);

                    Interlocked.Decrement(ref _pendingRequestsCount);
                }
            }

            if (_requestPropagatorTask?.IsFaulted ?? false)
                throw new ProtocolException("Request propagator faulted", _requestPropagatorTask?.Exception);

            if (_responseReceiverTask?.IsFaulted ?? false)
                throw new ProtocolException("Request receiver faulted", _responseReceiverTask?.Exception);
        }

        internal IEnumerable<RequestQueueEntry> ConsumePendingRequests()
        {
            while (_requestChan.Reader.TryRead(out RequestQueueEntry request))
                yield return request;

            foreach (RequestQueueEntry request in _requestsWaitingForResponse.Values)
                yield return request;

            _requestsWaitingForResponse.Clear();
        }

        private async Task PropagateRequests()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            try
            {
                Status = ClientConnectionStatus.Active;

                while (true)
                {
                    RequestQueueEntry nextRequest;

                    try
                    {
                        nextRequest = await _requestChan.Reader.ReadAsync();
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }

                    int requestNo = _requestNoGenerator.GetNext();
                    int messageId = _messageMap.GetMessageIdByType(nextRequest.Type);
                    RequestFlags flags = RequestFlags.None;

                    if (nextRequest.Body == null)
                        flags |= RequestFlags.NoBody;

                    if (nextRequest.Callback != null)
                        _requestsWaitingForResponse.TryAdd(requestNo, nextRequest);

                    await _messageWriter.Value.Write(new RequestHeader {
                        RequestNo = requestNo,
                        MessageId = messageId,
                        Flags = flags
                    });

                    if (nextRequest.Body != null)
                        await _messageWriter.Value.Write(nextRequest.Body);
                    
                    if (nextRequest.Callback == null)
                        Interlocked.Decrement(ref _pendingRequestsCount);
                }

                await _messageWriter.Value.Write(new RequestHeader{});
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Connection to '{0}' is broken", _uri);
                Status = ClientConnectionStatus.Broken;
            }
        }

        private async Task ReceiveResponses()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            try
            {
                while (true)
                {
                    var responseHeader = await _messageReader.Value.Read<ResponseHeader>();

                    if ((responseHeader.Flags & ResponseFlags.Termination) == ResponseFlags.Termination)
                        break;

                    if ((responseHeader.Flags & ResponseFlags.Error) == ResponseFlags.Error)
                    {
                        await _responseChan.Writer.WriteAsync(new ResponseQueueEntry(responseHeader.RequestNo, null, RequestResult.ServerError));
                        continue;
                    }

                    object? responseBody = null;
                    if ((responseHeader.Flags & ResponseFlags.NoBody) == ResponseFlags.None)
                    {
                        Type responseType = _messageMap.GetMessageTypeById(responseHeader.MessageId);
                        responseBody = await _messageReader.Value.Read(responseType);
                    }

                    await _responseChan.Writer.WriteAsync(new ResponseQueueEntry(responseHeader.RequestNo, responseBody, RequestResult.OK));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Connection to '{0}' is broken", _uri);
                Status = ClientConnectionStatus.Broken;
            }
        }
    }
}