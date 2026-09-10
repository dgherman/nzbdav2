// Issue #35: the Express frontend must not gzip/br-compress responses that are
// proxied straight through to the .NET backend. Compressing them strips the
// upstream Content-Length and forces `Transfer-Encoding: chunked`, which breaks
// HTTP range requests / seeking for media streamed via `/view` (Jellyfin `.strm`
// Direct Play) and corrupts Content-Length on the JSON APIs.
//
// The path list mirrors the canonical upstream nzbdav-dev/nzbdav
// `frontend/server.ts` filter; our list adds `/metrics` because this frontend
// also proxies the Prometheus endpoint (see `frontend/server/app.ts`).
const NO_COMPRESS_PREFIXES = [
  "/view",
  "/.ids",
  "/nzbs",
  "/content",
  "/completed-symlinks",
  "/api",
] as const;

/**
 * Returns false for any request path that is proxied to the backend and must keep
 * its original Content-Length (media streams, WebDAV file endpoints, JSON APIs,
 * Prometheus metrics); true for everything the frontend renders itself (HTML,
 * client assets), which is safe and beneficial to compress.
 *
 * `path` is expected to already be decoded. Prefer `shouldCompressRequestPath`
 * from request-handling code so a malformed `%`-sequence cannot throw.
 */
export function shouldCompress(path: string): boolean {
  if (path === "/metrics") return false;
  for (const prefix of NO_COMPRESS_PREFIXES) {
    if (path.startsWith(prefix)) return false;
  }
  return true;
}

/**
 * Nonthrowing entry point for the compression `filter`. `decodeURIComponent`
 * throws `URIError: URI malformed` on an invalid percent-sequence (e.g. `GET /%`),
 * and the filter runs while response headers are being written — an escaped throw
 * takes down the process. On decode failure we fall back to the raw, still-encoded
 * path: every exempt prefix is plain ASCII with no percent-encoding, so a
 * malformed path simply falls through to `compression.filter`, which is safe.
 */
export function shouldCompressRequestPath(rawPath: string): boolean {
  let p: string;
  try {
    p = decodeURIComponent(rawPath || "");
  } catch {
    p = rawPath || "";
  }
  return shouldCompress(p);
}
