#!/usr/bin/env python3
"""Header bridge: reuse pkg-viewer parsing for non-PKG images.

Usage:
    pkg_header.py <path> [--icon-out <file>]

Prints one JSON object to stdout:
    {"ok": true, "title": ..., "title_id": ..., "content_id": ...,
     "version": ..., "platform": ..., "description": ...,
     "format": "exfat|ffpfsc|ffpkg|folder|pkg",
     "icon_b64": "..." (omitted when --icon-out is used)}

Icon bytes are capped at 768KB (mirrors the C# library cache).
Exit code 0 on success, 1 with {"ok": false, "error": ...} on failure.
"""
import base64
import importlib.util
import json
import os
import sys

ICON_CAP = 768 * 1024


def load_pkgviewer():
    here = os.path.dirname(os.path.abspath(__file__))
    cand = os.path.normpath(os.path.join(here, "..", "..", "pkg-viewer", "pkgviewer.py"))
    if not os.path.isfile(cand):
        # Fallback: development layout next to this file.
        cand = os.path.join(here, "pkgviewer.py")
    spec = importlib.util.spec_from_file_location("pkgviewer_bridge", cand)
    if spec is None or spec.loader is None:
        raise RuntimeError("cannot load pkgviewer.py at " + cand)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def rows_get(rows, *names):
    try:
        for k, v in rows or []:
            for n in names:
                if k.strip().lower() == n.lower():
                    return v or ""
    except Exception:
        pass
    return ""


def main(argv):
    if len(argv) < 2:
        print(json.dumps({"ok": False, "error": "usage: pkg_header.py <path> [--icon-out <file>]"}))
        return 1
    path = argv[1]
    icon_out = None
    if "--icon-out" in argv:
        i = argv.index("--icon-out")
        if i + 1 < len(argv):
            icon_out = argv[i + 1]
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    if not os.path.exists(path):
        print(json.dumps({"ok": False, "error": "not found: " + path}))
        return 1
    try:
        pv = load_pkgviewer()
    except Exception as ex:
        print(json.dumps({"ok": False, "error": "pkgviewer load failed: %s" % ex}))
        return 1
    try:
        r = pv.parse_pkg(path)
    except Exception as ex:
        print(json.dumps({"ok": False, "error": "parse failed: %s" % ex}))
        return 1
    if not isinstance(r, dict) or "error" in r:
        err = r.get("error", "unknown parse error") if isinstance(r, dict) else "bad result"
        print(json.dumps({"ok": False, "error": str(err)}))
        return 1

    low = path.lower()
    if os.path.isdir(path):
        fmt = "folder"
    elif low.endswith(".ffpfsc"):
        fmt = "ffpfsc"
    elif low.endswith(".ffpkg"):
        fmt = "ffpkg"
    elif low.endswith(".exfat"):
        fmt = "exfat"
    elif low.endswith(".pkg"):
        fmt = "pkg"
    else:
        fmt = os.path.splitext(low)[1].lstrip(".") or "file"

    rows = r.get("rows") or []
    meta = r.get("meta") or {}
    title = r.get("title") or os.path.basename(path)
    title_id = rows_get(rows, "Title ID") or str(meta.get("titleId", "") or "")
    content_id = rows_get(rows, "Content ID") or str(meta.get("contentId", "") or "")
    version = rows_get(rows, "Content Ver") or str(meta.get("contentVersion", "") or "")
    platform = rows_get(rows, "Platform") or "PS5"

    # Icon: prefer icon0.png entry bytes.
    icon_b64 = ""
    try:
        icon_entry = None
        for e in r.get("entries") or []:
            nm = str(e.get("name", ""))
            if nm.lower() == "icon0.png":
                icon_entry = e
                break
        if icon_entry is not None:
            data = bytes(icon_entry.get("cached") or b"")
            if not data:
                lp = icon_entry.get("local_path")
                if lp and os.path.isfile(lp):
                    with open(lp, "rb") as f:
                        data = f.read(ICON_CAP + 16)
                else:
                    try:
                        data = bytes(pv.read_entry_bytes(
                            path, icon_entry.get("abs_off"),
                            int(icon_entry.get("size") or 0), limit=ICON_CAP + 16) or b"")
                    except Exception:
                        data = b""
            if data[:8] == b"\x89PNG\r\n\x1a\n" and 0 < len(data) <= ICON_CAP + 16:
                data = data[:ICON_CAP]
                if icon_out:
                    with open(icon_out, "wb") as f:
                        f.write(data)
                else:
                    icon_b64 = base64.b64encode(data).decode("ascii")
    except Exception:
        icon_b64 = ""

    out = {
        "ok": True,
        "title": title,
        "title_id": title_id,
        "content_id": content_id,
        "version": version,
        "platform": platform,
        "description": "[PS5 %s] %s" % (fmt, title),
        "format": fmt,
    }
    if icon_b64:
        out["icon_b64"] = icon_b64
    print(json.dumps(out, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
