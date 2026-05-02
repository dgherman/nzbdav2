using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Middlewares;

/// <summary>
/// Strips <c>&lt;D:propstat&gt;</c> blocks whose <c>&lt;D:status&gt;</c> is <c>HTTP/1.1 404 Not Found</c>
/// from PROPFIND responses produced by NWebDav.
///
/// Background:
/// NWebDav (correctly per RFC 4918 §9.1) returns one <c>&lt;D:response&gt;</c> per resource that
/// can contain multiple <c>&lt;D:propstat&gt;</c> blocks — one for properties found (200) and one
/// for properties NOT found (404). Properties like <c>getcontentlength</c> /
/// <c>getcontenttype</c> are file-only, so on collection nodes they are reported in a 404
/// propstat. rclone v1.74.0 became strict about this: any 404 propstat causes the entire
/// item to be discarded with the message
/// <c>Ignoring item with bad status ["HTTP/1.1 404 Not Found" "HTTP/1.1 200 OK"]</c>.
/// rclone v1.73.x ignored 404 propstats.
///
/// The cleanest workaround that doesn't fork NWebDav is to post-process the PROPFIND
/// response body and remove the 404 propstat blocks before sending. The remaining 200
/// propstat block is fully spec-compliant and accepted by both old and new rclone (as
/// well as Plex, Jellyfin, Finder, Windows Explorer, etc.).
/// </summary>
public static class PropFindResponseCleanupMiddleware
{
    // Match a single <D:propstat>...</D:propstat> block whose <D:status> contains "404".
    // The XML is deterministic (NWebDav always emits prefix "D:"), so a non-greedy regex is
    // both simpler and faster than a full XML parse on every PROPFIND response.
    private static readonly Regex PropStat404Regex = new(
        @"<D:propstat>(?:(?!</D:propstat>).)*?<D:status>HTTP/1\.1 404[^<]*</D:status>(?:(?!</D:propstat>).)*?</D:propstat>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static IApplicationBuilder UsePropFindResponseCleanup(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            // Only intercept PROPFIND. Everything else passes through untouched (no
            // body buffering, no memory cost, no behavioural change).
            if (!string.Equals(context.Request.Method, "PROPFIND", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next().ConfigureAwait(false);

                buffer.Position = 0;
                var contentType = context.Response.ContentType ?? string.Empty;
                if (buffer.Length == 0 || !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                {
                    // Non-XML body (e.g. an error page) — pass through verbatim.
                    await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                    return;
                }

                var xml = Encoding.UTF8.GetString(buffer.ToArray());
                var cleaned = PropStat404Regex.Replace(xml, string.Empty);
                var bytes = Encoding.UTF8.GetBytes(cleaned);
                context.Response.ContentLength = bytes.Length;
                await originalBody.WriteAsync(bytes).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        });
    }
}
