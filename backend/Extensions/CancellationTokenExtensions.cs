using System.Collections.Concurrent;

namespace NzbWebDAV.Extensions;

public static class CancellationTokenExtensions
{
    private static readonly ConcurrentDictionary<LookupKey, object?> Context = new();

    public static CancellationTokenScopedContext SetScopedContext<T>(this CancellationToken ct, T? value)
    {
        var lookupKey = new LookupKey() { CancellationToken = ct, Type = typeof(T) };
        Context[lookupKey] = value;

        // Disabled noisy logging - only log if context count is very high
        // if (typeof(T).Name.Contains("ConnectionUsageContext"))
        // {
        //     Serilog.Log.Debug("[CancellationTokenContext] Set context: {ContextType} = {ContextValue}. Total contexts: {ContextCount}",
        //         typeof(T).Name, value?.ToString() ?? "null", Context.Count);
        // }

        return new CancellationTokenScopedContext(lookupKey, value, DateTime.UtcNow);
    }

    public static T? GetContext<T>(this CancellationToken ct)
    {
        var lookupKey = new LookupKey() { CancellationToken = ct, Type = typeof(T) };
        return Context.TryGetValue(lookupKey, out var result) && result is T context ? context : default;
    }

    public class CancellationTokenScopedContext(LookupKey lookupKey, object? value, DateTime createdAt) : IDisposable
    {
        public void Dispose()
        {
            var heldDuration = DateTime.UtcNow - createdAt;
            var removed = Context.Remove(lookupKey, out _);

            if (lookupKey.Type.Name.Contains("ConnectionUsageContext"))
            {
                if (!removed)
                {
                    Serilog.Log.Warning("[CancellationTokenContext] FAILED to remove context: {ContextType} = {ContextValue}. Context was already removed or never existed!",
                        lookupKey.Type.Name, value?.ToString() ?? "null");
                }
                else if (heldDuration.TotalMinutes > 2)
                {
                    Serilog.Log.Warning("[CancellationTokenContext] Removed context after {HeldMinutes:F1} minutes: {ContextType} = {ContextValue}. Remaining contexts: {ContextCount}",
                        heldDuration.TotalMinutes, lookupKey.Type.Name, value?.ToString() ?? "null", Context.Count);
                }
                // Disabled noisy logging for normal context removal
                // else
                // {
                //     Serilog.Log.Debug("[CancellationTokenContext] Removed context after {HeldSeconds:F1}s: {ContextType} = {ContextValue}. Remaining contexts: {ContextCount}",
                //         heldDuration.TotalSeconds, lookupKey.Type.Name, value?.ToString() ?? "null", Context.Count);
                // }
            }
        }
    }

    public record struct LookupKey
    {
        public CancellationToken CancellationToken;
        public Type Type;
    }
}