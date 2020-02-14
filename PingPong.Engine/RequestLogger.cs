using System;
using System.Text;
using System.Text.Json;
using NLog;
using PingPong.Engine.Messages;

namespace PingPong.Engine
{
    sealed class RequestLogger
    {
        private static ILogger _logger = LogManager.GetCurrentClassLogger();

        private readonly string _endPointName;

        public RequestLogger(string endPointName)
        {
            _endPointName = endPointName;
        }

        public void Log(RequestHeader header, object? body, bool isSending) =>
            Log("Request", header.RequestNo, header.MessageId, header.Flags.ToString(), body, isSending);

        public void Log(ResponseHeader header, object? body, bool isSending) =>
            Log("Response", header.RequestNo, header.MessageId, header.Flags.ToString(), body, isSending);

        public void Log(HostStatusMessage hostStatus, bool isSending) =>
            _logger.Trace(() => $"Host status {hostStatus.PendingProcessing}/{hostStatus.InProcessing}/{hostStatus.PendingResponsePropagation} {GetAction(isSending)} {_endPointName}");

        private void Log(string messageKind, long reqNo, int messageId, string flags, object? body, bool isSending)
        {
            _logger.Trace(() => 
            {
                var result = new StringBuilder($"{messageKind} {reqNo}:{messageId} with flags [{flags}] {GetAction(isSending)} {_endPointName}");

                if (body != null)
                {
                    result.AppendLine();
                    result.AppendLine("[" + body.GetType().FullName + "]");

                    try
                    {
                        string messageBodyJson = JsonSerializer.Serialize(body, new JsonSerializerOptions {
                            WriteIndented = true
                        });

                        result.Append(messageBodyJson);
                    }
                    catch (Exception ex)
                    {
                        result
                            .AppendLine("Failed to serialize message body to JSON:")
                            .Append(ex.ToString());
                    }
                }

                return result.ToString();
            });
        }

        private static string GetAction(bool isSending) =>
            isSending ? "sent to" : "received from";
    }
}