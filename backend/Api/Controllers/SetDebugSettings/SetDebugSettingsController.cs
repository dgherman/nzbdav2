using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Logging;

namespace NzbWebDAV.Api.Controllers.SetDebugSettings;

public class SetDebugSettingsController(
    ConfigManager configManager,
    DavDatabaseClient dbClient
) : ControllerBase
{
    [HttpPost("api/debug-settings")]
    public async Task<IActionResult> SetDebugSettings([FromBody] SetDebugSettingsRequest request)
    {
        // Validate components
        var validComponents = request.EnabledComponents
            .Where(c => LogComponents.AllComponents.Contains(c) || c == LogComponents.All)
            .ToList();

        var configValue = JsonSerializer.Serialize(validComponents);

        // Update database
        var configItem = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(x => x.ConfigName == "debug.components")
            .ConfigureAwait(false);

        if (configItem == null)
        {
            configItem = new ConfigItem
            {
                ConfigName = "debug.components",
                ConfigValue = configValue
            };
            dbClient.Ctx.ConfigItems.Add(configItem);
        }
        else
        {
            configItem.ConfigValue = configValue;
        }

        await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);

        // Update in-memory config
        configManager.UpdateValues([configItem]);

        return Ok(new { success = true });
    }
}

public class SetDebugSettingsRequest
{
    public List<string> EnabledComponents { get; set; } = [];
}
