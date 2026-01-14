using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.DownloadKey;

[ApiController]
[Route("api/download-key")]
public class DownloadKeyController(ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var action = HttpContext.Request.Query["action"].FirstOrDefault() ?? "get";

        return action switch
        {
            "get" => await GetDownloadKey().ConfigureAwait(false),
            "regenerate" => await RegenerateDownloadKey().ConfigureAwait(false),
            _ => BadRequest(new DownloadKeyResponse { Status = false, Error = "Invalid action" })
        };
    }

    private Task<IActionResult> GetDownloadKey()
    {
        var key = configManager.GetStaticDownloadKey();
        return Task.FromResult<IActionResult>(Ok(new DownloadKeyResponse
        {
            Status = true,
            DownloadKey = key
        }));
    }

    private async Task<IActionResult> RegenerateDownloadKey()
    {
        var key = await configManager.RegenerateStaticDownloadKeyAsync().ConfigureAwait(false);
        return Ok(new DownloadKeyResponse
        {
            Status = true,
            DownloadKey = key
        });
    }
}

public class DownloadKeyResponse : BaseApiResponse
{
    public string? DownloadKey { get; set; }
}
