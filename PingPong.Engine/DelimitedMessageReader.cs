using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;

namespace PingPong.Engine
{
    sealed class DelimitedMessageReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _messageSizeBuffer = new byte[sizeof(int)];
        private readonly ISerializer _serializer;
        
        public DelimitedMessageReader(Stream stream, ISerializer serializer)
        {
            _stream = stream;
            _serializer = serializer;
        }

        public void Dispose() =>
            _stream.Dispose();

        public async Task<T> Read<T>() =>
            (T) await Read(typeof(T));

        public async Task<object> Read(Type type)
        {
            await _stream.ReadAsync(_messageSizeBuffer);

            int messageSize = BinaryPrimitives.ReadInt32LittleEndian(_messageSizeBuffer);

            using IMemoryOwner<byte> messageBuffer = MemoryPool<byte>.Shared.Rent(messageSize);
            Memory<byte> messageMemory = messageBuffer.Memory.Slice(0, messageSize);

            await _stream.ReadAsync(messageMemory);

            return _serializer.Deserialize(type, messageMemory);
        }
    }
}