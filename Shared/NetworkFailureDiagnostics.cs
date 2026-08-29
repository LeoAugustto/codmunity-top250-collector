using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;

namespace CODMunityTop250Collector.Diagnostics;

public static class NetworkFailureDiagnostics
{
    private const int MaxMessageLength = 240;

    public static string Format(Exception exception, CancellationToken cancellationToken)
    {
        var fields = new List<string>();
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 3; depth++, current = current.InnerException)
        {
            var prefix = depth == 0 ? string.Empty : depth == 1 ? "inner" : $"inner{depth}";
            var typeKey = depth == 0 ? "type" : $"{prefix}Type";
            var messageKey = depth == 0 ? "message" : $"{prefix}Message";
            fields.Add($"{typeKey}={current.GetType().Name}");
            fields.Add($"{messageKey}=\"{Sanitize(current.Message)}\"");
            if (current is HttpRequestException request)
                fields.Add($"{(depth == 0 ? "httpError" : $"{prefix}HttpError")}={request.HttpRequestError}");
            if (current is SocketException socket)
            {
                fields.Add($"{(depth == 0 ? "socketError" : $"{prefix}SocketError")}={socket.SocketErrorCode}");
                fields.Add($"{(depth == 0 ? "nativeError" : $"{prefix}NativeError")}={socket.NativeErrorCode}");
            }
            if (current is AuthenticationException)
                fields.Add($"{(depth == 0 ? "authenticationType" : $"{prefix}AuthenticationType")}={current.GetType().Name}");
            if (current is TaskCanceledException)
                fields.Add($"{(depth == 0 ? "cancellation" : $"{prefix}Cancellation")}={(cancellationToken.IsCancellationRequested ? "external" : "timeout")}");
        }

        return string.Join(' ', fields);
    }

    private static string Sanitize(string? value)
    {
        var message = Regex.Replace(value ?? string.Empty, "[\\r\\n\\t]+", " ");
        message = Regex.Replace(message, @"(?i)\bauthorization\s*[:=]\s*bearer\s+[^,;\s]+", "Authorization=[redacted]");
        message = Regex.Replace(message, @"(?i)\bbearer\s+[^,;\s]+", "Bearer=[redacted]");
        message = Regex.Replace(message, @"(?i)\btoken\s*[:=]\s*[^,;\s]+", "token=[redacted]");
        message = Regex.Replace(message, "\\s+", " ").Trim();
        return message.Length <= MaxMessageLength ? message : message[..MaxMessageLength] + "...";
    }
}
