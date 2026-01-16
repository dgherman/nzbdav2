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

public class DatabaseStoreRarFile(
    DavItem davRarFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager
) : BaseStoreStreamFile
{
    public DavItem DavItem => davRarFile;
    public override string Name => davRarFile.Name;
    public override string UniqueKey => davRarFile.Id.ToString();
    public override long FileSize => davRarFile.FileSize!.Value;
    public override DateTime CreatedAt => davRarFile.CreatedAt;

    public override async Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davRarFile;

        // create streaming usage context with normalized AffinityKey
        var rawAffinityKey = Path.GetFileName(Path.GetDirectoryName(davRarFile.Path));
        var normalizedAffinityKey = FilenameNormalizer.NormalizeName(rawAffinityKey);

        Serilog.Log.Debug("[DatabaseStoreRarFile] AffinityKey: Raw='{Raw}' Normalized='{Normalized}' for file '{File}'",
            rawAffinityKey, normalizedAffinityKey, davRarFile.Name);

        var usageContext = new ConnectionUsageContext(
            ConnectionUsageType.Streaming,
            new ConnectionUsageDetails
            {
                Text = davRarFile.Path,
                JobName = davRarFile.Name,
                AffinityKey = normalizedAffinityKey,
                DavItemId = davRarFile.Id,
                FileDate = davRarFile.ReleaseDate,
                FileSize = davRarFile.FileSize  // Total file size for UI display
            }
        );

        // return the stream
        var id = davRarFile.Id;
        var rarFile = await dbClient.Ctx.RarFiles.Where(x => x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (rarFile is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");
        var stream = new DavMultipartFileStream
        (
            rarFile.ToDavMultipartFileMeta().FileParts,
            usenetClient,
            configManager.GetTotalStreamingConnections(),
            usageContext
        );
        return new RarDeobfuscationStream(stream, rarFile.ObfuscationKey);
    }
}