using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.RequeueHistoryItem;

public class RequeueHistoryItemResponse
{
    [JsonPropertyName("status")]
    public bool Status { get; set; }

    [JsonPropertyName("nzo_id")]
    public string? NzoId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
