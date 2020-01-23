using System;
using System.Text;
using NLog;

namespace PingPong.Engine
{
    sealed class BinaryMessageLogger
    {
        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();
        private readonly string _endPointName;
        private readonly string _action;

        public BinaryMessageLogger(string endPointName, bool isSending)
        {
            _endPointName = endPointName;
            _action = isSending ? "Sent to" : "Received from";
        }

        public void Log(ReadOnlyMemory<byte> messageData)
        {
            _logger.Trace(() => {
                var result = new StringBuilder();

                result
                    .Append(_action)
                    .Append(" \"")
                    .Append(_endPointName)
                    .Append("\" ")
                    .Append(messageData.Length)
                    .Append(" bytes");

                const int bytesPerRow = 16;

                for (int rowOffset = 0; rowOffset < messageData.Length; rowOffset += bytesPerRow)
                {
                    result.AppendLine();

                    int offsetLim = Math.Min(rowOffset + bytesPerRow, messageData.Length);

                    for (int offset = rowOffset; offset < offsetLim; ++offset)
                    {
                        byte b = messageData.Span[offset];

                        result
                            .Append(b.ToString("X2"))
                            .Append(" ");
                    }

                    int paddingLen = rowOffset + bytesPerRow - offsetLim;
                    for (int i = 0; i < paddingLen; ++i)
                        result.Append("   ");

                    result.Append("|");

                    for (int offset = rowOffset; offset < offsetLim; ++offset)
                    {
                        char c = (char)messageData.Span[offset];
                        string s = 
                            char.IsLetterOrDigit(c) || 
                            char.IsPunctuation(c) ?
                            c.ToString() : 
                                char.IsWhiteSpace(c) ?
                                    " " :
                                    "?";

                        result.Append(s);
                    }
                }

                return result.ToString();
            });
        }
    }
}