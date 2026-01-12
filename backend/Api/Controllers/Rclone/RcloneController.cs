using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Rclone;

[ApiController]
[Route("api/rclone")]
public class RcloneController(RcloneRcService rcloneRcService) : ControllerBase
{
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromQuery] string? path)
    {
        var success = await rcloneRcService.RefreshAsync(path);
        return success ? Ok() : NotFound(new { error = "Rclone RC command failed. Check logs." });
    }

    [HttpPost("forget")]
    public async Task<IActionResult> Forget([FromBody] string[] files)
    {
        var success = await rcloneRcService.ForgetAsync(files);
        return success ? Ok() : NotFound(new { error = "Rclone RC command failed. Check logs." });
    }
}
