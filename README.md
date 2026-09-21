# PS4 PS5 PKG Sender

**PKG Sender** installs PlayStation games over LAN from your PC: pick games, they queue up and install on the console. No USB juggling, no manual IP typing. PS5 **PKG** games install fine, no FPKG patching needed on your side. Disc images (`.exfat` / `.ffpfsc` / `.ffpkg`) copy straight to `/data/homebrew`.

## Download

Get `PkgSender-Setup-X.Y.Z.exe` from [Releases](../../releases) — self-contained, no .NET needed, no admin needed. The matching `pkg-receiver.elf` is attached to the same release.

On first launch the About window opens (support links live there).

---

## Tutorial — PS5

### 1. Start the receiver on the console

1. Jailbreak your PS5 and send `pkg-receiver.elf` (it ships inside the PC install folder).
2. Wait for the console toast *listening on port 12800*.
3. On first run the receiver also installs a **pkg remote installer** shortcut on the PS5 home screen (Media category, `PKGS12800`).

### 2. Connect PC and console

Put the console on the same network as this PC (Wi-Fi or LAN). The console itself needs no internet. See [LAN connection](#lan-connection) below for the direct-cable option.

Open PKG Sender — it finds the console by itself (receiver beacon first, LAN sweep as fallback). If the console got a new DHCP address, it asks: *switch to it?* Press **Test** to verify (status dot goes green).

### 3. Install a PKG game

1. Press **Scan drives…** (or **+ Add folder**, or just drag files/folders onto the window), select games, press **Send PKG**.
2. Updates and DLCs stay glued to their base game (exact Title ID — regions stay separate). Click the 🔗 chip on a card to show only that family.
3. Watch the send queue: per-file progress, pause, reorder, retry. Tick **PS4 console** only for PS4 targets (see below) — PS5 queues natively.

### 4. Install from the console browser (no PC walking)

1. In PKG Sender press **Share to console**. The status bar shows the catalog URL (`http://<pc-ip>:9898/catalog`).
2. On the PS5, open the **pkg remote installer** home-screen shortcut (or browse to `http://<console-ip>:12800/`).
3. **Games** tab: base games with covers; click one for details, install updates/DLCs individually or everything at once with **Install all**.
4. **Images** tab: disc images with a **Copy to homebrew** button each. Copy progress (percent + speed) shows above the list with **Pause** / **Cancel copy** — works even for copies started from the PC app.
5. The header shows `page X • receiver Y` — if they differ, resend the newest ELF.

### 5. Copy a disc image (PC side)

Select `.exfat` / `.ffpkg` / `.ffpfsc` rows and press **Copy images** — they land in `/data/homebrew`. Each copy gets a queue row with live progress; ⏸ pauses the receiver, ✕ cancels it for real (partial file stays for resume).

---

## Tutorial — PS4

### 1. Prepare the console

Jailbreak the PS4 and start **one** of these (the app auto-detects in this order):

1. **Remote Package Installer (RPI)** — serves its API on port `12800`.
2. **GoldHEN with Payload Server enabled** — ports `9090` / `9021` / `9020`.

### 2. Connect and test

Same network as the PC (see [LAN connection](#lan-connection)). In PKG Sender type the console IP and press **Test** — it reports which mode it found (`RPI` or `GoldHEN`).

### 3. Install a PKG game

1. Scan / add folder / drag & drop, select the game, press **Send PKG**.
2. Tick **PS4 console** above the queue — PS4 installs go strictly one-by-one (a PS5 queues natively, a PS4 does not).
3. What happens per mode:
   - **RPI**: the PC sends the file URL to `http://<ps4>:12800/api/install`; the PS4 downloads and installs it itself.
   - **GoldHEN**: payload injection over the binloader ports, then install.

No FTP is involved on either console — all transfers are plain HTTP from the PC's file server (port `9898`).

---

## LAN connection

### Method 1 — Through a router

The easiest option — connect both your PC and console to the same router.

```
PC ─────┐
        ├── Router
PS5/PS4 ┘
```

You don't need to configure IP addresses manually.

1. Connect your PC to the router using Ethernet or Wi-Fi.
2. Connect your PS5/PS4 to the same router.
3. Start the receiver (PS5) / RPI-HEN (PS4) on the console.
4. Open PKG Sender — it discovers the console on your local network automatically.

### Method 2 — Direct Ethernet connection

PC straight to PS5/PS4 with an Ethernet cable, no router.

```
PC ───────── Ethernet ───────── PS5/PS4
192.168.10.1                  192.168.10.2
```

Because there is no router providing DHCP, you must manually assign an IP address to both devices.

**1. Set the PC IP address.** On Windows: Settings → Network & Internet → Ethernet → IP assignment → Edit. Select Manual, enable IPv4, and enter:

- IP address: `192.168.10.1`
- Subnet mask: `255.255.255.0`
- Gateway: leave empty
- DNS: leave empty

**2. Set the console IP address.** Configure the console's Ethernet connection with:

- IP address: `192.168.10.2`
- Subnet mask: `255.255.255.0`
- Gateway: leave empty
- DNS: leave empty

The important part is that both devices are on the same subnet (`255.255.255.0`).

**3. Start the receiver** on the console, then launch PKG Sender on your PC. In the app pick the PC address from the PC box, type the console IP, press **Test**.

> ⚠ Do NOT leave a direct cable on automatic IP assignment. Windows or the console may fall back to a `169.254.x.x` address when no DHCP server is available — PKG Sender ignores these automatic link-local addresses on purpose. If the cable is plugged in but nothing is found, this is almost always the cause.

---

## Features

- Zero-config networking: auto PC address, console auto-detect with switch prompt, live status dot, re-scan (↻ Detect) button
- Library: cover art, Title ID, version, size; filter PS5/PS4, sort name/size, search with in-bar clear (✕)
- Add games three ways: **Scan drives…**, **+ Add folder**, or **drag & drop** files/folders onto the window
- Family linking: updates and DLCs stay glued to their base game (exact Title ID — regions stay separate); 🔗 chip shows the family, click to filter, click again to go back
- Send queue with per-file progress, pause, reorder, retry, clear-done (orphaned rows included)
- Image copies with live progress, receiver-side pause, and real cancel (partial kept for resume)
- Console browser page (PS5): logo header, 5-column grid, platform badges, details modal, install-all queue, copy progress with pause/cancel
- Self-updating: silent check at startup, footer button lights up on new release (off switch in About for offline PCs)
- Guide window with setup + troubleshooting, first-run About

---

## Receiver API (PS5)

The receiver listens on `http://<console-ip>:12800`. File paths are jailed
under `/data/homebrew`. This API is not stable and may change between versions.

| Method | Path | Input | Reply |
| ------ | ---- | ----- | ----- |
| GET | `/api` | — | probe (online check, no action) |
| GET | `/api/status` | — | `{"busy":bool,"active":N,"pull":bool,"pullName","pullGot","pullWant","pullPaused":bool}` |
| GET | `/api/pc` | — | `{"pc":"1.2.3.4","age":N}` (last PC announce, `age` -1 = never) |
| GET | `/api/version` | — | `{"build":"..."}` (compare with the page header) |
| GET | `/api/space` | — | `{"free":N,"total":N}` (`/data` bytes) |
| GET | `/logo.png`, `/favicon.ico` | — | sender logo PNG |
| POST | `/api/install` | `{"packages":["<url>"],"name":"...","icon_url":"..."}` (`name`/`icon_url` optional) | `{"status":"success"}` or `{"status":"fail",...}` |
| GET | `/install?url=` | PKG URL as query arg (+`name`, `+icon`) | starts install, plain-text reply |
| GET | `/api/files/stat?path=` | remote path | `{"exists":bool,"size":N}` |
| POST | `/api/files/pull` | `{"url":"http://pc:9898/pkg/id","path":"/data/homebrew/f.pkg","mode":"overwrite"/"resume"}` | `{"ok":true,"started":true}` + console toast on finish |
| POST | `/api/pull/pause` | `{"paused":1/0}` | `{"ok":true,"paused":bool}` |
| POST | `/api/pull/cancel` | `{}` | `{"ok":true,"cancelled":true}` (partial stays for resume) |

File-explorer endpoints (`/api/fs/*`, Files tab) are temporarily disabled in the receiver.

Discovery: the receiver broadcasts `PKGSENDER v1` to UDP `255.255.255.255:12801`
every 3 seconds.

## Console library (pkg remote installer, PS5)

Browse and install the scanned PC library from the console's own browser —
no need to walk back to the PC:

1. In PKG Sender, scan your folders, then press **Share to console**. The status
   bar shows the catalog URL (`http://<pc-ip>:9898/catalog`). While shared,
   the PC also broadcasts `PKGSENDER-PC <pc-ip>:9898` to UDP `255.255.255.255:12802`
   every 3 seconds, so the console finds it automatically (no manual IP entry).
2. On first run the receiver installs a **pkg remote installer** shortcut on
   the PS5 home screen (Media category, `PKGS12800`). Open it — or browse to
   `http://<console-ip>:12800/` manually.
3. **Games tab**: base games only (alphabetical, with covers, sizes and IDs);
   click a base game for details and its updates/DLCs, each with its own Install
   button, or **Install all** for the whole family. Search filters by title or
   Title ID; chips filter PS5/PS4.
4. The **Images** tab shows disc images (exfat/ffpfsc/ffpkg) with a
   **Copy to homebrew** button each, plus live progress with Pause/Cancel.

Published endpoints on the PC file server (`:9898`, CORS-open):

| Method | Path | Reply |
| ------ | ---- | ----- |
| GET | `/catalog` | `[{"id","title","titleId","version","size","sizeText","role","familyKey","platform","format","file","hasIcon"}]` (`role` = Game/Patch/DLC/Image) |
| GET | `/icon/{id}` | cover PNG (`image/png`) |
| GET | `/pkg/{id}` | file bytes (range-capable, same as pushes use) |

---

## Credits

- [ps5-web-file-manager](https://github.com/owendswang/ps5-web-file-manager)
  (GPL-3.0) — the home-screen web-shortcut launcher idea; reimplemented here,
  no code copied.
- [seregonwar/zftpd](https://github.com/seregonwar/zftpd) (MIT) — pointed at
  PS5 TCP socket-buffer tuning as the fix for slow bulk transfers; our
  pull-downloader buffering was rewritten from scratch, no code copied.

## Screenshots

![PKG Sender main view](docs/screenshot-main.png)

![PKG Sender compact view](docs/screenshot-compact.png)

## Support

If you enjoy what I build and want to support my work, you can donate — every bit means a lot. 💙

- USDT (BEP-20): `0x839a30D52Ef7D2b53e818b9931efd7FE6F472e50`
  ([send via TrustWallet](https://link.trustwallet.com/send?coin=20000714&address=0x839a30D52Ef7D2b53e818b9931efd7FE6F472e50&token_id=0x55d398326f99059fF775485246999027B3197955))
- More: [loopayeh.github.io](https://loopayeh.github.io/)

## Troubleshooting

- **● No receiver (red)** — the elf isn't running on the console. Send it again. On the console page, `page X • receiver Y` mismatch means the same thing.
- **○ No network (gray)** — this PC has no active LAN/Wi-Fi.
- **Push goes through but download never starts** — allow inbound TCP port 9898 in Windows Firewall (the installer adds this rule plus UDP 12801 for beacons).
- **Console IP keeps changing (DHCP)** — reopen the app or hit Detect; it offers the new address.
- **Image cards without covers** — the header bridge needs `pkgviewer.py` next to the installed app (ships since 1.2.5) plus a Python with `mkpfs`; rescan after installing.

## Build from source

Needs .NET 8 SDK (+ Inno Setup 6 for the installer):

```bat
Build-Release.bat
```

This publishes a self-contained single-file build to `dist\`, copies in `pkg-receiver.elf` + `pkgviewer.py`, and, if `iscc` is available, produces `PkgSender-Setup-X.Y.Z.exe`. The PS5 receiver rebuilds with the ps5-payload-sdk toolchain:

```sh
cd payload && make PS5_PAYLOAD_SDK=/path/to/ps5-payload-sdk
```

## How updates work

The app checks GitHub releases for a `PkgSender-Setup-*.exe` asset newer than its own version. **Download + Install** fetches it to a temp dir, launches it silent, and exits so Setup can overwrite the running app.
