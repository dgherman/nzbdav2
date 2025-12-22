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

                // Capture ServiceScopeFactory to create a new scope in the background task
                // We cannot use context.RequestServices inside the task because the request might be finished (disposed) by then.
                var scopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
                var davItemId = davItem.Id;
                var davItemName = davItem.Name;

                // Queue health check in background - fire and forget to avoid blocking streaming
                _ = Task.Run(async () =>
                {
                    const int maxRetries = 3;
                    for (int i = 0; i < maxRetries; i++)
                    {
                        try
                        {
                            using var scope = scopeFactory.CreateScope();
                            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                            // Optimization: Combine delete + update into single operation
                            // Delete old results asynchronously (fire and forget)
                            _ = dbContext.HealthCheckResults
                                .Where(h => h.DavItemId == davItemId && h.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded)
                                .ExecuteDeleteAsync();

                            // Set priority to immediate health check
                            var rows = await dbContext.Items
                                .Where(x => x.Id == davItemId && x.NextHealthCheck != DateTimeOffset.MinValue)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.NextHealthCheck, DateTimeOffset.MinValue))
                                .ConfigureAwait(false);

                            if (rows > 0)
                                Log.Information($"[HealthCheckTrigger] Item `{davItemName}` priority set to immediate health check/repair due to missing articles.");
                            else
                                Log.Information($"[HealthCheckTrigger] Item `{davItemName}` already at highest priority for health check due to missing articles.");
                            break; // Success
                        }
                        catch (Exception ex) when (i < maxRetries - 1 && ex.Message.Contains("database is locked"))
                        {
                            Log.Warning($"[HealthCheckTrigger] Database locked on attempt {i + 1}/{maxRetries}, retrying...");
                            await Task.Delay(100 * (i + 1)); // Exponential backoff: 100ms, 200ms, 300ms
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to queue item for health check.");
                            break;
                        }
                    }
                });
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