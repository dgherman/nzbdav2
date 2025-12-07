using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Utils;

public class InMemoryLogSink : ILogEventSink
{
    private readonly ConcurrentDictionary<LogEventLevel, ConcurrentQueue<LogEvent>> _logsByLevel = new();
    private const int MaxEventsPerLevel = 10000;

    public void Emit(LogEvent logEvent)
    {
        var queue = _logsByLevel.GetOrAdd(logEvent.Level, _ => new ConcurrentQueue<LogEvent>());
        
        queue.Enqueue(logEvent);
        while (queue.Count > MaxEventsPerLevel)
        {
            queue.TryDequeue(out _);
        }
    }

    public IEnumerable<LogEvent> GetLogs()
    {
        // Return reversed (newest first) usually better for UI, or client can sort
        return _logsByLevel.Values.SelectMany(x => x).ToArray();
    }
    
    public static readonly InMemoryLogSink Instance = new();
}