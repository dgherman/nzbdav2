using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public class MediaAnalysisService(
    IServiceScopeFactory scopeFactory,
    ConfigManager configManager
)
{
    public async Task AnalyzeMediaAsync(Guid davItemId, CancellationToken ct = default)
    {
        // 1. Get Item
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var item = await dbContext.Items.FindAsync([davItemId], ct);
        if (item == null) return;

        // 2. Construct Path
        var mountDir = configManager.GetRcloneMountDir();
        // Remove leading slash from Item.Path to ensure Path.Combine works correctly
        var relPath = item.Path.TrimStart('/');
        var fullPath = Path.Combine(mountDir, relPath);

        // Check file existence (optional, but good for debugging)
        // Note: File.Exists might hang if Fuse is stuck, so maybe skip or use with timeout?
        // We'll trust ffprobe to fail if it can't read.

        // 3. Run ffprobe
        Log.Information("[MediaAnalysis] Running ffprobe on {Path}", fullPath);
        var result = await RunFfprobeAsync(fullPath, ct);
        
        // 4. Update DB
        if (string.IsNullOrWhiteSpace(result))
        {
             Log.Warning("[MediaAnalysis] ffprobe failed or returned empty result for {Path}", fullPath);
             item.MediaInfo = "{\"error\": \"ffprobe failed (file may be corrupt or incomplete)\", \"streams\": []}";
             item.IsCorrupted = true;
             item.CorruptionReason = "Media analysis (ffprobe) failed - possible corrupt file.";
        }
        else if (result.Contains("\"error\":"))
        {
             Log.Warning("[MediaAnalysis] ffprobe reported error for {Path}: {Result}", fullPath, result);
             item.MediaInfo = result;
             item.IsCorrupted = true;
             item.CorruptionReason = "Media analysis reported stream errors.";
        }
        else
        {
             item.MediaInfo = result;
             item.IsCorrupted = false;
             item.CorruptionReason = null;
        }

        await dbContext.SaveChangesAsync(ct);
        Log.Information("[MediaAnalysis] Media analysis complete for {Name}", item.Name);
    }

    private async Task<string?> RunFfprobeAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var start = Stopwatch.StartNew();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Read output
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            // Wait for exit with timeout (e.g. 60 seconds)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("[MediaAnalysis] ffprobe timed out after 60s for {Path}", filePath);
                process.Kill();
                return null;
            }

            var output = await outputTask;
            var error = await errorTask;
            
            start.Stop();
            Log.Debug("[MediaAnalysis] ffprobe took {Duration}ms", start.ElapsedMilliseconds);

            if (process.ExitCode != 0)
            {
                Log.Warning("[MediaAnalysis] ffprobe exited with code {Code}: {Error}", process.ExitCode, error);
                return null;
            }

            return output;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MediaAnalysis] Failed to run ffprobe");
            return null;
        }
    }
}
