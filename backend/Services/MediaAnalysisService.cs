using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public enum MediaAnalysisResult
{
    Success,
    Failed,
    Timeout
}

public class MediaAnalysisService(
    IServiceScopeFactory scopeFactory,
    ConfigManager configManager
)
{
    public async Task<MediaAnalysisResult> AnalyzeMediaAsync(Guid davItemId, CancellationToken ct = default)
    {
        // 1. Get Item
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var item = await dbContext.Items.FindAsync([davItemId], ct);
        if (item == null) return MediaAnalysisResult.Failed;

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
        var (result, timedOut) = await RunFfprobeAsync(fullPath, ct);

        // 4. Update DB
        MediaAnalysisResult analysisResult;
        if (timedOut)
        {
             Log.Warning("[MediaAnalysis] ffprobe timed out for {Path}", fullPath);
             // Don't save error to MediaInfo on timeout - leave it null for retry
             analysisResult = MediaAnalysisResult.Timeout;
        }
        else if (string.IsNullOrWhiteSpace(result))
        {
             Log.Warning("[MediaAnalysis] ffprobe failed or returned empty result for {Path}", fullPath);
             item.MediaInfo = "{\"error\": \"ffprobe failed (file may be corrupt or incomplete)\", \"streams\": []}";
             item.IsCorrupted = true;
             item.CorruptionReason = "Media analysis (ffprobe) failed - possible corrupt file.";
             analysisResult = MediaAnalysisResult.Failed;
        }
        else if (result.Contains("\"error\":"))
        {
             Log.Warning("[MediaAnalysis] ffprobe reported error for {Path}: {Result}", fullPath, result);
             item.MediaInfo = result;
             item.IsCorrupted = true;
             item.CorruptionReason = "Media analysis reported stream errors.";
             analysisResult = MediaAnalysisResult.Failed;
        }
        else
        {
             item.MediaInfo = result;
             item.IsCorrupted = false;
             item.CorruptionReason = null;
             analysisResult = MediaAnalysisResult.Success;
        }

        if (analysisResult != MediaAnalysisResult.Timeout)
        {
            await dbContext.SaveChangesAsync(ct);
        }

        Log.Information("[MediaAnalysis] Media analysis complete for {Name}. Result: {Result}", item.Name, analysisResult);
        return analysisResult;
    }

    /// <summary>
    /// Runs ffprobe on the given file path.
    /// </summary>
    /// <returns>Tuple of (output, timedOut). If timedOut is true, output will be null.</returns>
    private async Task<(string? output, bool timedOut)> RunFfprobeAsync(string filePath, CancellationToken ct)
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

            // Wait for exit with timeout (2 minutes)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("[MediaAnalysis] ffprobe timed out after 2 minutes for {Path}", filePath);
                process.Kill();
                return (null, timedOut: true);
            }

            var output = await outputTask;
            var error = await errorTask;
            
            start.Stop();
            Log.Debug("[MediaAnalysis] ffprobe took {Duration}ms", start.ElapsedMilliseconds);

            if (process.ExitCode != 0)
            {
                Log.Warning("[MediaAnalysis] ffprobe exited with code {Code}: {Error}", process.ExitCode, error);
                return (null, timedOut: false);
            }

            return (output, timedOut: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MediaAnalysis] Failed to run ffprobe");
            return (null, timedOut: false);
        }
    }
}
