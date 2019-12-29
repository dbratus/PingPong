using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;

namespace PingPong.Engine
{
    sealed class DelimitedMessageReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly Memory<byte> _messageSizeBuffer = 
            new Memory<byte>(new byte[1]);
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
            int messageSize = await ReadMessageSize();

            using IMemoryOwner<byte> messageBuffer = MemoryPool<byte>.Shared.Rent(messageSize);
            Memory<byte> messageMemory = messageBuffer.Memory.Slice(0, messageSize);

            int totalBytesRead = 0;

            while (totalBytesRead < messageSize)
            {
                Memory<byte> targetMemory = messageMemory.Slice(totalBytesRead);

                int bytesRead = await _stream.ReadAsync(targetMemory);
                if (bytesRead == 0)
                    throw new EndOfStreamException();

                totalBytesRead += bytesRead;
            }

            return _serializer.Deserialize(type, messageMemory);
        }

        private async Task<int> ReadMessageSize()
        {
            // Message size is written as protobuf base 128 varint.
            // https://developers.google.com/protocol-buffers/docs/encoding#varints

            int size = 0;
            int offset = 0;
            const int highBit = 0x80;

            while (true)
            {
                int bytesRead = await _stream.ReadAsync(_messageSizeBuffer);
                if (bytesRead == 0)
                    throw new EndOfStreamException();

                int nextByte = _messageSizeBuffer.Span[0];

                if ((nextByte & highBit) == 0)
                {
                    size |= nextByte << offset;
                    break;
                }
                else
                {
                    size |= (nextByte & ~highBit) << offset;
                    offset += 7;
                }
            }

            return size;
        }
    }
}