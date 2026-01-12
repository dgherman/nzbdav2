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
        await rcloneRcService.RefreshAsync(path);
        return Ok();
    }

    [HttpPost("forget")]
    public async Task<IActionResult> Forget([FromBody] string[] files)
    {
        await rcloneRcService.ForgetAsync(files);
        return Ok();
    }
}
