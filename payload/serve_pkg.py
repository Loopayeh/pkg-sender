"""serve_pkg.py - minimal range-supporting file server for PS5 PKG installs.
Sony's downloader requests byte ranges (206 Partial Content). Plain
`python -m http.server` ignores Range -> console aborts -> 0x80B211C8.

Usage (in the folder with the .pkg files):
    C:\\Python314\\python.exe D:\\OpenCode\\loopayeh-dpi\\serve_pkg.py [port]
Then install http://<PC_IP>:<port>/<file.pkg> via LoopDPI web page.
"""
import os
import sys
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
CHUNK = 1024 * 1024


class Handler(BaseHTTPRequestHandler):
    server_version = "LoopDPI/1.0"

    def log_message(self, fmt, *args):
        sys.stdout.write("%s - %s\n" % (self.client_address[0], fmt % args))
        sys.stdout.flush()

    def _send_file(self, path, start, end, total, partial):
        try:
            f = open(path, "rb")
        except OSError:
            self.send_error(404, "not found")
            return
        f.seek(start)
        length = end - start + 1
        if partial:
            self.send_response(206)
            self.send_header("Content-Range",
                             "bytes %d-%d/%d" % (start, end, total))
        else:
            self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        self.end_headers()
        try:
            while length > 0:
                buf = f.read(min(CHUNK, length))
                if not buf:
                    break
                self.wfile.write(buf)
                length -= len(buf)
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            f.close()

    def do_GET(self):
        # strip Sony's appended query (&product=..&serverIpAddr=..&r=..)
        raw = self.path.split("?", 1)[0]
        name = urllib.parse.unquote(raw.lstrip("/"))
        # no subdirs, no escapes
        name = os.path.basename(name)
        if not name or not os.path.isfile(name):
            self.send_error(404, "not found: %s" % name)
            return
        total = os.path.getsize(name)
        start, end = 0, total - 1
        partial = False
        rng = self.headers.get("Range")
        if rng and rng.startswith("bytes="):
            try:
                spec = rng[6:].split("-", 1)
                start = int(spec[0]) if spec[0] else 0
                if len(spec) > 1 and spec[1]:
                    end = min(int(spec[1]), total - 1)
                partial = True
            except ValueError:
                pass
        if start >= total:
            self.send_error(416, "range past EOF")
            return
        self._send_file(name, start, end, total, partial)

    def do_HEAD(self):
        name = os.path.basename(
            urllib.parse.unquote(self.path.split("?", 1)[0].lstrip("/")))
        if not name or not os.path.isfile(name):
            self.send_error(404)
            return
        total = os.path.getsize(name)
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(total))
        self.send_header("Accept-Ranges", "bytes")
        self.end_headers()


if __name__ == "__main__":
    srv = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    print("LoopDPI file server on port %d, dir: %s" % (PORT, os.getcwd()),
          flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
