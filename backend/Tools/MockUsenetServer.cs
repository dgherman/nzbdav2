using NzbWebDAV.Utils;

namespace NzbWebDAV.Tools;

/// <summary>
/// Runs <see cref="MockNntpServer"/> as a standalone process so it can be a container of its own.
///
/// The in-process harnesses (--mock-benchmark, --test-full-nzb --mock-server) host the same server
/// inside the client, which is right for measuring the client but cannot reproduce a deployment:
/// there is no real socket, no container boundary, and no way for a separately-configured NzbDav to
/// treat it as a provider. This mode generates the NZB, writes it where the rest of the compose
/// stack can pick it up, and then just serves.
/// </summary>
public static class MockUsenetServer
{
    public static async Task RunAsync(string[] args)
    {
        var port = GetInt(args, "--port=", 1119);
        var totalSizeMb = GetInt(args, "--size=", 2048);
        var segmentSizeKb = GetInt(args, "--segment-size=", 700);
        var latencyMs = GetInt(args, "--latency=", 40);
        var jitterMs = GetInt(args, "--jitter=", 10);
        var stallRate = GetDouble(args, "--stall=", 0.0);
        var nzbOut = GetString(args, "--nzb-out=", "/mock/mock.nzb");
        var useRar = args.Contains("--rar");
        var volumes = GetInt(args, "--volumes=", 3);
        // Adds a second, much smaller video named *.sample.mkv, so the release exercises the
        // sample filter end to end instead of only in unit tests.
        var withSample = args.Contains("--with-sample");

        var segmentSize = segmentSizeKb * 1024;
        var totalSize = (long)totalSizeMb * 1024 * 1024;

        var directory = Path.GetDirectoryName(nzbOut);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  MOCK USENET PROVIDER");
        Console.WriteLine($"  Port: {port}  Latency: {latencyMs}ms (+/-{jitterMs}ms)  Stall rate: {stallRate:P1}");
        Console.WriteLine($"  Content: {totalSizeMb} MB in {segmentSizeKb} KB segments" + (useRar ? $", {volumes} rar volumes" : "") + (withSample ? ", plus a *.sample.mkv" : ""));
        Console.WriteLine($"  NZB: {nzbOut}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // The server answers each segment with the yEnc part header belonging to that segment, so it
        // has to know the geometry before it can serve anything — generate first, serve second.
        var layout = useRar
            ? await MockNzbGenerator.GenerateAsync(nzbOut, totalSize, segmentSize, useRar: true, volumes)
                .ConfigureAwait(false)
            : await MockNzbGenerator.GenerateAsync(nzbOut, totalSize, segmentSize, useRar: false,
                rarVolumeCount: 1, includeSample: withSample).ConfigureAwait(false);

        Console.WriteLine($"Generated {layout.TotalSegments} segments across {layout.Files.Count} file(s).");

        // Written last and atomically: the provisioner waits on this file, so it must not observe a
        // half-written NZB or an NZB whose server is not accepting connections yet.
        var readyMarker = Path.Combine(Path.GetDirectoryName(nzbOut) ?? ".", "ready");

        using var server = new MockNntpServer(port, latencyMs, segmentSize, jitterMs, stallRate, layout);
        server.Start();
        Console.WriteLine($"Serving on port {port}. Ctrl-C to stop.");

        await File.WriteAllTextAsync(readyMarker, layout.TotalSegments.ToString()).ConfigureAwait(false);

        var shutdown = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.TrySetResult();
        await shutdown.Task.ConfigureAwait(false);

        Console.WriteLine("Shutting down mock usenet provider.");
        try { File.Delete(readyMarker); } catch { /* best effort */ }
    }

    private static string? GetRaw(string[] args, string prefix) =>
        args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static string GetString(string[] args, string prefix, string fallback) =>
        StringUtil.EmptyToNull(GetRaw(args, prefix) ?? "") ?? fallback;

    private static int GetInt(string[] args, string prefix, int fallback) =>
        int.TryParse(GetRaw(args, prefix), out var value) ? value : fallback;

    private static double GetDouble(string[] args, string prefix, double fallback) =>
        double.TryParse(GetRaw(args, prefix), out var value) ? value : fallback;
}
