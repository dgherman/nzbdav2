import { test } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import type { AddressInfo } from "node:net";
import express from "express";
import compression from "compression";
import { shouldCompressRequestPath } from "../server-compression.ts";

// Issue #35 (cross-review BLOCKING 1): a request for a malformed URL such as
// `GET /%` makes decodeURIComponent throw `URIError: URI malformed`. In
// production the compression filter is re-invoked while finalhandler writes the
// error response — a *later tick*, outside any try/catch — so the throw becomes
// an uncaughtException and the process exits 1.
//
// This reproduces that async path in-process: the downstream handler defers its
// response with setImmediate, so the throwing filter's URIError escapes as an
// uncaughtException instead of being swallowed by Express's synchronous
// try/catch around the route handler.
//
// Before the fix (shouldCompressRequestPath reverted to an inline throwing
// `decodeURIComponent`): the deferred write throws -> uncaughtException -> the
// test runner fails this test, and the follow-up request never gets 200.
// After the fix: no throw, follow-up request returns 200, `seen` stays empty.

function buildApp() {
  const app = express();
  app.use(
    compression({
      filter: (req, res) => {
        if (!shouldCompressRequestPath(req.path)) return false;
        return compression.filter(req, res);
      },
    }),
  );
  // Defer past Express's synchronous try/catch so a filter throw surfaces as
  // uncaughtException, matching the production finalhandler re-entry.
  app.use((_req, res) => {
    setImmediate(() => res.type("text/html").send("x".repeat(4096)));
  });
  return app;
}

function request(port: number, path: string): Promise<{ status: number; errored: boolean }> {
  return new Promise((resolve) => {
    const req = http.request({ host: "127.0.0.1", port, path, method: "GET" }, (res) => {
      res.resume();
      res.on("end", () => resolve({ status: res.statusCode ?? 0, errored: false }));
    });
    req.on("error", () => resolve({ status: 0, errored: true }));
    req.setTimeout(4000, () => req.destroy());
    req.end();
  });
}

test("malformed URL must not raise an uncaughtException from the compression filter", async () => {
  const seen: unknown[] = [];
  const onUncaught = (err: unknown) => seen.push(err);
  process.on("uncaughtException", onUncaught);

  const server = http.createServer(buildApp());
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  const port = (server.address() as AddressInfo).port;

  try {
    // the malformed request — transport outcome does not matter, a crash does
    await request(port, "/%");
    // give any deferred throw a chance to surface
    await new Promise((r) => setTimeout(r, 50));

    // server is still up and serving normal traffic
    const ok = await request(port, "/");
    assert.equal(ok.errored, false, "follow-up request errored — server went down");
    assert.equal(ok.status, 200);

    assert.deepEqual(
      seen.map(String),
      [],
      "compression filter raised an uncaughtException on the malformed URL",
    );
  } finally {
    process.removeListener("uncaughtException", onUncaught);
    await new Promise<void>((r) => server.close(() => r()));
  }
});
