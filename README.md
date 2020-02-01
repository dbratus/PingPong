# PingPong Service Host

The simple lightweight asynchronous microservice engine. Completely experimental.

## Features

* Simple and lightweight.
* Asynchronous full-duplex protocol.
* TLS support.
* Network failure tolerance.
* Load aware load balancing.
* Stateful and stateless services support.
* Sharding.
* IoC container.
* Allows for custom serialization.

## Motivation

In some cases the standard protocols and frameworks like ASP.NET, Orleans etc. are too heavy for the task. They may force large overhead per message to provide functionality not required for a particular application. Moreover, some application protocols rely on TCP for message ordering hence utilize connection inefficiently. While a TCP channel allows for full-duplex communication, those protocols use it half-duplex. A client sends request and waits for response. No communication happens while the request is being processed and no requests are sent while the response is being delivered by the network. However, the application may need to do multiple independent requests to the same service by the same connection in a short period of time, but in the case of half-duplex communication it can't. A full-duplex protocol allows an application to send multiple requests without waiting for response. The server sends back responses as they ready, in arbitrary order. The requests which are longer to process don't block the requests which are processed faster. Moreover, less IP-packages may be required to deliver the same amount of data.

The goal is to create a robust high-performance framework for scalable distributed applications of various kinds - microservices, distributed calculations, real-time distributed systems, games etc. In the same time the platform itself should stay as simple and lightweight as possible.

## Features Overview
### Protocol

The PingPong protocol consists of two independent streams of messages - from client to server, and from server to client. Messages in the stream are prefixed by their size encoded as [base 128 varint](https://developers.google.com/protocol-buffers/docs/encoding#varints).

```
    +-----------+----+    +-----------+----+    +-----------+----+
--->| Message 3 | sz |--->| Message 2 | sz |--->| Message 1 | sz |--->
    +-----------+----+    +-----------+----+    +-----------+----+

    +----------------+    +----+----------+    +----+-----------+  
<---| sz | Message 1 |<---| sz |Message 2 |<---| sz | Message 3 |<---
    +----------------+    +----+----------+    +----+-----------+  
```

The messages going from the client are called requests, those coming back from the server are called responses. Both consist of a header optionally followed by a body. The client enumerates requests and puts the numbers to the request header. The server copies the request number into the response header. Thus, the client can match responses to requests. Headers also contain flags describing the message and the message type id.

Within the stream message types are identified by integer numbers assigned by the server. However, theses ids are valid only during a single communication session. The permanent unique message type id is the MD5 hash of the assembly qualified type name known by both the client and the server. As a connection opened, the server sends to the client the first message called 'preamble'. It contains the mapping between the permanent message type ids and their session ids.

Message headers and bodies are serialized by the serializer setup for the server and the client. PingPong supports [MessagePack](https://msgpack.org) and [JSON](https://www.json.org) serializers out of box, however custom serializers may also be provided.

### Cluster

The ensemble of servers hosting PingPong nodes is called cluster. Each PingPong node hosts one or more services, but the client knows nothing about the services - it works with the cluster as whole. The interface of a cluster is the set of request messages its services handle. To get access to some functionality the only thing a client needs to know is the request message type and the corresponding response message type. The cluster ensures that the message is delivered to the service able to handle it.

Each PingPong node has a list of known hosts i.e. the list of URLs of other PingPong nodes it can communicate. However, its not mandatory to specify the known hosts. If the list is not provided for a host, this means that no service of the host can communicate with any of the services on the other hosts, but they will still be able to receive messages from those services and clients.

### Hosts and Gateways

There can be two types of PingPong nodes - hosts and gateways. The nodes primarily hosting services are called hosts. The nodes routing requests to their known hosts are called gateways. The gateways, however, may also host services themselves. So, a gateway routs a message only if no service it hosts handles it.

There may be the following usages of the gateways:

* To route external messages from the Internet to the cluster. A gateway may expose TLS endpoint to the Internet, while the cluster itself may use unencrypted TCP connections.
* To provide external clients with the serialization they support.

### Services

PingPong services are simple message handlers which may have a state. They are implemented as CLR classes with methods conforming to the message handler convention. PingPong doesn't require an interface of a service to be explicitly defined as a CLR interface. 

A service object is instantiated on demand, as the first message addressed to it is received, and lives until the host is stopped.

Services are instantiated by the host's IoC container, so the host interfaces can be injected into the constructor. The host interfaces provide a service with the functionality it may need from the host like reading the configuration or obtaining the host's instance id. A service can also add its custom types to the container, so they can be configured and injected by the container.

## Usage

### Making a service

Normally services and messages are defined in separate assemblies. 

A messages assembly should contain only message definitions and should not have any external references except probably a custom serializer. This assembly is the contract between the services and the clients.

A services assembly contain service implementations and everything else required. It may need to reference the host interfaces PingPong.HostInterfaces assembly if it uses them.

Neither messages assembly, nor services assembly should reference PingPong.Engine.

#### Stateless service

First of all we need to define a request and a response message. PingPong uses MessagePack serialization by default, so we define them as MessagePack serializable.

```C#
[MessagePackObject]
public class SquareRequest
{
    [Key(0)]
    public int Value { get; set; }
}

[MessagePackObject]
public class SquareResponse
{
    [Key(0)]
    public int Result { get; set; }
}
```

Then we need to define a service.

```C#
public class SquareService
{
    public async Task<SquareResponse> Square(SquareRequest request) =>
        await Task.Run(() => new SquareResponse {
            Result = request.Value * request.Value
        });
}
```

The minimum required is to define a single method accepting the request message as the only parameter. The response message may be returned as the return value or as the task result if the method is async (in this example the method might have been simpler, it is made async only for demonstration).

#### Stateful service

Making a stateful service is more tricky. The first difficulty is the fact that the state of a service may be accessed by concurrent tasks. PingPong does not serialize access to a service instance. The second difficulty arises if there are more than one instance of a stateful service in the cluster. In that case a client needs to know, which host it should send messages to.

Below is an example of a stateful service.

```C#
public class SumService
{
    private readonly ISession _session;
    private readonly ConcurrentDictionary<int, int> _counters =
        new ConcurrentDictionary<int, int>();

    private int _nextCountertId = 0;

    public SumService(ISession session)
    {
        _session = session;
    }

    public InitSumResponse InitSum(InitSumRequest request)
    {
        int counterId = Interlocked.Increment(ref _nextCountertId);
        _counters.AddOrUpdate(counterId, id => 0, (id, prev) => 0);
        return new InitSumResponse { 
            CounterId = counterId, 
            InstanceId = _session.InstanceId 
        };
    }

    public void Add(AddRequest request)
    {
        _counters.AddOrUpdate(request.CounterId, id => 0, (id, prev) => prev + request.Value);
    }

    public GetSumResponse GetSum(GetSumRequest request)
    {
        int sum = _counters.GetOrAdd(request.CounterId, id => -1);
        return new GetSumResponse { Result = sum };
    }
}
```

The idea is that a client first sends `InitSumRequest` to initialize the counter and get its id. Along with the id it gets an id of the host which processed the request (instance id). The client then uses the instance id to direct further messages to the host containing the initialized counter and puts the counter id to the message to specify the counter for the service.

### Making a client

To send messages to a cluster a client needs to reference PingPong.Engine and the message assembly containing those message types.

First of all an instance of ClusterConnection needs to be created and connected.

``` C#
var connectionSettings = new ClusterConnectionSettings {
    ...
};
var uris = new string[] {
    "tcp://hostname:10000",
    ...
};

var connection = new ClusterConnection(uris, connectionSettings);

await connection.Connect();
```

Then the connection can be used to send messages via `Send` or `SendAsync` method overloads. `Send` methods are synchronous and use callbacks to receive results. `SendAsync` are their async variants.

The full call patterns for these methods are following:

```C#
connection.Send<RequestMessage, ResponseMessage>(
    new DeliveryOptions { InstanceId = instanceId }, 
    request, 
    (ResponseMessage response, RquestResult result) => {
        ...
    }
);
```

```C#
var response = 
    await connection.Send<RequestMessage, ResponseMessage>(new DeliveryOptions { InstanceId = instanceId }, request);
```

Almost all parts of the call may be omitted if not required.

`Send` methods pass request result as a parameter to callback while SendAsync versions throw `RequestResultException` if request result is not `RequestResult.OK`.

#### Calling a stateless service

```C#
connection.Send(new SquareRequest { Value = arg }, (SquareResponse? response, RequestResult result) => {
    if (result != RequestResult.OK)
    {
        // The message has not been processed properly.
        // Handler error.
        ...
    }

    ...
});
```

```C#
var response = 
    await connection.SendAsync<SquareRequest, SquareResponse>(new SquareRequest { Value = i });
```

#### Calling a stateful service

```C#
connection.Send<InitSumRequest, InitSumResponse>((response, result) => {
    if (result != RequestResult.OK)
    {
        // The message has not been processed properly.
        // Handler error.
        ...
        return;
    }

    int instanceId = response?.InstanceId ?? -1;
    int counterId = response?.CounterId ?? -1;

    connection.Send(new DeliveryOptions { InstanceId = instanceId }, new AddRequest { CounterId = counterId, Value = 1 });
});
```

```C#
var initResponse = 
    await connection.SendAsync<InitSumRequest, InitSumResponse>();

int instanceId = initResponse?.InstanceId ?? -1;
int counterId = initResponse?.CounterId ?? -1;

await connection.SendAsync(new DeliveryOptions { InstanceId = instanceId }, new AddRequest { CounterId = counterId, Value = i });
```

### Service communication using events

Sometimes it is required for a service to notify other services on some events. For example, a stateful service may need to notify other services on its state changes so that the state could be replicated. PingPong events address this.

Normal messages have only one destination, so they cannot be used for broadcast notification. Events are also messages, but with different dispatching mechanism. While a message is delivered to either a random handler, or to a handler at particular host, an event is broadcasted to all who subscribed. A service considered subscribed if it has a handler for the event - a handler like for ordinary message.

An event is sent via `Publish` or `PublishAsync` method of `ICluster`.

```C#
public class PublisherService
{
    private readonly ICluster _cluster;

    public PublisherService(ICluster cluster)
    {
        _cluster = cluster;
    }

    public async Task Publish(PublishRequest request)
    {
        await _cluster.PublishAsync(new PublisherEvent {
            Message = request.Message
        });
    }
}
```

`Publish` method returns as soon as the event is scheduled for delivery and has no result, while `PublishAsync` can be awaited and completes when either all addressees confirmed delivery or any of them responded with error. In the later case `PublishAsync` throws `RequestResultException`.

Publishing without confirmation doesn't guarantee delivery. For messages critical for system's consistency `PublishAsync` should always be used.

To subscribe to an event a service needs to implement a message handler just like for an ordinary message. An event handler may also be async, but it can't return result.

```C#
public class SubscriberService
{
    public void ReceiveEvent(PublisherEvent ev)
    {
        ...
    }
}
```

### Streaming data from a service

A client may need to receive a large amount of data from a server. Moreover, this data may be generated by the server in real-time and not exist all at once. A client might request pieces of data one by one sending messages and receiving responses, but it would generate additional unnecessary traffic from the client to the server. The better solution is to have a persistent channel from the server to the client, so that the server could send the data chunks as they ready.

A channel can be opened by a client by calling `OpenChannel` or `OpenChannelAsync` method.

```C#
ChannelReader<(StreamingResponse?, RequestResult)> channel = 
    connection.OpenChannelAsync<StreamingRequest, StreamingResponse>(
        new StreamingRequest { Count = responsesCount }
    );
```

`ChannelReader` is the type from `System.Threading.Channels` package (see [An Introduction to System.Threading.Channels](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/)).

To receive data the client needs to read the channel until it is closed.

Callback versions are also exist. Methods are called `OpenChannel` and have the same parameters as the `Send` methods. Callbacks are called for each received response. `null` response indicates the end of the channel.

On the server side message handler method is called repeatedly until returns `null`. This indicates the end of the channel. After receiving `null` the host closes the channel.

Order of responses in a channel is preserved.

```C#
public class StreamingService
{
    private static ConcurrentDictionary<(int, int), int> _counters = 
        new ConcurrentDictionary<(int, int), int>();

    private readonly ISession _session;

    public StreamingService(ISession session)
    {
        _session = session;
    }

    public StreamingResponse? Stream(StreamingRequest request)
    {
        var uniqueRequestId = (_session.ConnectionId, _session.RequestNo);
        int count = _counters.GetOrAdd(uniqueRequestId, reqNo => Math.Max(request.Count, 0));

        if (count < 0)
        {
            _counters.TryRemove(uniqueRequestId, out count);
            return null;
        }

        _counters.TryUpdate(uniqueRequestId, count - 1, count);

        return new StreamingResponse { Value = count };
    }
}
```

### Custom serialization

PingPong protocol is serialization agnostic. Out of box it supports MessagePack and JSON serialization, however other serializers can be provided for a particular cluster configuration.

PingPong serializer is a class implementing `PingPong.Engine.ISerializer` interface. 

```C#
public interface ISerializer
{
    void Serialize(IBufferWriter<byte> buffer, object message);
    object Deserialize(Type type, ReadOnlyMemory<byte> memory);
}
```

Below is the JSON serializer implementation.

```C#
public sealed class SerializerJson : ISerializer
{
    public object Deserialize(Type type, ReadOnlyMemory<byte> memory) =>
        JsonSerializer.Deserialize(memory.Span, type);

    public void Serialize(IBufferWriter<byte> buffer, object message) =>
        JsonSerializer.Serialize(new Utf8JsonWriter(buffer), message, message.GetType());
}
```

On the client side the serializer instance can be passed to a cluster connection in `ClusterConnectionSettings`.

```C#
var connection = new ClusterConnection(uris, new ClusterConnectionSettings {
    Serializer = new SerializerJson()
});
```

On the server the serializer type is specified in the host configuration by its assembly qualified name.

```JSON
{
    "MessageAssemblies": [
        "/Path/To/MyAssembly.dll"
    ],

    "Serializer": "MyAssembly, MySerializerType",

    "ClusterConnectionSettings": {
        "Serializer": "MyAssembly, MySerializerType"
    }
}
```

The serializer in the root section is the serializer used by the host itself. The one in the "ClusterConnectionSettings" is the serializer used for cluster connections. These serializers may be different. For example, a gateway may accept JSON serialized messages, while the rest of the cluster may use MessagePack.

It is important to note that the assembly containing the serializer must be loaded into the host process. This can be achieved by specifying the assembly in the "MessageAssemblies" list.

### Configuring a Host

Hosts are configured via JSON configuration files. A path to a configuration file is specified as the command line argument of PingPong.Server.

Below is an example configuration file.

```JSON
{
    "Port": 10000,
    "InstanceId": 0,

    "NLogConfigFile": "NLog.config.xml",

    "MessageAssemblies": [
        "/Path/To/Messages.dll"
    ],

    "ServiceAssemblies": [
        "/Path/To/Services.dll"
    ],

    "KnownHosts": [
        "tcp://localhost:10000",
        "tcp://localhost:10001"
    ],

    "ServiceConfigs": {
        "PingPong.Services.ConfigurableService": {
            "ConfigValue": "Value from config"
        }
    }
}
```

For more, see [ServiceHostConfig.cs](PingPong.Engine/ServiceHostConfig.cs).

#### Configuring services

PingPong host provides means to configure services via the configuration file of the host. "ServiceConfigs" section contains subsections named by the service type full names. A service can get access to the section by deserializing it to an object via `HostInterfaces.IConfig` interface. `GetConfigForService` method accepts two type parameters - the type of the service (it determines the name of the section to get) and the type to deserialize the section to.

```C#
using PingPong.HostInterfaces;
using PingPong.Messages;

namespace PingPong.Services
{
    public class ConfigurableService
    {
        private readonly ConfigSection _config;

        public ConfigurableService(IConfig config)
        {
            _config = config.GetConfigForService<ConfigurableService, ConfigSection>();
        }

        public GetConfigValueResponse GetConfigValue(GetConfigValueRequest request) =>
            new GetConfigValueResponse {
                Value = _config.ConfigValue
            };

        public class ConfigSection
        {
            public string ConfigValue { get; set; } = "";
        }
    }
}
```

#### TLS Support

PingPong hosts support secure connections over TLS. To configure a host to use TLS the section "TlsSettings" needs to be present in the host config. The section contains the path to the server certificate file (.p12 file with the certificate and the private key) and the path to the text file containing password to decrypt .p12 file. 

It is also possible to allow the host to ignore certificate errors in a test environment. This is done by specifying `"AllowSelfSignedCertificates": true`.

```JSON
{
    "TlsSettings": {
        "CertificateFile": "../Certificates/Test.p12",
        "PasswordFile": "../Certificates/Password",
        "AllowSelfSignedCertificates": true
    }
}
```

To connect to a TLS host, "tls" must be specified as the scheme in the URL instead of "tcp".