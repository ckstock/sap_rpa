const fs = require("fs");
const http = require("http");
const https = require("https");
const path = require("path");
const { URL } = require("url");

const runtimeRoot = path.resolve(process.env.RPA_RUNTIME_ROOT || path.join(__dirname, ".."));
const publicPath = normalizeBasePath(process.env.RPA_PUBLIC_PATH || "/rpa");
const port = Number(process.env.RPA_GATEWAY_PORT || "6174");
const host = process.env.RPA_GATEWAY_HOST || "0.0.0.0";
const apiTarget = new URL(process.env.RPA_API_TARGET || "http://127.0.0.1:8080");
const gatewayProtocol = String(process.env.RPA_GATEWAY_PROTOCOL || "http").trim().toLowerCase();
const tlsCertPath = process.env.RPA_TLS_CERT || "";
const tlsKeyPath = process.env.RPA_TLS_KEY || "";
const tlsCaPath = process.env.RPA_TLS_CA || "";
const tlsPassphrase = process.env.RPA_TLS_PASSPHRASE || "";
const useHttps = gatewayProtocol === "https" || Boolean(tlsCertPath || tlsKeyPath);
const logFile = path.join(runtimeRoot, "logs", "rpa-gateway.log");

const contentTypes = {
  ".css": "text/css; charset=utf-8",
  ".gif": "image/gif",
  ".html": "text/html; charset=utf-8",
  ".ico": "image/x-icon",
  ".jpeg": "image/jpeg",
  ".jpg": "image/jpeg",
  ".js": "application/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".map": "application/json; charset=utf-8",
  ".png": "image/png",
  ".svg": "image/svg+xml; charset=utf-8",
  ".txt": "text/plain; charset=utf-8",
  ".vbs": "text/plain; charset=utf-8",
  ".webp": "image/webp"
};

function normalizeBasePath(value) {
  let base = String(value || "/rpa").trim();
  if (!base.startsWith("/")) base = "/" + base;
  return base.replace(/\/+$/, "") || "/rpa";
}

function log(message) {
  const line = `[${new Date().toISOString()}] ${message}\n`;
  try {
    fs.mkdirSync(path.dirname(logFile), { recursive: true });
    fs.appendFileSync(logFile, line, "utf8");
  } catch (_) {
    // Logging must never break request handling.
  }
}

function sendText(res, statusCode, body) {
  const text = body || "";
  res.writeHead(statusCode, {
    "Content-Type": "text/plain; charset=utf-8",
    "Content-Length": Buffer.byteLength(text)
  });
  res.end(text);
}

function redirect(res, location) {
  res.writeHead(302, { Location: location });
  res.end();
}

function injectApiBase(html) {
  const snippet = [
    "<script>",
    `window.SAP_RPA_API_BASE = window.location.origin + ${JSON.stringify(publicPath)};`,
    "try {",
    "  var desired = window.SAP_RPA_API_BASE;",
    "  localStorage.setItem('sapRpaApiBase', desired);",
    "} catch (_) {}",
    "</script>"
  ].join("");

  if (html.includes("</head>")) {
    return html.replace("</head>", `${snippet}\n</head>`);
  }
  return snippet + html;
}

function resolveStaticPath(requestPath) {
  let relativePath = requestPath;
  if (relativePath === "/" || relativePath === "") {
    relativePath = "/index.html";
  }

  let decoded;
  try {
    decoded = decodeURIComponent(relativePath);
  } catch (_) {
    return null;
  }

  const normalized = path.normalize(decoded).replace(/^([/\\])+/, "");
  if (!isPublicStaticPath(normalized)) {
    return null;
  }

  const fullPath = path.resolve(runtimeRoot, normalized);
  if (fullPath !== runtimeRoot && !fullPath.startsWith(runtimeRoot + path.sep)) {
    return null;
  }
  return fullPath;
}

function isPublicStaticPath(normalizedPath) {
  const parts = normalizedPath.split(/[\\/]+/).filter(Boolean);
  if (parts.length === 1 && parts[0].toLowerCase() === "index.html") {
    return true;
  }
  return parts.length >= 2 && parts[0].toLowerCase() === "assets";
}

function serveStatic(req, res, requestPath) {
  if (req.method !== "GET" && req.method !== "HEAD") {
    sendText(res, 405, "Method Not Allowed");
    return;
  }

  const filePath = resolveStaticPath(requestPath);
  if (!filePath) {
    sendText(res, 400, "Bad Request");
    return;
  }

  fs.stat(filePath, (statError, stats) => {
    if (statError || !stats.isFile()) {
      sendText(res, 404, "Not Found");
      return;
    }

    const extension = path.extname(filePath).toLowerCase();
    const type = contentTypes[extension] || "application/octet-stream";

    if (extension === ".html") {
      fs.readFile(filePath, "utf8", (readError, html) => {
        if (readError) {
          sendText(res, 500, "Failed to read file");
          return;
        }
        const body = Buffer.from(injectApiBase(html), "utf8");
        res.writeHead(200, {
          "Content-Type": type,
          "Content-Length": body.length,
          "Cache-Control": "no-store"
        });
        if (req.method === "HEAD") res.end();
        else res.end(body);
      });
      return;
    }

    res.writeHead(200, {
      "Content-Type": type,
      "Content-Length": stats.size,
      "Cache-Control": "public, max-age=60"
    });
    if (req.method === "HEAD") {
      res.end();
      return;
    }
    fs.createReadStream(filePath).pipe(res);
  });
}

function proxyApi(req, res, requestPath, search) {
  const headers = { ...req.headers, host: apiTarget.host };
  delete headers["accept-encoding"];

  const proxyReq = http.request(
    {
      protocol: apiTarget.protocol,
      hostname: apiTarget.hostname,
      port: apiTarget.port || 80,
      method: req.method,
      path: requestPath + (search || ""),
      headers
    },
    proxyRes => {
      const responseHeaders = { ...proxyRes.headers };
      responseHeaders["cache-control"] = responseHeaders["cache-control"] || "no-store";
      res.writeHead(proxyRes.statusCode || 502, responseHeaders);
      proxyRes.pipe(res);
    }
  );

  proxyReq.on("error", error => {
    log(`API proxy error: ${error.message}`);
    if (!res.headersSent) sendText(res, 502, "RPA API is not available");
    else res.end();
  });

  req.pipe(proxyReq);
}

function createRequestHandler(protocol) {
  return (req, res) => {
    const requestUrl = new URL(req.url || "/", `${protocol}://${req.headers.host || "localhost"}`);
    const pathname = requestUrl.pathname;

    if (pathname === "/") {
      redirect(res, `${publicPath}/`);
      return;
    }

    if (pathname === publicPath) {
      redirect(res, `${publicPath}/${requestUrl.search || ""}`);
      return;
    }

    if (!pathname.startsWith(publicPath + "/")) {
      sendText(res, 404, "Not Found");
      return;
    }

    const localPath = pathname.slice(publicPath.length) || "/";
    if (localPath === "/api" || localPath.startsWith("/api/")) {
      proxyApi(req, res, localPath, requestUrl.search);
      return;
    }

    serveStatic(req, res, localPath);
  };
}

function createServer() {
  const protocol = useHttps ? "https" : "http";
  const requestHandler = createRequestHandler(protocol);
  if (!useHttps) {
    return { protocol, server: http.createServer(requestHandler) };
  }

  if (!tlsCertPath || !tlsKeyPath) {
    throw new Error("RPA_TLS_CERT and RPA_TLS_KEY are required when HTTPS is enabled");
  }

  const options = {
    cert: fs.readFileSync(tlsCertPath),
    key: fs.readFileSync(tlsKeyPath)
  };
  if (tlsCaPath) {
    options.ca = fs.readFileSync(tlsCaPath);
  }
  if (tlsPassphrase) {
    options.passphrase = tlsPassphrase;
  }
  return { protocol, server: https.createServer(options, requestHandler) };
}

const { protocol, server } = createServer();

server.on("error", error => {
  log(`Gateway failed: ${error.stack || error.message}`);
  process.exitCode = 1;
});

server.listen(port, host, () => {
  log(`Gateway listening on ${protocol}://${host}:${port}${publicPath}/, root=${runtimeRoot}, api=${apiTarget.origin}`);
});
