namespace NzbWebDAV.Api.Controllers.SearchWebdav;

public class SearchWebdavResponse
{
    public List<SearchResult> Results { get; set; } = new();

    public class SearchResult
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long? Size { get; set; }
        public string? DavItemId { get; set; }
    }
}
