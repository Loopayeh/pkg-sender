# PKG Sender — PS5 / PS4

**PKG Sender** installs PlayStation packages over LAN: pick games on your PC, they queue up and install on the console. No USB juggling, no manual IP typing.

## Download

Get `PkgSender-Setup-X.Y.Z.exe` from [Releases](../../releases) — self-contained, no .NET needed, no admin needed.

## Setup

1. Jailbreak your PS5 and send `pkg-receiver.elf` (ships inside the install folder). Wait for the console toast *listening on port 12800*. (Receiver tested on PS5 firmware 6.02.)
2. Put the console on the same network as this PC (Wi-Fi or LAN). The console itself needs no internet.
3. Open PKG Sender — it finds the console by itself (receiver beacon first, LAN sweep as fallback). If the console got a new DHCP address, it asks: *switch to it?*
4. Press **Scan drives…** (or **+ Add folder**), select games, press **Send PKG**.

On first launch the About window opens (support links live there).

## PS4

Works with a jailbroken PS4 running a compatible package receiver (listening on port 12800): Test should go green and Send PKG works. Tick **PS4 console** above the queue — PS4 installs go strictly one-by-one (PS5 queues natively).

## Features

- Zero-config networking: auto PC address, console auto-detect with switch prompt, live status dot, re-scan (↻) button
- Library: cover art, Title ID, version, size; filter PS5/PS4, sort name/size, search with in-bar clear (✕)
- Family linking: updates and DLCs stay glued to their base game (exact Title ID — regions stay separate); 🔗 chip shows the family, click to filter, click again to go back
- Send queue with per-file progress, resume, stop, clear-done (orphaned rows included)
- Self-updating: silent check at startup, footer button lights up on new release (off switch in About for offline PCs)
- Guide window with setup + troubleshooting, first-run About

## Support

If you enjoy what I build and want to support my work, you can donate — every bit means a lot. 💙

- USDT (BEP-20): `0x839a30D52Ef7D2b53e818b9931efd7FE6F472e50`
  ([send via TrustWallet](https://link.trustwallet.com/send?coin=20000714&address=0x839a30D52Ef7D2b53e818b9931efd7FE6F472e50&token_id=0x55d398326f99059fF775485246999027B3197955))
- More: [loopayeh.github.io](https://loopayeh.github.io/)

## Troubleshooting

- **● No receiver (red)** — the elf isn't running on the console. Send it again.
- **○ No network (gray)** — this PC has no active LAN/Wi-Fi.
- **Push goes through but download never starts** — allow inbound TCP port 9898 in Windows Firewall (the installer adds this rule plus UDP 12801 for beacons).
- **Console IP keeps changing (DHCP)** — reopen the app or hit ↻; it offers the new address.

## Build from source

Needs .NET 8 SDK (+ Inno Setup 6 for the installer):

```bat
Build-Release.bat
```

This publishes a self-contained single-file build to `dist\` and, if `iscc` is available, produces `PkgSender-Setup-X.Y.Z.exe`. The PS5 receiver rebuilds with the ps5-payload-sdk toolchain:

```sh
cd payload && make PS5_PAYLOAD_SDK=/path/to/ps5-payload-sdk
```

## How updates work

The app checks GitHub releases for a `PkgSender-Setup-*.exe` asset newer than its own version. **Download + Install** fetches it to a temp dir, launches it silent, and exits so Setup can overwrite the running app.
