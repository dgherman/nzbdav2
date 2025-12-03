namespace NzbWebDAV.Api.Controllers.TestUsenetThroughput;

public class TestUsenetThroughputResponse : BaseApiResponse
{
    public bool Success { get; set; }
    public double SpeedInMBps { get; set; }
    public string Message { get; set; }
}