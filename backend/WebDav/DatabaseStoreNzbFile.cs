using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.WebDav.Base;

using NzbWebDAV.Services;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreNzbFile(
    DavItem davNzbFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    NzbAnalysisService nzbAnalysisService
) : BaseStoreStreamFile
{
    public DavItem DavItem => davNzbFile;
    public override string Name => davNzbFile.Name;
    public override string UniqueKey => davNzbFile.Id.ToString();
    public override long FileSize => davNzbFile.FileSize!.Value;
    public override DateTime CreatedAt => davNzbFile.CreatedAt;

    public override async Task<Stream> GetStreamAsync(CancellationToken cancellationToken)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davNzbFile;

        // create streaming usage context
        var usageContext = new ConnectionUsageContext(
            ConnectionUsageType.Streaming,
            new ConnectionUsageDetails
            {
                Text = davNzbFile.Path,
                JobName = davNzbFile.Name,
                AffinityKey = Path.GetFileName(Path.GetDirectoryName(davNzbFile.Path)),
                DavItemId = davNzbFile.Id,
                FileDate = davNzbFile.ReleaseDate
            }
        );

        // return the stream with usage context and buffering options
        var id = davNzbFile.Id;
        var file = await dbClient.GetNzbFileAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");

        // Trigger background analysis if cache is missing
        if (file.SegmentSizes == null)
        {
            nzbAnalysisService.TriggerAnalysisInBackground(file.Id, file.SegmentIds);
        }

        Serilog.Log.Debug("[DatabaseStoreNzbFile] Opening stream for {FileName} ({Id})", Name, id);

        return usenetClient.GetFileStream(
            file.SegmentIds,
            FileSize,
            configManager.GetConnectionsPerStream(),
            usageContext,
            configManager.UseBufferedStreaming(),
            configManager.GetStreamBufferSize(),
            file.GetSegmentSizes()
        );
    }
}
