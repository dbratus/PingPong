using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace PingPong.Engine
{
    public sealed class ClientConnection : IDisposable
    {
        private readonly Socket _socket;
        private readonly Lazy<DelimitedMessageReader> _messageReader;
        private readonly Lazy<DelimitedMessageWriter> _messageWriter;
        private readonly MessageMap _messageMap = new MessageMap();
        private readonly Dictionary<int, int> _reqestResponseMap = new Dictionary<int, int>();
        
        private Task? _requestPropagatorTask;
        private volatile bool _stopRequestPropagator;
        private readonly ConcurrentQueue<(Type Type, object? Body, Action<object?, bool>? Callback)> _requestQueue 
            = new ConcurrentQueue<(Type Type, object? Body, Action<object?, bool>? Callback)>();
        
        private Task? _responseReceiverTask;
        private readonly ConcurrentQueue<(int RequestNo, object? Body, bool IsError)> _responseQueue 
            = new ConcurrentQueue<(int RequestNo, object? Body, bool IsError)>();

        private readonly ConcurrentDictionary<int, Action<object?, bool>> _responseCallbacks =
            new ConcurrentDictionary<int, Action<object?, bool>>();

        private int _pendingRequestsCount;

        public bool HasPendingRequests => 
            _pendingRequestsCount > 0;

        public ClientConnection()
        {
            _socket = new Socket(SocketType.Stream, ProtocolType.IP);
            _messageReader = new Lazy<DelimitedMessageReader>(() => new DelimitedMessageReader(new NetworkStream(_socket, System.IO.FileAccess.Read, false)));
            _messageWriter = new Lazy<DelimitedMessageWriter>(() => new DelimitedMessageWriter(new NetworkStream(_socket, System.IO.FileAccess.Write, false)));
        }

        public void Dispose()
        {
            if (_requestPropagatorTask != null)
            {
                _stopRequestPropagator = true;
                _requestPropagatorTask.Wait();
            }

            if (_responseReceiverTask != null)
                _responseReceiverTask.Wait();

            if (_messageReader.IsValueCreated)
                _messageReader.Value.Dispose();
            
            if (_messageWriter.IsValueCreated)
                _messageWriter.Value.Dispose();
            
            _socket.Dispose();
        }

        public ClientConnection Connect(IPAddress address, int port)
        {
            _socket.Connect(new IPEndPoint(address, port));

            var preamble = _messageReader.Value.Read<Preamble>().Result;

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

            return this;
        }

        public ClientConnection Send<TRequest>() 
            where TRequest: class
        {
            _requestQueue.Enqueue((typeof(TRequest), null, null));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;
        }

        public ClientConnection Send<TRequest>(TRequest request) 
            where TRequest: class
        {
            _requestQueue.Enqueue((typeof(TRequest), request, null));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;
        }

        public ClientConnection Send<TRequest, TResponse>(TRequest request, Action<TResponse, bool> result)
            where TRequest: class 
            where TResponse: class
        {
            _requestQueue.Enqueue((typeof(TRequest), request, callback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void callback(object? responseBody, bool isError) {
                if (responseBody == null)
                    throw new ArgumentNullException("Response is not expected to be null");

                result((TResponse)responseBody, isError);
            };
        }

        public ClientConnection Send<TRequest, TResponse>(Action<TResponse, bool> result)
            where TRequest: class 
            where TResponse: class
        {
            _requestQueue.Enqueue((typeof(TRequest), null, callback));
            Interlocked.Increment(ref _pendingRequestsCount);
            return this;

            void callback(object? responseBody, bool isError) {
                if (responseBody == null)
                    throw new ArgumentNullException("Response is not expected to be null");

                result((TResponse)responseBody, isError);
            };
        }

        public void CheckInbox()
        {
            if (_requestPropagatorTask == null || _responseReceiverTask == null)
                throw new InvalidOperationException("Connection is not established");

            while (_responseQueue.TryDequeue(out (int RequestNo, object? Body, bool IsError) response))
            {
                if (_responseCallbacks.TryRemove(response.RequestNo, out Action<object?, bool> callback))
                {
                    callback(response.Body, response.IsError);
                    Interlocked.Decrement(ref _pendingRequestsCount);
                }
            }

            if (_requestPropagatorTask.IsFaulted)
                throw new ProtocolException("Request propagator faulted", _requestPropagatorTask.Exception);

            if (_responseReceiverTask.IsFaulted)
                throw new ProtocolException("Request receiver faulted", _responseReceiverTask.Exception);
        }

        private async Task PropagateRequests()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            int nextRequestNo = 1;

            while (!_stopRequestPropagator)
            {
                if (!_requestQueue.TryDequeue(out (Type Type, object? Body, Action<object?, bool>? Callback) nextRequest))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                    continue;
                }

                int requestNo = nextRequestNo++;
                int messageId = _messageMap.GetMessageIdByType(nextRequest.Type);
                RequestFlags flags = RequestFlags.None;

                if (nextRequest.Body == null)
                    flags |= RequestFlags.NoBody;

                if (nextRequest.Callback != null)
                    _responseCallbacks.TryAdd(requestNo, nextRequest.Callback);

                await _messageWriter.Value.Write(new RequestHeader() {
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

        private async Task ReceiveResponses()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            while (true)
            {
                var responseHeader = await _messageReader.Value.Read<ResponseHeader>();

                if ((responseHeader.Flags & ResponseFlags.Termination) == ResponseFlags.Termination)
                    break;

                if ((responseHeader.Flags & ResponseFlags.Error) == ResponseFlags.Error)
                {
                    _responseQueue.Enqueue((responseHeader.RequestNo, null, true));
                    continue;
                }

                object? responseBody = null;
                if ((responseHeader.Flags & ResponseFlags.NoBody) == ResponseFlags.None)
                {
                    Type responseType = _messageMap.GetMessageTypeById(responseHeader.MessageId);
                    responseBody = await _messageReader.Value.Read(responseType);
                }

                _responseQueue.Enqueue((responseHeader.RequestNo, responseBody, false));
            }
        }
    }
}