using System;
using System.Buffers;
using System.Text.Json;

namespace PingPong.Engine
{
    public sealed class SerializerJson : ISerializer
    {
        public object Deserialize(Type type, ReadOnlyMemory<byte> memory) =>
            JsonSerializer.Deserialize(memory.Span, type);

        public void Serialize(IBufferWriter<byte> buffer, object message) =>
            JsonSerializer.Serialize(new Utf8JsonWriter(buffer), message, message.GetType());
    }
}