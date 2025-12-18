using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.RemoveUnlinkedFiles;

[ApiController]
[Route("api/remove-unlinked-files")]
public class RemoveUnlinkedFilesController(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager,
    ProviderErrorService providerErrorService
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var task = new RemoveUnlinkedFilesTask(configManager, dbClient, websocketManager, providerErrorService, isDryRun: false);
        await task.Execute();
        return Ok(new RemoveUnlinkedFilesResponse(RemoveUnlinkedFilesTask.GetAuditReport()));
    }
}

public class RemoveUnlinkedFilesResponse(string report) : BaseApiResponse
{
    public string Report { get; } = report;
}