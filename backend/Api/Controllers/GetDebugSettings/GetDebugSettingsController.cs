using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Logging;

namespace NzbWebDAV.Api.Controllers.GetDebugSettings;

public class GetDebugSettingsController(ConfigManager configManager) : ControllerBase
{
    [HttpGet("api/debug-settings")]
    public IActionResult GetDebugSettings()
    {
        var enabledComponents = configManager.GetDebugLogComponents();

        return Ok(new
        {
            availableComponents = LogComponents.AllComponents,
            enabledComponents = enabledComponents.ToArray()
        });
    }
}
