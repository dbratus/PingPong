using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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
                        Type requestMessageType = newHandler.RequestMessageType;

                        if (_messageMap.ContainsType(requestMessageType))
                        {
                            Console.WriteLine($"Message ${requestMessageType.FullName} handled by more then one handler.");
                            continue;
                        }

                        Type? responseMessageType = newHandler.ResponseMessageType;

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

            public abstract Task<object?> Invoke(object? request);
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

            public override Task<object?> Invoke(object? request)
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

            public override Task<object?> Invoke(object? request)
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

            public override Task<object?> Invoke(object? request) =>
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

            public override Task<object?> Invoke(object? request)
            {
                _serviceMethod(_serviceInstance.Value, request);
                return Task.FromResult<object?>(null);
            }
        }
    }
}