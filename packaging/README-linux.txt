PKG Sender (Linux) — send PS4/PS5 .pkg files over LAN
=====================================================

Run:
  chmod +x PkgSender   (only needed after manual download)
  ./PkgSender

The folder should contain pkg-receiver.elf — send that to your jailbroken
PS5 first (the app can copy it for you), then PKG Sender discovers the
console by itself. Full guide: open the "?" / Guide button in the app.

Firewall:
  The app serves files on TCP 9898 and listens for console beacons on
  UDP 12801/12802. If pushes start but the console download never does,
  allow the inbound port, e.g.:
    sudo ufw allow 9898/tcp
    sudo firewall-cmd --permanent --add-port=9898/tcp && sudo firewall-cmd --reload
  (No firewall is enabled by default on most desktop distros.)

Optional Python bridge (folder/fpkg header parsing):
  pip install mkpfs
  The app uses python3 automatically when mkpfs is importable.

Self-update:
  In-app updates replace this binary in place (the old one is kept as
  PkgSender.old and removed on next start). Installed via the official
  .deb or an AppImage? Update through your package manager or download
  the new release instead.
