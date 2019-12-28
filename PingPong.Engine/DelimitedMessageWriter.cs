using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using MessagePack;

namespace PingPong.Engine
{
    sealed class DelimitedMessageWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly ArrayBufferWriter<byte> _buffer = new ArrayBufferWriter<byte>();
        private readonly ISerializer _serializer;

        public DelimitedMessageWriter(Stream stream, ISerializer serializer)
        {
            _stream = stream;
            _serializer = serializer;
        }

        public void Dispose() =>
            _stream.Dispose();

        public async Task Write(object message)
        {
            _serializer.Serialize(_buffer, message);

            ReadOnlyMemory<byte> messageMemory = _buffer.WrittenMemory;
            ReadOnlyMemory<byte> messageSizeMemory = WriteMessageSize();

            await _stream.WriteAsync(messageSizeMemory);
            await _stream.WriteAsync(messageMemory);

            _buffer.Clear();

            ReadOnlyMemory<byte> WriteMessageSize()
            {
                Span<byte> sizeSpan = stackalloc byte[sizeof(int)];
                int messageSize = _buffer.WrittenCount;
                BinaryPrimitives.WriteInt32LittleEndian(sizeSpan, messageSize);
                _buffer.Write(sizeSpan);
                return _buffer.WrittenMemory.Slice(messageSize, sizeof(int));
            }
        }
    }
}