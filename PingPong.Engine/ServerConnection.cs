using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
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
        private readonly ServiceHostCounters _counters;

        private sealed class TlsData
        {
            public X509Certificate Certificate { get; private set; }
            public SslStream Stream { get; private set; }

            public TlsData(X509Certificate certificate, SslStream stream)
            {
                Certificate = certificate;
                Stream = stream;
            }
        }
        private readonly TlsData? _tls;

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

        private Task? _hostStatusSenderTask;
        private readonly CancellationTokenSource _hostStatusSenderCancellation = 
            new CancellationTokenSource();

        public Socket Socket =>
            _socket;

        public ServerConnection(Socket socket, ServiceDispatcher dispatcher, ServiceHostConfig config, ServiceHostCounters counters, ISerializer serializer, X509Certificate? certificate)
        {
            _dispatcher = dispatcher;
            _socket = socket;
            _config = config;

            Stream readerStream, writerStream;

            if (config.TlsSettings == null || certificate == null)
            {
                readerStream = new NetworkStream(socket, FileAccess.Read, false);
                writerStream = new NetworkStream(socket, FileAccess.Write, false);
            }
            else
            {
                var networkStream = new NetworkStream(socket, FileAccess.ReadWrite, false);
                var tlsStream = new SslStream(networkStream, true);

                readerStream = writerStream = tlsStream;

                _tls = new TlsData(certificate, tlsStream);
            }
            
            _messageReader = new DelimitedMessageReader(readerStream, serializer);
            _messageWriter = new DelimitedMessageWriter(writerStream, serializer);

            _responsePropagatorTask = PropagateResponses();
            _counters = counters;
        }

        public void Dispose()
        {
            foreach(Task requestHandlerTask in _requestHanderTasks.Keys.ToArray())
                requestHandlerTask.Wait();
            
            _requestHanderTasks.Clear();

            _hostStatusSenderCancellation.Cancel();
            _hostStatusSenderTask?.Wait();

            _responseChan.Writer.Complete();
            _responsePropagatorTask.Wait();

            _messageReader.Dispose();
            _messageWriter.Dispose();
            _tls?.Stream.Dispose();

            _socket.Dispose();
        }

        public async Task Serve()
        {
            // Yield to get rid of the sync section.
            await Task.Yield();

            if (_tls != null)
                await _tls.Stream.AuthenticateAsServerAsync(_tls.Certificate);

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

            _hostStatusSenderTask = SendHostStatus();

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

                _counters.PendingProcessing.Increment();

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
        }

        private async Task HandleWithResponse(RequestHeader requestHeader, object? requestBody)
        {
            await Task.Yield();

            _counters.PendingProcessing.Decrement();
            _counters.InProcessing.Increment();

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
                _counters.PendingResponsePropagation.Increment();

                await _responseChan.Writer.WriteAsync(new ResponseQueueEntry(new ResponseHeader {
                    RequestNo = requestHeader.RequestNo,
                    MessageId = messageId,
                    Flags = responseFalgs
                }, responseBody));
            }
        }

        private async Task HandleWithoutResponse(RequestHeader requestHeader, object? requestBody)
        {
            await Task.Yield();
            
            _counters.PendingProcessing.Decrement();
            _counters.InProcessing.Increment();

            await _dispatcher.InvokeServiceMethod(requestHeader.MessageId, requestBody);
        }

        private async void CompleteRequestHandlerTask(int requestNo, Task task)
        {
            try
            {
                await task;

                _requestHanderTasks.TryRemove(task, out var _);

                _counters.InProcessing.Decrement();
            }
            catch(Exception ex)
            {
                _logger.Error(ex, "Request {0} faulted.", requestNo);
            }
        }

        private async Task PropagateResponses()
        {
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

                    if ((nextResponse.Header.Flags & ResponseFlags.HostStatus) == ResponseFlags.HostStatus)
                    {
                        await _messageWriter.Write(new HostStatusMessage {
                            PendingProcessing = _counters.PendingProcessing.Count,
                            InProcessing = _counters.InProcessing.Count,
                            PendingResponsePropagation = _counters.PendingResponsePropagation.Count
                        });
                    }

                    _counters.PendingResponsePropagation.Decrement();
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

        private async Task SendHostStatus()
        {
            while (true)
            {
                try 
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.StatusMessageInterval), _hostStatusSenderCancellation.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                await _responseChan.Writer.WriteAsync(new ResponseQueueEntry(new ResponseHeader {
                    Flags = ResponseFlags.HostStatus | ResponseFlags.NoBody
                }, null));
            }
        }
    }
}