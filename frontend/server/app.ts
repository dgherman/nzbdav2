import "react-router";
import { createRequestHandler } from "@react-router/express";
import express from "express";
import { createProxyMiddleware } from "http-proxy-middleware";
import { websocketServer } from "./websocket.server";
import { isAuthenticated } from "~/auth/authentication.server";
import { authMiddleware } from "~/auth/auth-middleware.server";

declare module "react-router" {
  interface AppLoadContext {
    VALUE_FROM_EXPRESS: string;
  }
}

export const app = express();
export const initializeWebsocketServer = websocketServer.initialize;

// Proxy all webdav and api requests to the backend.
//
// `changeOrigin` MUST be false. With `changeOrigin: true`, http-proxy-middleware rewrites
// the outbound `Host` header to the target's host (i.e. `localhost:8080`). NWebDav then
// builds its `<D:href>` response URLs from `Request.Host`, so PROPFIND responses end up
// containing absolute hrefs like `http://localhost:8080/.ids` instead of the public-facing
// host the client connected to. Older rclone (≤ v1.73.x) ignored the host component of
// hrefs and used only the path, so this worked by accident. rclone v1.74.0 became strict
// about validating href hosts against the connected host, which causes PROPFIND listings
// to appear empty even though the server returns 207 with a full body. Keeping the
// original `Host` header (`changeOrigin: false`) makes NWebDav emit hrefs with the
// correct public host. Kestrel does not validate the Host header, so this is safe.
const forwardToBackend = createProxyMiddleware({
  target: process.env.BACKEND_URL,
  changeOrigin: false,
});

const setApiKeyForAuthenticatedRequests = async (req: express.Request) => {
  // if the path is not an API/metrics backend proxy request, do nothing
  if (!req.path.startsWith("/api") && req.path !== "/metrics") return;
  var apikey = req.query.apikey || req.query.apiKey || req.headers["x-api-key"];
  var hasApiKey = apikey && typeof apikey === "string";

  // if the request already has an apikey, do nothing
  if (hasApiKey) return;

  // if the request is not authenticated, do nothing
  const authenticated = await isAuthenticated(req);
  if (!authenticated) return;

  // otherwise, set the api key header
  req.headers["x-api-key"] = process.env.FRONTEND_BACKEND_API_KEY || "";
}

app.use(async (req, res, next) => {
  const path = decodeURIComponent(req.path);
  if (
    req.method.toUpperCase() === "PROPFIND"
    || req.method.toUpperCase() === "OPTIONS"
    || path.startsWith("/api")
    || path.startsWith("/view")
    || path.startsWith("/.ids")
    || path.startsWith("/nzbs")
    || path.startsWith("/content")
    || path.startsWith("/completed-symlinks")
  ) {
    await setApiKeyForAuthenticatedRequests(req);
    return forwardToBackend(req, res, next);
  }
  next();
});

// Silently return 404 for browser-generated static asset requests that would
// otherwise produce verbose error logging from React Router's SSR handler
app.use((req, res, next) => {
  const path = req.path.toLowerCase();
  if (
    path.includes("apple-touch-icon") ||
    path.includes("favicon") && path !== "/favicon.ico" ||
    path === "/robots.txt" ||
    path === "/site.webmanifest" ||
    path === "/browserconfig.xml"
  ) {
    return res.status(404).end();
  }
  next();
});

// Require authentication for all React Router routes
app.use(authMiddleware);

// Forward /metrics only after frontend authentication. Authenticated UI users get
// the backend API key injected automatically; unauthenticated browser or public
// internet requests are redirected to login instead of exposing Prometheus data.
app.use(async (req, res, next) => {
  const path = decodeURIComponent(req.path);
  if (path === "/metrics") {
    await setApiKeyForAuthenticatedRequests(req);
    return forwardToBackend(req, res, next);
  }
  next();
});

// Let frontend handle all other requests
app.use(
  createRequestHandler({
    build: () => import("virtual:react-router/server-build"),
    getLoadContext() {
      return {
        VALUE_FROM_EXPRESS: "Hello from Express",
      };
    },
  }),
);
