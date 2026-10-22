// DCB website demo: proves the server's export (Saved Games\<server>\DCB\export) can drive a
// web page in real time. Node built-ins only. Serves the page, the current state and a
// server-sent-events stream that pushes state.json changes and new events.jsonl lines.
//   node tools/web-demo/server.js [export dir] [port]
// Binds to 127.0.0.1: a demo, not the website (see docs/RESEARCH-WEBSITE.md).
"use strict";
const http = require("http");
const fs = require("fs");
const path = require("path");
const os = require("os");

const exportDir = process.argv[2] || path.join(os.homedir(), "Saved Games", "DCS.server", "DCB", "export");
const port = Number(process.argv[3] || 8099);
const statePath = path.join(exportDir, "state.json");
const eventsPath = path.join(exportDir, "events.jsonl");
const page = fs.readFileSync(path.join(__dirname, "index.html"));

const clients = new Set();

function readState() {
  try { return fs.readFileSync(statePath, "utf8"); } catch { return null; }
}

function lastEvents(n) {
  try {
    const lines = fs.readFileSync(eventsPath, "utf8").split("\n").filter(Boolean);
    return lines.slice(-n);
  } catch { return []; }
}

function send(res, event, data) {
  res.write(`event: ${event}\ndata: ${data.replace(/\n/g, " ")}\n\n`);
}

// Watch by polling once a second: works on every file system and survives the hook's
// remove-and-rename of state.json.
let stateMtime = 0;
let eventsSize = (() => { try { return fs.statSync(eventsPath).size; } catch { return 0; } })();
setInterval(() => {
  try {
    const st = fs.statSync(statePath);
    if (st.mtimeMs !== stateMtime) {
      stateMtime = st.mtimeMs;
      const s = readState();
      if (s) for (const c of clients) send(c, "state", s);
    }
  } catch { /* state.json being replaced */ }
  try {
    const size = fs.statSync(eventsPath).size;
    if (size > eventsSize) {
      const fd = fs.openSync(eventsPath, "r");
      const buf = Buffer.alloc(size - eventsSize);
      fs.readSync(fd, buf, 0, buf.length, eventsSize);
      fs.closeSync(fd);
      eventsSize = size;
      for (const line of buf.toString("utf8").split("\n").filter(Boolean))
        for (const c of clients) send(c, "event", line);
    } else if (size < eventsSize) {
      eventsSize = size;  // file replaced
    }
  } catch { /* no events yet */ }
}, 1000);

http.createServer((req, res) => {
  const url = new URL(req.url, "http://localhost");
  if (url.pathname === "/") {
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    return res.end(page);
  }
  if (url.pathname === "/api/state") {
    const s = readState();
    res.writeHead(s ? 200 : 503, { "Content-Type": "application/json" });
    return res.end(s || '{"error":"no state yet"}');
  }
  if (url.pathname === "/api/events") {
    res.writeHead(200, { "Content-Type": "application/json" });
    return res.end("[" + lastEvents(Number(url.searchParams.get("n") || 100)).join(",") + "]");
  }
  if (url.pathname === "/api/stream") {
    res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache", Connection: "keep-alive" });
    const s = readState();
    if (s) send(res, "state", s);
    for (const line of lastEvents(40)) send(res, "event", line);
    clients.add(res);
    req.on("close", () => clients.delete(res));
    return;
  }
  res.writeHead(404);
  res.end();
}).listen(port, "127.0.0.1", () => console.log(`DCB web demo on http://127.0.0.1:${port}/ reading ${exportDir}`));
