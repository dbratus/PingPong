using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;

namespace PingPong.Engine
{
    sealed class ServerConnection : IDisposable
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly ServiceDispatcher _dispatcher;
        private readonly Socket _socket;
        private readonly ServiceHostConfig _config;
        private readonly DelimitedMessageReader _messageReader;
        private readonly DelimitedMessageWriter _messageWriter;

        private readonly ConcurrentDictionary<Task, object?> _requestHanderTasks =
            new ConcurrentDictionary<Task, object?>();

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
        private readonly Channel<ResponseQueueEntry> _responseChan = 
            Channel.CreateUnbounded<ResponseQueueEntry>(new UnboundedChannelOptions {
                SingleReader = true,
                SingleWriter = true
            });
        private readonly Task _responsePropagatorTask;

        public Socket Socket =>
            _socket;

        public ServerConnection(Socket socket, ServiceDispatcher dispatcher, ServiceHostConfig config)
        {
            _dispatcher = dispatcher;
            _socket = socket;
            _config = config;
            _messageReader = new DelimitedMessageReader(new NetworkStream(socket, System.IO.FileAccess.Read, false));
            _messageWriter = new DelimitedMessageWriter(new NetworkStream(socket, System.IO.FileAccess.Write, false));
            _responsePropagatorTask = PropagateRequests();
        }

        public void Dispose()
        {
            foreach(Task requestHandlerTask in _requestHanderTasks.Keys.ToArray())
                requestHandlerTask.Wait();
            
            _requestHanderTasks.Clear();

            _responseChan.Writer.Complete();
            _responsePropagatorTask.Wait();

            _messageReader.Dispose();
            _messageWriter.Dispose();
            _socket.Dispose();
        }

        public async Task Serve()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            var preamble = new Preamble
            {
                InstanceId = _config.InstanceId,
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

            await _messageWriter.Write(preamble);

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

                Task requestHandlerTask;
                if ((requestHeader.Flags & RequestFlags.NoResponse) == RequestFlags.None)
                    requestHandlerTask = HandleWithResponse(requestHeader, requestBody);
                else
                    requestHandlerTask = HandleWithoutResponse(requestHeader, requestBody);
                
                _requestHanderTasks.TryAdd(requestHandlerTask, null);

                CompleteRequestHandlerTask(requestHeader.RequestNo, requestHandlerTask);
            }

            foreach(Task requestHandlerTask in _requestHanderTasks.Keys.ToArray())
                await requestHandlerTask;

            async Task HandleWithResponse(RequestHeader requestHeader, object? requestBody)
            {
                await Task.Yield();

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
                catch (Exception)
                {
                    responseFalgs |= ResponseFlags.Error;
                    throw;
                }
                finally
                {
                    await _responseChan.Writer.WriteAsync(new ResponseQueueEntry(new ResponseHeader {
                        RequestNo = requestHeader.RequestNo,
                        MessageId = messageId,
                        Flags = responseFalgs
                    }, responseBody));
                }
            }

            async Task HandleWithoutResponse(RequestHeader requestHeader, object? requestBody)
            {
                await Task.Yield();
                await _dispatcher.InvokeServiceMethod(requestHeader.MessageId, requestBody);
            }

            async void CompleteRequestHandlerTask(int requestNo, Task task)
            {
                try
                {
                    await task;

                    _requestHanderTasks.TryRemove(task, out var _);
                }
                catch(Exception ex)
                {
                    _logger.Error(ex, "Request {0} faulted.", requestNo);
                }
            }
        }

        private async Task PropagateRequests()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            try
            {
                while (true)
                {
                    ResponseQueueEntry nextResponse;

                    try
                    {
                        nextResponse = await _responseChan.Reader.ReadAsync();
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }

                    await _messageWriter.Write(nextResponse.Header);
                    
                    if (nextResponse.Body != null)
                        await _messageWriter.Write(nextResponse.Body);
                }

                await _messageWriter.Write(new ResponseHeader{
                    Flags = ResponseFlags.Termination
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Response propagator faulted");
            }
        }
    }
}