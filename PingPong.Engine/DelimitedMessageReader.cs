using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using NLog;

namespace PingPong.Engine
{
    sealed class DelimitedMessageReader : IDisposable
    {
        private const int MaxMessageSize = 1024 * 1024 * 4;

        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly Stream _stream;
        private readonly Memory<byte> _messageSizeBuffer = 
            new Memory<byte>(new byte[1]);
        private readonly ISerializer _serializer;
        private readonly BinaryMessageLogger _messageLogger;
        
        public DelimitedMessageReader(string endPointName, Stream stream, ISerializer serializer)
        {
            _stream = stream;
            _serializer = serializer;
            _messageLogger = new BinaryMessageLogger(endPointName, false);
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

            _messageLogger.Log(_messageSizeBuffer);
            _messageLogger.Log(messageMemory);

            return _serializer.Deserialize(type, messageMemory);
        }

        private async Task<int> ReadMessageSize()
        {
            // Message size is written as protobuf base 128 varint.
            // https://developers.google.com/protocol-buffers/docs/encoding#varints

            int size = 0;
            int offset = 0;
            const int highBit = 0x80;
            const int maxOffset = (64 / 7) * 7;

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

                    if (offset > maxOffset)
                        throw new ProtocolException("Message size has invalid format.");
                }
            }

            if (size > MaxMessageSize)
                throw new ProtocolException($"Maximum message size {MaxMessageSize} exceeded.");

            return size;
        }
    }
}