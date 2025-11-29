namespace NzbWebDAV.Database.Models;

public class BandwidthSample
{
    public int Id { get; set; }
    public int ProviderIndex { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public long Bytes { get; set; }
}
