using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server.Stores;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Api.Controllers.SearchWebdav;

[ApiController]
[Route("api/search-webdav")]
public class SearchWebdavController(DatabaseStore store, ConfigManager configManager, DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<SearchWebdavResponse> SearchWebdav(SearchWebdavRequest request)
    {
        var results = new List<SearchWebdavResponse.SearchResult>();
        var query = request.Query.ToLower();

        if (string.IsNullOrWhiteSpace(query))
            return new SearchWebdavResponse() { Results = results };

        var startItem = await store.GetItemAsync(request.Directory, HttpContext.RequestAborted).ConfigureAwait(false);
        if (startItem is null) throw new BadHttpRequestException("The directory does not exist.");
        if (startItem is not IStoreCollection startDir) throw new BadHttpRequestException("The directory does not exist.");

        var showHiddenWebdavFiles = configManager.ShowHiddenWebdavFiles();

        await SearchRecursive(startDir, request.Directory, query, results, showHiddenWebdavFiles);

        return new SearchWebdavResponse() { Results = results };
    }

    private async Task SearchRecursive(
        IStoreCollection dir,
        string currentPath,
        string query,
        List<SearchWebdavResponse.SearchResult> results,
        bool showHiddenFiles)
    {
        await foreach (var child in dir.GetItemsAsync(HttpContext.RequestAborted))
        {
            if (!showHiddenFiles && child.Name.StartsWith('.'))
                continue;

            var childPath = string.IsNullOrEmpty(currentPath)
                ? child.Name
                : $"{currentPath}/{child.Name}";

            // Check if name matches query
            if (child.Name.ToLower().Contains(query))
            {
                string? davItemId = null;
                if (child is not IStoreCollection) // Only for files, not directories
                {
                    // Check for different file types that have direct access to DavItem
                    if (child is DatabaseStoreNzbFile nzbFile)
                    {
                        davItemId = nzbFile.DavItem.Id.ToString();
                    }
                    else if (child is DatabaseStoreMultipartFile multipartFile)
                    {
                        davItemId = multipartFile.DavItem.Id.ToString();
                    }
                    else if (child is DatabaseStoreRarFile rarFile)
                    {
                        davItemId = rarFile.DavItem.Id.ToString();
                    }
                }

                results.Add(new SearchWebdavResponse.SearchResult()
                {
                    Name = child.Name,
                    Path = childPath,
                    IsDirectory = (child is IStoreCollection),
                    Size = (child is BaseStoreItem bsi ? bsi.FileSize : null),
                    DavItemId = davItemId
                });
            }

            // Recursively search subdirectories
            if (child is IStoreCollection subDir)
            {
                await SearchRecursive(subDir, childPath, query, results, showHiddenFiles);
            }
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new SearchWebdavRequest(HttpContext);
        var response = await SearchWebdav(request).ConfigureAwait(false);
        return Ok(response);
    }
}
