using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreMultipartFile(
    DavItem davMultipartFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager
) : BaseStoreStreamFile
{
    public DavItem DavItem => davMultipartFile;
    public override string Name => davMultipartFile.Name;
    public override string UniqueKey => davMultipartFile.Id.ToString();
    public override long FileSize => davMultipartFile.FileSize!.Value;
    public override DateTime CreatedAt => davMultipartFile.CreatedAt;

    public override async Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davMultipartFile;

        // create streaming usage context with normalized AffinityKey
        var rawAffinityKey = Path.GetFileName(Path.GetDirectoryName(davMultipartFile.Path));
        var normalizedAffinityKey = FilenameNormalizer.NormalizeName(rawAffinityKey);

        Serilog.Log.Debug("[DatabaseStoreMultipartFile] AffinityKey: Raw='{Raw}' Normalized='{Normalized}' for file '{File}'",
            rawAffinityKey, normalizedAffinityKey, davMultipartFile.Name);

        var usageContext = new ConnectionUsageContext(
            ConnectionUsageType.Streaming,
            new ConnectionUsageDetails
            {
                Text = davMultipartFile.Path,
                JobName = davMultipartFile.Name,
                AffinityKey = normalizedAffinityKey,
                DavItemId = davMultipartFile.Id,
                FileDate = davMultipartFile.ReleaseDate,
                FileSize = davMultipartFile.FileSize  // Total file size for UI display
            }
        );

        // return the stream
        var id = davMultipartFile.Id;
        var multipartFile = await dbClient.Ctx.MultipartFiles.Where(x => x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (multipartFile is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");

        // Lazily compute + persist exact decoded per-segment sizes so NzbFileStream can seek precisely.
        // Covers items created before this field existed and any aggregator that did not populate them.
        if (SegmentSizePopulation.NeedsPopulation(multipartFile.Metadata))
        {
            var changed = false;
            foreach (var part in multipartFile.Metadata.FileParts)
            {
                if (part.SegmentSizes != null && part.SegmentSizes.Length == part.SegmentIds.Length) continue;
                if (part.SegmentIds.Length == 0) continue;
                try
                {
                    var sizes = await usenetClient.AnalyzeNzbAsync(
                        part.SegmentIds, configManager.GetTotalStreamingConnections(),
                        progress: null, ct, useSmartAnalysis: true).ConfigureAwait(false);

                    if (SegmentSizePopulation.IsValidForPart(part, sizes)) { part.SegmentSizes = sizes; changed = true; }
                    else Serilog.Log.Warning("[DatabaseStoreMultipartFile] Computed sizes for '{File}' did not sum to part size; will interpolate.", davMultipartFile.Name);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[DatabaseStoreMultipartFile] Failed to compute segment sizes for '{File}'; will interpolate.", davMultipartFile.Name);
                }
            }

            if (changed)
            {
                dbClient.Ctx.MultipartFiles.Update(multipartFile);
                dbClient.Ctx.Entry(multipartFile).Property(x => x.Metadata).IsModified = true;
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                Serilog.Log.Information("[DatabaseStoreMultipartFile] Persisted segment sizes for '{File}'.", davMultipartFile.Name);
            }
        }

        // Honor the consumer's HTTP Range end byte (set by GetAndHeadHandlerPatch) so archive
        // part prefetch is bounded to the requested segment instead of reading ahead to EOF.
        // Skipped for AES-encrypted archives, which must be decoded sequentially from the start.
        long? requestedEndByte = null;
        if (multipartFile.Metadata.AesParams == null &&
            httpContext.Items.TryGetValue("RequestedRangeEnd", out var endObj) &&
            endObj is long endByte)
        {
            requestedEndByte = endByte;
        }

        var packedStream = new DavMultipartFileStream(
            multipartFile.Metadata.FileParts,
            usenetClient,
            configManager.GetTotalStreamingConnections(),
            usageContext,
            requestedEndByte: requestedEndByte
        );
        Stream finalStream = multipartFile.Metadata.AesParams != null
            ? new AesDecoderStream(packedStream, multipartFile.Metadata.AesParams)
            : packedStream;
            
        return new RarDeobfuscationStream(finalStream, multipartFile.Metadata.ObfuscationKey);
    }
}