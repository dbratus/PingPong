using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using NLog;

namespace PingPong.Engine
{
    sealed class ServerConnection : IDisposable
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();
        
        private readonly ServiceDispatcher _dispatcher;
        private readonly Socket _socket;
        private readonly DelimitedMessageReader _messageReader;
        private readonly DelimitedMessageWriter _messageWriter;

        private struct ResponseQueueEntry
        {
            public readonly ResponseHeader Header;
            public readonly object? Body;

            public ResponseQueueEntry(ResponseHeader header, object? body)
            {
                Header = header;
                Body = body;
            }
        }
        private readonly ConcurrentQueue<ResponseQueueEntry> _responseQueue = 
            new ConcurrentQueue<ResponseQueueEntry>();
        private readonly Task _responsePropagatorTask;
        private volatile bool _stopResponsePropagator;

        public Socket Socket =>
            _socket;

        public ServerConnection(Socket socket, ServiceDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            
            _socket = socket;
            _socket.NoDelay = true;

            _messageReader = new DelimitedMessageReader(new NetworkStream(socket, System.IO.FileAccess.Read, false));
            _messageWriter = new DelimitedMessageWriter(new NetworkStream(socket, System.IO.FileAccess.Write, false));
            _responsePropagatorTask = PropagateRequests();
        }

        public void Dispose()
        {
            _stopResponsePropagator = true;
            _responsePropagatorTask.Wait();

            _messageReader.Dispose();
            _messageWriter.Dispose();
            _socket.Dispose();
        }

        public async Task Serve()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            var preamle = new Preamble
            {
                MessageIdMap = _dispatcher
                    .MessageMap
                    .Enumerate()
                    .Select(entry => new MessageIdMapEntry() {
                        MessageType = entry.Type.AssemblyQualifiedName,
                        MessageId = entry.Id
                    })
                    .ToArray(),
                RequestResponseMap = _dispatcher
                    .GetRequestResponseMap()
                    .Select(entry => new RequestResponseMapEntry {
                        RequestId = entry.RequestId,
                        ResponsetId = entry.ResponseId
                    })
                    .ToArray()
            };

            await _messageWriter.Write(preamle);

            while (true)
            {
                var requestHeader = await _messageReader.Read<RequestHeader>();
                
                if (requestHeader.MessageId == 0)
                    break;

                object? requestBody = null;
                if ((requestHeader.Flags & RequestFlags.NoBody) == RequestFlags.None)
                {
                    Type requestType = _dispatcher.MessageMap.GetMessageTypeById(requestHeader.MessageId);
                    requestBody = await _messageReader.Read(requestType);
                }

                object? responseBody = null;
                int messageId = -1;
                var responseFalgs = ResponseFlags.None;

                try
                {
                    responseBody = await _dispatcher.InvokeServiceMethod(requestHeader.MessageId, requestBody);

                    if (responseBody == null)
                        responseFalgs |= ResponseFlags.NoBody;
                    else
                        messageId = _dispatcher.MessageMap.GetMessageIdByType(responseBody.GetType());
                }
                catch(Exception ex)
                {
                    _logger.Error(ex, "Request {0} faulted.", requestHeader.RequestNo);

                    responseFalgs |= ResponseFlags.Error;
                }

                _responseQueue.Enqueue(new ResponseQueueEntry(new ResponseHeader {
                    RequestNo = requestHeader.RequestNo,
                    MessageId = messageId,
                    Flags = responseFalgs
                }, responseBody));
            }
        }

        private async Task PropagateRequests()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            while (!_stopResponsePropagator)
            {
                if (!_responseQueue.TryDequeue(out ResponseQueueEntry nextResponse))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                    continue;
                }

                await _messageWriter.Write(nextResponse.Header);
                
                if (nextResponse.Body != null)
                    await _messageWriter.Write(nextResponse.Body);
            }

            await _messageWriter.Write(new ResponseHeader{
                Flags = ResponseFlags.Termination
            });
        }
    }
}