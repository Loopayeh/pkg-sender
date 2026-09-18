"""Mock of pkg-receiver.elf file endpoints for sender-side testing."""
import json
import sys
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STORE = {}  # path -> bytearray


class H(BaseHTTPRequestHandler):
    server_version = "MockDPI/1.0"

    def log(self, *a):
        sys.stdout.write("MOCK %s\n" % (" ".join(str(x) for x in a)))
        sys.stdout.flush()

    def _q(self):
        u = urllib.parse.urlparse(self.path)
        return u.path, {k: v[0] for k, v in urllib.parse.parse_qs(u.query).items()}

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n > 0 else b""

    def _send(self, code, obj):
        b = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        path, q = self._q()
        self.log("GET", self.path)
        if path == "/api":
            self._send(200, {"status": "fail",
                             "error": "Unsupported method: use POST /api/install"})
        elif path == "/api/files/stat":
            p = q.get("path", "")
            if p in STORE:
                self._send(200, {"exists": True, "size": len(STORE[p])})
            else:
                self._send(200, {"exists": False, "size": 0})
        else:
            self._send(404, {"error": "unknown"})

    def do_POST(self):
        path, q = self._q()
        body = self._body()
        self.log("POST", self.path, "body_len=%d" % len(body))
        if path == "/api/files/mkdir":
            self._send(200, {"ok": True})
        elif path == "/api/files/write":
            p = q.get("path", "")
            off = int(q.get("offset", "0"))
            cur = STORE.get(p, bytearray())
            if len(cur) < off:
                cur.extend(b"\x00" * (off - len(cur)))
            cur[off:off + len(body)] = body
            STORE[p] = cur
            self._send(200, {"ok": True})
        elif path == "/api/files/done":
            try:
                j = json.loads(body.decode())
            except Exception:
                self._send(200, {"error": "bad json"})
                return
            p, want = j.get("path", ""), int(j.get("size", -1))
            if p in STORE and len(STORE[p]) == want:
                self._send(200, {"ok": True, "size": want})
            else:
                self._send(200, {"error": "size mismatch"})
        else:
            self._send(404, {"error": "unknown"})


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 12800
    srv = ThreadingHTTPServer(("127.0.0.1", port), H)
    print("mock on 12800", flush=True)
    srv.serve_forever()
