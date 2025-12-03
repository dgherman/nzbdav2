using NzbWebDAV.Api.Controllers;

namespace NzbWebDAV.Api.Controllers.GetLogs;

public class GetLogsResponse : BaseApiResponse
{
    public required List<LogEntry> Logs { get; init; }

    public class LogEntry
    {
        public required string Timestamp { get; init; }
        public required string Level { get; init; }
        public required string Message { get; init; }
    }
}