using System;
using System.Buffers;
using MessagePack;

namespace PingPong.Engine
{
    public sealed class SerializerMessagePack : ISerializer
    {
        public object Deserialize(Type type, ReadOnlyMemory<byte> memory) =>
            MessagePackSerializer.Deserialize(type, memory);

        public void Serialize(IBufferWriter<byte> buffer, object message) =>
            MessagePackSerializer.Serialize(buffer, message);
    }
}