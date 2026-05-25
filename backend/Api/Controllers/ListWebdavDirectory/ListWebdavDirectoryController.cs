using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NWebDav.Server.Stores;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Api.Controllers.ListWebdavDirectory;

[ApiController]
[Route("api/list-webdav-directory")]
public class ListWebdavDirectoryController(DatabaseStore store, ConfigManager configManager, DavDatabaseClient dbClient, ILogger<ListWebdavDirectoryController> logger) : BaseApiController
{
    private async Task<ListWebdavDirectoryResponse> ListWebdavDirectory(ListWebdavDirectoryRequest request)
    {
        var item = await store.GetItemAsync(request.Directory, HttpContext.RequestAborted).ConfigureAwait(false);
        if (item is null) throw new BadHttpRequestException("The directory does not exist.");
        if (item is not IStoreCollection dir) throw new BadHttpRequestException("The directory does not exist.");
        var children = new List<ListWebdavDirectoryResponse.DirectoryItem>();
        var showHiddenWebdavFiles = configManager.ShowHiddenWebdavFiles();
        await foreach (var child in dir.GetItemsAsync(HttpContext.RequestAborted))
        {
            if (!showHiddenWebdavFiles && child.Name.StartsWith('.'))
                continue;

            string? davItemId = null;
            if (child is DatabaseStoreCollection dirCollection)
            {
                davItemId = dirCollection.UniqueKey;
            }
            else if (child is DatabaseStoreNzbFile nzbFile)
            {
                davItemId = nzbFile.DavItem.Id.ToString();
            }
            else if (child is DatabaseStoreMultipartFile multipartFile)
            {
                davItemId = multipartFile.DavItem.Id.ToString();
            }

            children.Add(new ListWebdavDirectoryResponse.DirectoryItem()
            {
                Name = child.Name,
                IsDirectory = (child is IStoreCollection),
                Size = (child is BaseStoreItem bsi ? bsi.FileSize : null),
                DavItemId = davItemId
            });
        }

        return new ListWebdavDirectoryResponse() { Items = children };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new ListWebdavDirectoryRequest(HttpContext);
        var response = await ListWebdavDirectory(request).ConfigureAwait(false);
        return Ok(response);
    }
}