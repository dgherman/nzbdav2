using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.RegisterLocalLink;

public class RegisterLocalLinkRequest
{
    [JsonPropertyName("nzoId")]
    public string NzoId { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("linkPath")]
    public string LinkPath { get; set; } = string.Empty;
}
