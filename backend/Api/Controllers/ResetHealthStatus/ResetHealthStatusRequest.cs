namespace NzbWebDAV.Api.Controllers.ResetHealthStatus;

public class ResetHealthStatusRequest
{
    public List<string> DavItemIds { get; init; } = [];
}
