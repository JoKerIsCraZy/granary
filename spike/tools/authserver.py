"""Tiny media server that mimics Silo's header-authenticated media routes.

Usage: python -I authserver.py <media_root> <tokens_file> <log_file> [port]

- Every request must carry `Authorization: Bearer <token>`; valid tokens are re-read
  from <tokens_file> (one per line) on every request, so a test can "expire" a token.
- Supports HEAD and single-range GET (206/416), like Silo's direct-play route.
- Logs one JSON line per request: method, path, range, auth result, user agent.
"""
import json
import os
import re
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlparse

ROOT, TOKENS, LOG = sys.argv[1], sys.argv[2], sys.argv[3]
PORT = int(sys.argv[4]) if len(sys.argv) > 4 else 8765
MIME = {".mkv": "video/x-matroska", ".mp4": "video/mp4", ".m3u8": "application/vnd.apple.mpegurl",
        ".ts": "video/mp2t", ".vtt": "text/vtt", ".srt": "application/x-subrip"}


def valid_tokens():
    try:
        with open(TOKENS, encoding="utf-8") as f:
            return {line.strip() for line in f if line.strip()}
    except OSError:
        return set()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _log(self, status, auth):
        rec = {"t": round(time.time(), 3), "method": self.command, "path": self.path, "status": status,
               "auth": auth, "range": self.headers.get("Range"), "ua": self.headers.get("User-Agent")}
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")

    def _auth(self):
        header = self.headers.get("Authorization", "")
        if not header:
            return "missing"
        m = re.fullmatch(r"Bearer (\S+)", header)
        if not m:
            return "malformed"
        return "ok" if m.group(1) in valid_tokens() else "invalid"

    def _problem(self, status, code):
        body = json.dumps({"type": f"https://siloserver.org/docs/api/v2/problems/{code}", "title": code,
                           "status": status, "detail": code, "instance": "urn:silo:request:test"}).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/problem+json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _serve(self, head):
        auth = self._auth()
        if auth != "ok":
            self._log(401, auth)
            return self._problem(401, "invalid_token" if auth == "invalid" else "authentication_required")
        rel = unquote(urlparse(self.path).path).lstrip("/")
        path = os.path.normpath(os.path.join(ROOT, rel))
        if not path.startswith(os.path.normpath(ROOT)) or not os.path.isfile(path):
            self._log(404, auth)
            return self._problem(404, "not_found")
        size = os.path.getsize(path)
        start, end, status = 0, size - 1, 200
        rng = self.headers.get("Range")
        if rng:
            m = re.fullmatch(r"bytes=(\d*)-(\d*)", rng.strip())
            if not m or (m.group(1) == "" and m.group(2) == ""):
                self._log(416, auth)
                return self._problem(416, "range_not_satisfiable")
            if m.group(1) == "":
                start = max(0, size - int(m.group(2)))
            else:
                start = int(m.group(1))
                end = min(int(m.group(2)), size - 1) if m.group(2) else size - 1
            if start >= size or start > end:
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{size}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                self._log(416, auth)
                return
            status = 206
        length = end - start + 1
        self.send_response(status)
        self.send_header("Content-Type", MIME.get(os.path.splitext(path)[1], "application/octet-stream"))
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(length))
        if status == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        self._log(status, auth)
        if head:
            return
        with open(path, "rb") as f:
            f.seek(start)
            remaining = length
            try:
                while remaining > 0:
                    chunk = f.read(min(256 * 1024, remaining))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    remaining -= len(chunk)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass

    def do_GET(self):
        self._serve(head=False)

    def do_HEAD(self):
        self._serve(head=True)


if __name__ == "__main__":
    ThreadingHTTPServer.daemon_threads = True
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"serving {ROOT} on http://127.0.0.1:{PORT}", flush=True)
    server.serve_forever()
