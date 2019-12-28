using System;
using System.Buffers;

namespace PingPong.Engine
{
    public interface ISerializer
    {
        void Serialize(IBufferWriter<byte> buffer, object message);
        object Deserialize(Type type, ReadOnlyMemory<byte> memory);
    }
}