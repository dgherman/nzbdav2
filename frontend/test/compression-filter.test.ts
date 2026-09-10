import { test } from "node:test";
import assert from "node:assert/strict";
import { shouldCompress } from "../server-compression.ts";

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
