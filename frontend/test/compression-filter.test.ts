import { test } from "node:test";
import assert from "node:assert/strict";
import { shouldCompress, shouldCompressRequestPath } from "../server-compression.ts";

// Issue #35: proxied backend responses must be exempt from compression so their
// Content-Length survives and HTTP range/seek keeps working.

test("shouldCompress skips /view media stream paths", () => {
  assert.equal(shouldCompress("/view/movies/Big.Buck.Bunny.mkv"), false);
  assert.equal(shouldCompress("/view"), false);
});

test("shouldCompress skips the other backend-proxied prefixes", () => {
  for (const p of [
    "/.ids/abc-123",
    "/nzbs/some.nzb",
    "/content/foo/bar.mkv",
    "/completed-symlinks/Show/S01E01.mkv",
    "/api/sab?mode=queue",
  ]) {
    assert.equal(shouldCompress(p), false, `expected ${p} to be exempt`);
  }
});

test("shouldCompress skips the exact /metrics endpoint", () => {
  assert.equal(shouldCompress("/metrics"), false);
});

test("shouldCompress still compresses normal HTML and client asset paths", () => {
  assert.equal(shouldCompress("/"), true);
  assert.equal(shouldCompress("/settings"), true);
  assert.equal(shouldCompress("/queue"), true);
  assert.equal(shouldCompress("/assets/root-abc123.js"), true);
  // "/metrics" is exempt only as an exact match, not as a prefix
  assert.equal(shouldCompress("/metrics-dashboard"), true);
});

// Issue #35 (cross-review BLOCKING 1): decodeURIComponent throws URIError on a
// malformed %-sequence; that must never escape the compression filter.
test("shouldCompressRequestPath never throws on a malformed percent-sequence", () => {
  for (const raw of ["/%", "/%zz", "/view/%E0%A4%A", "/%C0", "/api/%"]) {
    let result: boolean;
    assert.doesNotThrow(() => {
      result = shouldCompressRequestPath(raw);
    }, `shouldCompressRequestPath(${JSON.stringify(raw)}) threw`);
    assert.equal(typeof result!, "boolean");
  }
});

test("shouldCompressRequestPath falls back to the raw path when decode fails", () => {
  // exempt prefix still recognised on the still-encoded path
  assert.equal(shouldCompressRequestPath("/view/%E0%A4%A"), false);
  assert.equal(shouldCompressRequestPath("/api/%"), false);
  // non-exempt malformed path just falls through to compression.filter
  assert.equal(shouldCompressRequestPath("/%"), true);
  // well-formed encoded paths still decode and match
  assert.equal(shouldCompressRequestPath("/view/%41"), false); // %41 -> "A"
});
