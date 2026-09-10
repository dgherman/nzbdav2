import { test } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import express from "express";
import compression from "compression";
import { shouldCompressRequestPath } from "../server-compression.ts";

// Issue #35 (cross-review BLOCKING 1): a request for a malformed URL such as
// `GET /%` makes decodeURIComponent throw `URIError: URI malformed` inside the
// compression filter. The filter runs while response headers are written (for the
// error response too), so an escaped throw takes down the whole process with
// exit 1. This mounts the real filter middleware in isolation (no SSR build) and
// asserts the request still gets an HTTP response and the server stays up.
//
// Before the fix: the filter threw, Express's error path re-entered the filter,
// the throw escaped finalhandler -> uncaughtException -> this test errors out
// with no response. After the fix: a normal response comes back.

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
  // body large enough that compression would otherwise engage
  app.use((_req, res) => res.type("text/html").send("x".repeat(4096)));
  return app;
}

function get(port: number, path: string): Promise<{ status: number; body: string }> {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: "127.0.0.1", port, path, method: "GET" },
      (res) => {
        const chunks: Buffer[] = [];
        res.on("data", (c) => chunks.push(c as Buffer));
        res.on("end", () =>
          resolve({ status: res.statusCode ?? 0, body: Buffer.concat(chunks).toString("utf8") }),
        );
      },
    );
    req.on("error", reject);
    req.setTimeout(4000, () => req.destroy(new Error("request timed out")));
    req.end();
  });
}

test("GET /%  (malformed URL) gets a response and the server does not crash", async () => {
  const server = http.createServer(buildApp());
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  const port = (server.address() as import("node:net").AddressInfo).port;
  try {
    // the malformed request: must resolve, not reject
    const bad = await get(port, "/%");
    assert.equal(typeof bad.status, "number");
    assert.ok(bad.status >= 200 && bad.status < 600, `unexpected status ${bad.status}`);

    // server still serving: a normal request right after still works
    const ok = await get(port, "/queue");
    assert.equal(ok.status, 200);
    assert.equal(ok.body.length, 4096);
  } finally {
    await new Promise<void>((r) => server.close(() => r()));
  }
});
