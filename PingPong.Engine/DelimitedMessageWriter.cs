using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using MessagePack;
using NLog;

namespace PingPong.Engine
{
    sealed class DelimitedMessageWriter : IDisposable
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

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
            (int messageSize, ReadOnlyMemory<byte> messageSizeMemory) = WriteMessageSize();

            await _stream.WriteAsync(messageSizeMemory);
            await _stream.WriteAsync(messageMemory);

            _logger.Trace("Message written {0} '{1}' {2}", messageSize, message.GetType().AssemblyQualifiedName, _serializer.GetType().Name);

            _buffer.Clear();
        }

        private (int, ReadOnlyMemory<byte>) WriteMessageSize()
        {
            // Message size is written as protobuf base 128 varint.
            // https://developers.google.com/protocol-buffers/docs/encoding#varints

            int messageSize = _buffer.WrittenCount;
            int varint = messageSize;

            const int maxVarintSize = 5;
            Span<byte> buff = stackalloc byte[maxVarintSize];

            const int base128Mask = 0x7F;
            const int highBit = 0x80;
            int varintLen = 0;

            while (true)
            {
                buff[varintLen] = (byte)(varint & base128Mask);
                varint >>= 7;

                if (varint == 0)
                    break;

                buff[varintLen] |= highBit;
                ++varintLen;
            }

            ++varintLen;

            _buffer.Write(buff.Slice(0, varintLen));
            
            return (messageSize, _buffer.WrittenMemory.Slice(messageSize, varintLen));
        }
    }
}