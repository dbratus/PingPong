using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;
using PingPong.Engine.Messages;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    sealed class ClientConnection : IDisposable
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan _propagatorCloseTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan _receiverCloseTimeout = TimeSpan.FromSeconds(30);

        private readonly string _uri;
        public string Uri =>
            _uri;

        private readonly Socket _socket;

        private sealed class Network
        {
            public SslStream? TlsStream { get; private set; }
            public DelimitedMessageReader MessageReader { get; private set; }
            public DelimitedMessageWriter MessageWriter { get; private set; }

            public Network(SslStream? tlsStream, DelimitedMessageReader messageReader, DelimitedMessageWriter messageWriter)
            {
                TlsStream = tlsStream;
                MessageReader = messageReader;
                MessageWriter = messageWriter;
            }
        }
        private readonly Lazy<Network> _net;

        private readonly MessageMap _messageMap = new MessageMap();
        private readonly Dictionary<int, int> _reqestResponseMap 
            = new Dictionary<int, int>();
        public IEnumerable<Type> SupportedRequestTypes =>
            _reqestResponseMap.Keys.Select(_messageMap.GetMessageTypeById);

        public IEnumerable<(Type RequestType, Type? ResponseType)> RequestResponseMap =>
            _reqestResponseMap
                .Select(kv => (
                    _messageMap.GetMessageTypeById(kv.Key),
                    kv.Value > 0 ? _messageMap.GetMessageTypeById(kv.Value) : null
                ))
                .Distinct();

        private readonly RequestNoGenerator _requestNoGenerator;

        private Task? _requestPropagatorTask;

        internal struct RequestQueueEntry
        {
            public readonly int InstanceId;
            public readonly RequestFlags Flags;
            public readonly Type Type;
            public readonly object? Body;
            public readonly Action<object?, RequestResult>? Callback;
            public readonly DateTime CreationTime;

            public RequestQueueEntry(int instanceId, RequestFlags flags, Type type, object? body, Action<object?, RequestResult>? callback)
            {
                InstanceId = instanceId;
                Flags = flags;
                Type = type;
                Body = body;
                Callback = callback;
                CreationTime = DateTime.Now;
            }
        }
        private readonly Channel<RequestQueueEntry> _requestChan 
            = Channel.CreateUnbounded<RequestQueueEntry>(new UnboundedChannelOptions {
                SingleReader = true
            });

        private Task? _responseReceiverTask;

        private struct ResponseQueueEntry
        {
            public readonly long RequestNo;
            public readonly object? Body;
            public readonly RequestResult Result;

            public ResponseQueueEntry(long requestNo, object? body, RequestResult result)
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

        private readonly ConcurrentDictionary<long, RequestQueueEntry> _requestsWaitingForResponse =
            new ConcurrentDictionary<long, RequestQueueEntry>();

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

        private volatile int _instanceId = -1;
        public int InstanceId =>
            _instanceId;

        public sealed class HostStatusData
        {
            private volatile int _pendingProcessing;
            private volatile int _inProcessing;
            private volatile int _pendingResponsePropagation;

            public int PendingProcessing 
            { 
                get => _pendingProcessing;
                set => _pendingProcessing = value;
            }

            public int InProcessing
            { 
                get => _inProcessing;
                set => _inProcessing = value;
            }

            public int PendingResponsePropagation
            { 
                get => _pendingResponsePropagation;
                set => _pendingResponsePropagation = value;
            }
        }
        private readonly HostStatusData _hostStatus = new HostStatusData();
        public HostStatusData HostStatus =>
            _hostStatus;

        private readonly ClientTlsSettings _tlsSettings;

        private readonly Dictionary<(long, long), Type> _messageHashMap;

        public ClientConnection(string uri) :
            this(new RequestNoGenerator(), new ClientTlsSettings(), new SerializerMessagePack(), MessageName.FindMessageTypes(), uri)
        {
        }

        public ClientConnection(ClientTlsSettings tlsSettings, ISerializer serializer, string uri) :
            this(new RequestNoGenerator(), tlsSettings, serializer, MessageName.FindMessageTypes(), uri)
        {
        }

        internal ClientConnection(RequestNoGenerator requestNoGenerator, ClientTlsSettings tlsSettings, ISerializer serializer, Dictionary<(long, long), Type> messageHashMap, string uri)
        {
            _uri = uri;
            _messageHashMap = messageHashMap;
            _socket = new Socket(SocketType.Stream, ProtocolType.IP);

            _net = new Lazy<Network>(() => {
                var uriBuilder = new UriBuilder(uri);
                Stream readerStream, writerStream;
                SslStream? tlsStream = null;

                switch (uriBuilder.Scheme)
                {
                    case "tcp":
                        readerStream = new NetworkStream(_socket, FileAccess.Read, false);
                        writerStream = new NetworkStream(_socket, FileAccess.Write, false);
                    break;
                    case "tls":
                        tlsStream = new SslStream(new NetworkStream(_socket, FileAccess.ReadWrite, false), true, TlsAuthenticate);
                        readerStream = writerStream = tlsStream;
                    break;
                    default:
                        throw new ProtocolException($"Scheme '{uriBuilder.Scheme}' is not supported.");
                }

                var messageReader = new DelimitedMessageReader(readerStream, serializer);
                var messageWriter = new DelimitedMessageWriter(writerStream, serializer);

                return new Network(tlsStream, messageReader, messageWriter);
            });

            _requestNoGenerator = requestNoGenerator;
            _tlsSettings = tlsSettings;
        }

        public void Dispose()
        {
            if (Status == ClientConnectionStatus.Disposed)
                throw new InvalidOperationException("Connection already disposed.");

            if (_requestPropagatorTask != null)
            {
                _requestChan.Writer.Complete();
                _requestPropagatorTask.Wait(_propagatorCloseTimeout);
            }

            _responseReceiverTask?.Wait(_receiverCloseTimeout);

            if (_net.IsValueCreated)
            {
                Network net = _net.Value;

                net.MessageReader.Dispose();
                net.MessageWriter.Dispose();
                net.TlsStream?.Dispose();
            }

            _socket.Dispose();

            Status = ClientConnectionStatus.Disposed;
        }

        public async Task Connect(TimeSpan delay)
        {
            {
                int status = _status;

                if (status != (int)ClientConnectionStatus.NotConnected)
                    throw new InvalidOperationException($"Invalid connection status {(ClientConnectionStatus)status}.");
                
                int newStatus;
                if ((newStatus = Interlocked.CompareExchange(ref _status, (int)ClientConnectionStatus.Connecting, status)) != status)
                    throw new InvalidOperationException($"Invalid connection status {(ClientConnectionStatus)newStatus}.");
            }

            if (delay > TimeSpan.Zero)
            {
                _logger.Info("Connecting to '{0}' in {1} {2}", _uri, delay, Status);

                await Task.Delay(delay);
            }
            else
            {
                _logger.Info("Connecting to '{0}'", _uri);
            }

            try
            {
                var uriBuilder = new UriBuilder(_uri);
                
                await _socket.ConnectAsync(uriBuilder.Host, uriBuilder.Port);

                Network net = _net.Value;

                if (net.TlsStream != null)
                    await net.TlsStream.AuthenticateAsClientAsync(uriBuilder.Host);

                var preamble = await net.MessageReader.Read<Preamble>();

                _instanceId = preamble.InstanceId;

                foreach (MessageIdMapEntry ent in preamble.MessageIdMap)
                {
                    if (!_messageHashMap.TryGetValue((ent.MessageTypeHashLo, ent.MessageTypeHashHi), out Type messageType))
                        throw new ProtocolException($"Message type not found {MessageName.HashToString(ent.MessageTypeHashLo, ent.MessageTypeHashHi)}");

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

                throw;
            }
        }

        private bool TlsAuthenticate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors && _tlsSettings.AllowSelfSignedCertificates)
                return true;

            _logger.Warn("Server TLS certificate rejected {0}", sslPolicyErrors);
            return false;
        }

        public ClientConnection Send<TRequest>(bool instanceAffinity) 
            where TRequest: class
        {
            ValidateSend(typeof(TRequest), null);

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.None, typeof(TRequest), null, null));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;
        }

        public ClientConnection Send<TRequest>(bool instanceAffinity, TRequest request)
            where TRequest: class
        {
            ValidateSend(typeof(TRequest), null);

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.None, typeof(TRequest), request, null));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;
        }

        public ClientConnection Send<TRequest, TResponse>(bool instanceAffinity, TRequest request, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend(typeof(TRequest), typeof(TResponse));

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.None, typeof(TRequest), request, InvlokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public ClientConnection OpenChannel<TRequest, TResponse>(bool instanceAffinity, TRequest request, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend(typeof(TRequest), typeof(TResponse));

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.OpenChannel, typeof(TRequest), request, InvlokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public ClientConnection Send<TRequest>(bool instanceAffinity, TRequest request, Action<RequestResult> callback)
            where TRequest: class
        {
            ValidateSend(typeof(TRequest), null);

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.None, typeof(TRequest), request, InvlokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback(result);
            };
        }

        public ClientConnection OpenChannel<TRequest>(bool instanceAffinity, TRequest request, Action<RequestResult> callback)
            where TRequest: class
        {
            ValidateSend(typeof(TRequest), null);

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.OpenChannel, typeof(TRequest), request, InvlokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvlokeCallback(object? responseBody, RequestResult result) {
                callback(result);
            };
        }

        public ClientConnection Send<TRequest, TResponse>(bool instanceAffinity, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend(typeof(TRequest), typeof(TResponse));

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.None, typeof(TRequest), null, InvokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public ClientConnection OpenChannel<TRequest, TResponse>(bool instanceAffinity, Action<TResponse?, RequestResult> callback)
            where TRequest: class 
            where TResponse: class
        {
            ValidateSend(typeof(TRequest), typeof(TResponse));

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.OpenChannel, typeof(TRequest), null, InvokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvokeCallback(object? responseBody, RequestResult result) {
                callback((TResponse?)responseBody, result);
            };
        }

        public ClientConnection Send<TRequest>(bool instanceAffinity, Action<RequestResult> callback)
            where TRequest: class
        {
            ValidateSend(typeof(TRequest), null);

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.None, typeof(TRequest), null, InvokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvokeCallback(object? responseBody, RequestResult result) {
                callback(result);
            };
        }

        public ClientConnection OpenChannel<TRequest>(bool instanceAffinity, Action<RequestResult> callback)
            where TRequest: class 
        {
            ValidateSend(typeof(TRequest), null);

            _requestChan.Writer.TryWrite(new RequestQueueEntry(instanceAffinity ? _instanceId : -1, RequestFlags.OpenChannel, typeof(TRequest), null, InvokeCallback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void InvokeCallback(object? responseBody, RequestResult result) {
                callback(result);
            };
        }

        internal ClientConnection Send(RequestQueueEntry request)
        {
            ValidateSend(request.Type, null);

            _requestChan.Writer.TryWrite(request);
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;
        }

        private void ValidateSend(Type request, Type? response)
        {
            ClientConnectionStatus status = Status;
            if (!(status == ClientConnectionStatus.Connecting || status == ClientConnectionStatus.Active || status == ClientConnectionStatus.Broken))
                throw new InvalidOperationException($"Invalid connection status {status}.");

            if (!_messageMap.TryGetMessageIdByType(request, out int requestMessageId))
                throw new ProtocolException($"Request type {request.FullName} not supported");

            if (response != null)
            {
                if (!_messageMap.TryGetMessageIdByType(response, out int responseMessageId))
                    throw new ProtocolException($"Response type {response.FullName} not supported");

                if (!_reqestResponseMap.TryGetValue(requestMessageId, out int expectedResponseMessageId))
                    throw new ProtocolException($"Message type {request.FullName} is known, but not a request.");

                if (responseMessageId != expectedResponseMessageId)
                    throw new ProtocolException($"The message {response.FullName} is not a response for {request.FullName}");
            }
            else
            {
                if (!_reqestResponseMap.TryGetValue(requestMessageId, out int _))
                    throw new ProtocolException($"Message type {request.FullName} is known, but not a request.");
            }
        }

        public void Update()
        {
            ClientConnectionStatus status = Status;

            if (status == ClientConnectionStatus.Connecting)
                return;

            if (!(status == ClientConnectionStatus.Active || status == ClientConnectionStatus.Broken))
                throw new InvalidOperationException($"Invalid connection status {status}.");

            while (_responseChan.Reader.TryRead(out ResponseQueueEntry response))
            {
                if (_requestsWaitingForResponse.TryRemove(response.RequestNo, out RequestQueueEntry request))
                {
                    if (request.Callback != null)
                        request.Callback(response.Body, response.Result);
                    
                    // If the request is a stream opening request and the response body is not empty,
                    // put back the handler to wait for further responses.
                    if ((request.Flags & RequestFlags.OpenChannel) == RequestFlags.OpenChannel && response.Body != null)
                        _requestsWaitingForResponse.TryAdd(response.RequestNo, request);
                    else
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

                    long requestNo = _requestNoGenerator.GetNext();
                    int messageId = _messageMap.GetMessageIdByType(nextRequest.Type);
                    RequestFlags flags = nextRequest.Flags;

                    if (nextRequest.Body == null)
                        flags |= RequestFlags.NoBody;

                    if (nextRequest.Callback != null)
                        _requestsWaitingForResponse.TryAdd(requestNo, nextRequest);
                    else
                        flags |= RequestFlags.NoResponse;

                    try
                    {
                        await _net.Value.MessageWriter.Write(new RequestHeader {
                            RequestNo = requestNo,
                            MessageId = messageId,
                            Flags = flags
                        });

                        if (nextRequest.Body != null)
                            await _net.Value.MessageWriter.Write(nextRequest.Body);
                        
                        if (nextRequest.Callback == null)
                            Interlocked.Decrement(ref _pendingRequestsCount);
                    }
                    catch (ObjectDisposedException)
                    {
                        // The connection is being closed. The socket was disposed by timeout.
                        return;
                    }
                    catch
                    {
                        // Returning the request to the channel to prevent its loss in case of 
                        // network failures.
                        _requestChan.Writer.TryWrite(nextRequest);
                        throw;
                    }
                }

                await _net.Value.MessageWriter.Write(new RequestHeader{});
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
                    var responseHeader = await _net.Value.MessageReader.Read<ResponseHeader>();

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
                        responseBody = await _net.Value.MessageReader.Read(responseType);
                    }

                    if ((responseHeader.Flags & ResponseFlags.HostStatus) == ResponseFlags.HostStatus)
                    {
                        var hostStatusMessage = await _net.Value.MessageReader.Read<HostStatusMessage>();

                        _hostStatus.PendingProcessing = hostStatusMessage.PendingProcessing;
                        _hostStatus.InProcessing = hostStatusMessage.InProcessing;
                        _hostStatus.PendingResponsePropagation = hostStatusMessage.PendingResponsePropagation;
                    }

                    await _responseChan.Writer.WriteAsync(new ResponseQueueEntry(responseHeader.RequestNo, responseBody, RequestResult.OK));
                }
            }
            catch (ObjectDisposedException)
            {
                // The connection is being closed. The socket was disposed by timeout.
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Connection to '{0}' is broken", _uri);
                Status = ClientConnectionStatus.Broken;
            }
        }
    }
}