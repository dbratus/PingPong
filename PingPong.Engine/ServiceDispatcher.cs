using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Autofac;
using NLog;

namespace PingPong.Engine
{
    sealed class ServiceDispatcher
    {
        private readonly Dictionary<int, RequestHandlerBase> _messageHandlersById = 
            new Dictionary<int, RequestHandlerBase>();
        private readonly (int RequestId, int ResponseId)[] _requestResponseMessageMap;
        private readonly MessageMap _messageMap = new MessageMap();

        public MessageMap MessageMap =>
            _messageMap;

        public ServiceDispatcher(IContainer container, List<Type> serviceTypes)
        {
            int nextMessageId = 1;
            var requestResponseMessageMap = new List<(int, int)>();

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
                        Type requestMessageType = newHandler.GetRequestMessageType();

                        if (_messageMap.ContainsType(requestMessageType))
                        {
                            Console.WriteLine($"Message ${requestMessageType.FullName} handled by more then one handler.");
                            continue;
                        }

                        Type? responseMessageType = newHandler.GetResponseMessageType();

                        if (responseMessageType != null && _messageMap.ContainsType(responseMessageType))
                        {
                            Console.WriteLine($"Message ${responseMessageType.FullName} is used as response by more than one handler.");
                            continue;
                        }

                        int requestMessageId = nextMessageId++;

                        _messageMap.Add(requestMessageType, requestMessageId);
                        _messageHandlersById.Add(requestMessageId, newHandler);

                        if (responseMessageType != null)
                        {
                            int responseMessageId = nextMessageId++;
                            _messageMap.Add(responseMessageType, responseMessageId);

                            requestResponseMessageMap.Add((requestMessageId, responseMessageId));
                        }
                        else
                        {
                            requestResponseMessageMap.Add((requestMessageId, -1));
                        }
                    }
                }
            }

            _requestResponseMessageMap = requestResponseMessageMap.ToArray();
        }

        public IEnumerable<(int RequestId, int ResponseId)> GetRequestResponseMap() =>
            _requestResponseMessageMap;

        public Task<object?> InvokeServiceMethod(int messageId, object? request) =>
            _messageHandlersById[messageId].Invoke(request);

        private abstract class RequestHandlerBase
        {
            protected readonly Lazy<object> _serviceInstance;
            protected readonly MethodInfo _method;

            protected RequestHandlerBase(Lazy<object> serviceInstance, MethodInfo method)
            {
                _serviceInstance = serviceInstance;
                _method = method;
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
                else if (method.ReturnType.IsClass || method.ReturnType == typeof(void))
                    return new SyncRequestHandler(serviceInstance, method);

                return null;
            }

            public Type GetRequestMessageType() =>
                _method.GetParameters()[0].ParameterType;

            public abstract Type? GetResponseMessageType();

            public abstract Task<object?> Invoke(object? request);
        }

        private sealed class AsyncRequestHandler : RequestHandlerBase
        {
            public AsyncRequestHandler(Lazy<object> serviceInstance, MethodInfo method) : base(serviceInstance, method)
            {
            }

            public override Type GetResponseMessageType() =>
                _method.ReturnType.GetGenericArguments()[0];

            public override Task<object?> Invoke(object? request)
            {
                var taskCompletion = new TaskCompletionSource<object?>();
                
                ((Task)_method.Invoke(_serviceInstance.Value, new object?[] { request }))
                    .ContinueWith((Task t) => {
                        if (t.IsFaulted)
                        {
                            taskCompletion.SetException(t.Exception);
                            return;
                        }

                        dynamic taskWithResult = t;
                        object result = taskWithResult.Result;
                        
                        taskCompletion.SetResult(result);
                    });

                return taskCompletion.Task;
            }
        }

        private sealed class AsyncOneWayRequestHandler : RequestHandlerBase
        {
            public AsyncOneWayRequestHandler(Lazy<object> serviceInstance, MethodInfo method) : base(serviceInstance, method)
            {
            }

            public override Type? GetResponseMessageType() =>
                null;

            public override Task<object?> Invoke(object? request)
            {
                var taskCompletion = new TaskCompletionSource<object?>();

                ((Task)_method.Invoke(_serviceInstance.Value, new object?[] { request }))
                    .ContinueWith(t => {
                        if (t.IsFaulted)
                        {
                            taskCompletion.SetException(t.Exception);
                            return;
                        }

                        taskCompletion.SetResult(null);
                    });
                
                return taskCompletion.Task;
            }
        }

        private sealed class SyncRequestHandler : RequestHandlerBase
        {
            public SyncRequestHandler(Lazy<object> serviceInstance, MethodInfo method) : base(serviceInstance, method)
            {
            }

            public override Type? GetResponseMessageType() =>
                _method.ReturnType.IsClass ? _method.ReturnType : null;

            public override Task<object?> Invoke(object? request) =>
                Task.FromResult<object?>(_method.Invoke(_serviceInstance.Value, new object?[] { request }));
        }
    }
}