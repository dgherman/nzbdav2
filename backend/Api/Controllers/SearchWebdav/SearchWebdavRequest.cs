using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.SearchWebdav;

public class SearchWebdavRequest
{
    public string Query { get; set; }
    public string Directory { get; set; }

    public SearchWebdavRequest(HttpContext context)
    {
        var form = context.Request.Form;
        Query = form["query"].ToString();
        Directory = form["directory"].ToString();
    }
}
