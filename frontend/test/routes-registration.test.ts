import { test } from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";

// Issue #36: the reset-connections route lives at
// app/routes/settings.maintenance.reset-connections/route.tsx so flatRoutes()
// actually mounts it. Before the fix it was nested colocation
// (app/routes/settings/maintenance/reset-connections.tsx) and never registered,
// so the Connection Management buttons POSTed into the SSR 404 handler.
//
// app/routes.ts calls `await flatRoutes()`, which reads
// globalThis.__reactRouterAppDirectory (normally set by the react-router CLI).
// Set it here, then import the real route config the app ships.
const appDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../app");
(globalThis as unknown as { __reactRouterAppDirectory: string }).__reactRouterAppDirectory = appDir;

const routes = (await import("../app/routes.ts")).default;

type RouteNode = { path?: string; file?: string; children?: RouteNode[] };

function collect(nodes: RouteNode[], parent = ""): { path: string; file?: string }[] {
  const out: { path: string; file?: string }[] = [];
  for (const node of nodes) {
    const seg = (node.path ?? "").replace(/^\/+|\/+$/g, "");
    const full = [parent, seg].filter(Boolean).join("/");
    out.push({ path: "/" + full, file: node.file });
    if (node.children) out.push(...collect(node.children, full));
  }
  return out;
}

test("reset-connections route is registered at /settings/maintenance/reset-connections", () => {
  const all = collect(routes as RouteNode[]);
  const match = all.find((r) => r.path === "/settings/maintenance/reset-connections");
  assert.ok(
    match,
    "expected a route mounted at /settings/maintenance/reset-connections, got:\n" +
      all.map((r) => `  ${r.path} -> ${r.file}`).join("\n"),
  );
  assert.match(match!.file ?? "", /settings\.maintenance\.reset-connections[/\\]route\.tsx$/);
});

test("the pre-fix colocated path is not what got mounted", () => {
  const all = collect(routes as RouteNode[]);
  const bad = all.find((r) => r.file?.endsWith("settings/maintenance/reset-connections.tsx"));
  assert.equal(bad, undefined);
});
