using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server.Helpers;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Middlewares;

public class ExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (IsCausedByAbortedRequest(e, context))
        {
            // If the response has not started, we can write our custom response
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 499; // Non-standard status code for client closed request
                await context.Response.WriteAsync("Client closed request.").ConfigureAwait(false);
            }
        }
        catch (UsenetArticleNotFoundException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            
            if (context.Items["DavItem"] is DavItem davItem)
            {
                // Log with Job Name context
                Log.Error("File `{FilePath}` (Job: {JobName}) has missing articles: {ErrorMessage}", filePath, davItem.Name, e.Message);

                try
                {
                    using var scope = context.RequestServices.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                    // Reset any existing error status so the item is picked up by the queue query
                    await dbContext.HealthCheckResults
                        .Where(h => h.DavItemId == davItem.Id && h.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded)
                        .ExecuteDeleteAsync()
                        .ConfigureAwait(false);
                    
                    var rows = await dbContext.Items
                        .Where(x => x.Id == davItem.Id && x.NextHealthCheck != DateTimeOffset.MinValue)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.NextHealthCheck, DateTimeOffset.MinValue))
                        .ConfigureAwait(false);
                        
                    if (rows > 0)
                        Log.Information($"[HealthCheckTrigger] Item `{davItem.Name}` priority set to immediate health check/repair due to missing articles.");
                    else
                        Log.Information($"[HealthCheckTrigger] Item `{davItem.Name}` already at highest priority for health check due to missing articles.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to queue item for health check.");
                }
            }
            else
            {
                // Log without Job Name context
                Log.Error("File `{FilePath}` has missing articles: {ErrorMessage}", filePath, e.Message);
            }
        }
        catch (SeekPositionNotFoundException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "unknown";
            Log.Error($"File `{filePath}` could not seek to byte position: {seekPosition}");
        }
        catch (System.TimeoutException e)
        {
            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "0";

            if (e.Message.Contains("Operation timed out on provider "))
            {
                // Log without stack trace for specific timeout messages
                Log.Warning("Operation timed out on Usenet provider for file `{FilePath}` at seek position {SeekPosition}: {ErrorMessage}", filePath, seekPosition, e.Message);
            }
            else
            {
                // Log with stack trace for other TimeoutExceptions
                Log.Error(e, "An unhandled timeout exception occurred for file `{FilePath}` at seek position {SeekPosition}.", filePath, seekPosition);
            }

            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 500; // Internal Server Error
                await context.Response.WriteAsync("An error occurred during processing.").ConfigureAwait(false);
            }
        }
        catch (Exception e) when (IsDavItemRequest(context))
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 500;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "0";
            Log.Error($"File `{filePath}` could not be read from byte position: {seekPosition} " +
                      $"due to unhandled {e.GetType()}: {e.Message}");
        }
    }

    private bool IsCausedByAbortedRequest(Exception e, HttpContext context)
    {
        var isAffectedException = e is OperationCanceledException or EndOfStreamException;
        var isRequestAborted = context.RequestAborted.IsCancellationRequested ||
                               SigtermUtil.GetCancellationToken().IsCancellationRequested;
        return isAffectedException && isRequestAborted;
    }

    private static string GetRequestFilePath(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem davItem
            ? davItem.Path
            : context.Request.Path;
    }

    private static bool IsDavItemRequest(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem;
    }
}