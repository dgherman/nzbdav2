using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Utils;

public class InMemoryLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    private const int MaxEvents = 50000;

    public void Emit(LogEvent logEvent)
    {
        _events.Enqueue(logEvent);
        while (_events.Count > MaxEvents)
        {
            _events.TryDequeue(out _);
        }
    }

    public IEnumerable<LogEvent> GetLogs()
    {
        // Return reversed (newest first) usually better for UI, or client can sort
        return _events.ToArray();
    }
    
    public static readonly InMemoryLogSink Instance = new();
}