using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autofac;
using PingPong.Engine.Messages;
using PingPong.HostInterfaces;

namespace PingPong.Engine
{
    sealed class ServiceDispatcher
    {
        private readonly Dictionary<int, IRequestHanlder> _messageHandlersById = 
            new Dictionary<int, IRequestHanlder>();
        private readonly Dictionary<int, int> _requestResponseMessageMap =
            new Dictionary<int, int>();
        private readonly ClusterConnection _clusterConnection;

        private readonly MessageMap _messageMap = new MessageMap();
        public MessageMap MessageMap =>
            _messageMap;

        private int _nextMessageId = 1;

        public ServiceDispatcher(IContainer container, List<Type> serviceTypes, ClusterConnection clusterConnection)
        {
            _clusterConnection = clusterConnection;

            foreach (Type serviceType in serviceTypes)
            {
                var serviceTypeCapture = serviceType;
                var serviceInstance = new Lazy<object>(() => container.Resolve(serviceTypeCapture));

                foreach (MethodInfo serviceMethod in serviceType.GetMethods())
                {
                    if (serviceMethod.IsStatic || !serviceMethod.IsPublic)
                        continue;

                    var newHandler = RequestHandlerBase.CreateFromMethod(serviceInstance, serviceMethod);

                    if (newHandler != null)
                    {
                        Type requestMessageType = newHandler.RequestMessageType;

                        int requestMessageId;
                        if (!_messageMap.ContainsType(requestMessageType))
                        {
                            requestMessageId = _nextMessageId++;
                            _messageMap.Add(requestMessageType, requestMessageId);
                        }
                        else
                        {
                            requestMessageId = _messageMap.GetMessageIdByType(requestMessageType);
                        }

                        if (_messageHandlersById.TryGetValue(requestMessageId, out IRequestHanlder existingHandler))
                            _messageHandlersById[requestMessageId] = new LinkedRequestHandler(newHandler, existingHandler);
                        else
                            _messageHandlersById.Add(requestMessageId, newHandler);

                        Type? responseMessageType = newHandler.ResponseMessageType;

                        if (responseMessageType != null)
                        {
                            if (!_messageMap.ContainsType(responseMessageType))
                            {
                                int responseMessageId = _nextMessageId++;
                                _messageMap.Add(responseMessageType, responseMessageId);
                                _requestResponseMessageMap[requestMessageId] = responseMessageId;
                            }
                            else
                            {
                                _requestResponseMessageMap[requestMessageId] = _messageMap.GetMessageIdByType(responseMessageType);
                            }
                        }
                        else
                        {
                            _requestResponseMessageMap[requestMessageId] = -1;
                        }
                    }
                }
            }
        }

        public void InitGatewayRouts()
        {
            foreach ((Type requestType, Type? responseType) in _clusterConnection.RequestResponseMap)
            {
                int requestTypeId;
                if (!_messageMap.TryGetMessageIdByType(requestType, out requestTypeId))
                {
                    requestTypeId = _nextMessageId++;
                    _messageMap.Add(requestType, requestTypeId);
                }

                int responseTypeId = -1;
                if (responseType != null)
                {
                    if (!_messageMap.TryGetMessageIdByType(responseType, out responseTypeId))
                    {
                        responseTypeId = _nextMessageId++;
                        _messageMap.Add(responseType, responseTypeId);
                    }
                }

                // Routing handlers do not replace the own handlers of the host.
                if (!_messageHandlersById.ContainsKey(requestTypeId))
                {
                    IRequestHanlder requestHanlder;

                    if (responseType != null)
                        requestHanlder = new RoutingRequestHandler(_clusterConnection, requestType);
                    else
                        requestHanlder = new RoutingOneWayRequestHandler(_clusterConnection, requestType);

                    _messageHandlersById.Add(requestTypeId, requestHanlder);
                    _requestResponseMessageMap.Add(requestTypeId, responseTypeId);
                }
            }
        }

        public IEnumerable<(int RequestId, int ResponseId)> GetRequestResponseMap() =>
            _requestResponseMessageMap.Select(kv => (kv.Key, kv.Value));

        public Task<object?> InvokeServiceMethod(RequestHeader header, object? request) =>
            _messageHandlersById[header.MessageId].Invoke(header, request);

        private interface IRequestHanlder
        {
            Task<object?> Invoke(RequestHeader header, object? request);
        }

        private abstract class RequestHandlerBase : IRequestHanlder
        {
            protected readonly Lazy<object> _serviceInstance;
            protected readonly MethodInfo _method;

            protected RequestHandlerBase(Lazy<object> serviceInstance, MethodInfo method)
            {
                _serviceInstance = serviceInstance;
                _method = method;
            }

            protected static TLambda CreateServiceMethodInvoker<TLambda>(MethodInfo method)
            {
                Type requestType = method.GetParameters()[0].ParameterType;

                var instanceParam = Expression.Parameter(typeof(object));
                var requestParam = Expression.Parameter(typeof(object));
                var lambdaBody = Expression.Call(
                    Expression.Convert(instanceParam, method.DeclaringType),
                    method,
                    new [] { Expression.Convert(requestParam, requestType) }
                );
                var lambda = Expression.Lambda<TLambda>(
                    lambdaBody, 
                    false, 
                    new [] { instanceParam, requestParam }
                );

                return lambda.Compile();
            }

            public static RequestHandlerBase? CreateFromMethod(Lazy<object> serviceInstance, MethodInfo method)
            {
                ParameterInfo[] methodParams = method.GetParameters();
                if (methodParams.Length != 1)
                    return null;

                if (!methodParams[0].ParameterType.IsClass)
                    return null;

                if (method.ReturnType == typeof(Task))
                    return new AsyncOneWayRequestHandler(serviceInstance, method);
                else if (method.ReturnType.IsSubclassOf(typeof(Task)))
                    return new AsyncRequestHandler(serviceInstance, method);
                else if (method.ReturnType == typeof(void))
                    return new SyncOneWayRequestHandler(serviceInstance, method);
                else if (method.ReturnType.IsClass)
                    return new SyncRequestHandler(serviceInstance, method);

                return null;
            }

            public Type RequestMessageType =>
                _method.GetParameters()[0].ParameterType;

            public abstract Type? ResponseMessageType { get; }

            public abstract Task<object?> Invoke(RequestHeader header, object? request);
        }

        private sealed class LinkedRequestHandler : IRequestHanlder
        {
            private readonly IRequestHanlder _head;
            private readonly IRequestHanlder _tail;

            public LinkedRequestHandler(IRequestHanlder head, IRequestHanlder tail)
            {
                _head = head;
                _tail = tail;
            }

            public async Task<object?> Invoke(RequestHeader header, object? request)
            {
                await _tail.Invoke(header, request);
                return await _head.Invoke(header, request);
            }
        }

        private class AsyncOneWayRequestHandler : RequestHandlerBase
        {
            protected readonly Func<object, object?, Task> _serviceMethod;

            public AsyncOneWayRequestHandler(Lazy<object> serviceInstance, MethodInfo method) 
                : base(serviceInstance, method)
            {
                _serviceMethod = CreateServiceMethodInvoker<Func<object, object?, Task>>(method);
            }

            public override Type? ResponseMessageType =>
                null;

            public override Task<object?> Invoke(RequestHeader header, object? request)
            {
                var taskCompletion = new TaskCompletionSource<object?>();

                _serviceMethod(_serviceInstance.Value, request)
                    .ContinueWith(task => {
                        if (task.IsFaulted)
                        {
                            taskCompletion.SetException(task.Exception);
                            return;
                        }

                        taskCompletion.SetResult(null);
                    });
                
                return taskCompletion.Task;
            }
        }

        private sealed class AsyncRequestHandler : AsyncOneWayRequestHandler
        {
            private readonly Type _responseMessageType;
            private readonly Func<Task, object?> _getTaskResult;

            public AsyncRequestHandler(Lazy<object> serviceInstance, MethodInfo method) 
                : base(serviceInstance, method)
            {
                _responseMessageType = _method.ReturnType.GetGenericArguments()[0];
                _getTaskResult = CreateTaskResultGetter(_responseMessageType, _method.ReturnType);
            }

            private static Func<Task, object?> CreateTaskResultGetter(Type responseMessageType, Type taskType)
            {
                MethodInfo resultGetter = taskType.GetProperty("Result").GetGetMethod();

                var taskParam = Expression.Parameter(typeof(Task));
                var lambdaBody = Expression.Property(
                    Expression.Convert(taskParam, taskType),
                    resultGetter
                );
                var lambda = Expression.Lambda<Func<Task, object?>>(
                    lambdaBody,
                    false,
                    new [] { taskParam }
                );

                return lambda.Compile();
            }

            public override Type ResponseMessageType =>
                _responseMessageType;

            public override Task<object?> Invoke(RequestHeader header, object? request)
            {
                var taskCompletion = new TaskCompletionSource<object?>();
                
                _serviceMethod(_serviceInstance.Value, request)
                    .ContinueWith(task => {
                        if (task.IsFaulted)
                        {
                            taskCompletion.SetException(task.Exception);
                            return;
                        }

                        taskCompletion.SetResult(_getTaskResult(task));
                    });

                return taskCompletion.Task;
            }
        }

        private sealed class SyncRequestHandler : RequestHandlerBase
        {
            private readonly Func<object, object?, object?> _serviceMethod;

            public SyncRequestHandler(Lazy<object> serviceInstance, MethodInfo method) 
                : base(serviceInstance, method)
            {
                _serviceMethod = CreateServiceMethodInvoker<Func<object, object?, object?>>(method);
            }

            public override Type? ResponseMessageType =>
                _method.ReturnType.IsClass ? _method.ReturnType : null;

            public override Task<object?> Invoke(RequestHeader header, object? request) =>
                Task.FromResult<object?>(_serviceMethod(_serviceInstance.Value, request));
        }

        private sealed class SyncOneWayRequestHandler : RequestHandlerBase
        {
            private readonly Action<object, object?> _serviceMethod;

            public SyncOneWayRequestHandler(Lazy<object> serviceInstance, MethodInfo method) 
                : base(serviceInstance, method)
            {
                _serviceMethod = CreateServiceMethodInvoker<Action<object, object?>>(method);
            }

            public override Type? ResponseMessageType =>
                _method.ReturnType.IsClass ? _method.ReturnType : null;

            public override Task<object?> Invoke(RequestHeader header, object? request)
            {
                _serviceMethod(_serviceInstance.Value, request);
                return Task.FromResult<object?>(null);
            }
        }

        private class RoutingOneWayRequestHandler : IRequestHanlder
        {
            protected readonly ClusterConnection _clusterConnection;
            protected readonly Type _requestType;

            public RoutingOneWayRequestHandler(ClusterConnection clusterConnection, Type requestType)
            {
                _clusterConnection = clusterConnection;
                _requestType = requestType;
            }

            public virtual Task<object?> Invoke(RequestHeader header, object? request)
            {
                _clusterConnection.Send(request, _requestType);
                return Task.FromResult<object?>(null);
            }
        }

        private sealed class RoutingRequestHandler : RoutingOneWayRequestHandler
        {
            private static readonly ConcurrentDictionary<RequestHeader, ChannelReader<(object? Response, RequestResult Result)>> _currentChannels =
                new ConcurrentDictionary<RequestHeader, ChannelReader<(object? Response, RequestResult Result)>>();

            public RoutingRequestHandler(ClusterConnection clusterConnection, Type requestType)
                : base(clusterConnection, requestType)
            {
            }

            public override async Task<object?> Invoke(RequestHeader header, object? request)
            {
                if ((header.Flags & RequestFlags.OpenChannel) == RequestFlags.OpenChannel)
                {
                    var channel = _currentChannels.GetOrAdd(header, h => 
                        _clusterConnection.OpenChannelAsync(request, _requestType)
                    );

                    try
                    {
                        (object? response, RequestResult result) = await channel.ReadAsync();

                        if (result != RequestResult.OK)
                        {
                            _currentChannels.Remove(header, out channel);
                            throw new CommunicationException($"Failed to route a message: ${result}.");
                        }

                        return response;
                    }
                    catch (ChannelClosedException)
                    {
                        _currentChannels.Remove(header, out channel);
                        return null;
                    }
                    catch (Exception)
                    {
                        _currentChannels.Remove(header, out channel);
                        throw;
                    }
                }
                else
                {
                    return await _clusterConnection.SendAsync(request, _requestType);
                }
            }
        }
    }
}