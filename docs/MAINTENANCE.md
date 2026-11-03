# Maintaining World Science

## Upstream

`v2rayN/ServiceLib` is v2rayN 7.25.6 (`57f17d5939e88c8e643ab49b90869d637765e75a`) with these additions, all needed by the
simplified client:

- macOS TUN through a fixed privileged network helper (`native/macos/network-helper.c`, `Manager/MacPersistentTun.cs`,
  `MacTunSupervisor.cs`) and a temporary supplemental DNS resolver (`MacTunDns.cs`) that is removed on exit.
- IPv4-only node dialing with IPv6 captured and rejected inside the tunnel (`CoreConfigContext.IPv4Only`).
- App identity: data folder `World-Science`, `Global.AppName`, overridable with `WORLD_SCIENCE_DATA_DIR` for tests.

Everything else lives in `v2rayN/Simple`. When updating upstream, re-check outbound mapping, routing order, sniffing,
TUN domain detection and the macOS start/stop path (connect, disconnect, quit, crash) before release, not only the UI.

Core binaries come from `2dust/v2rayN-core-bin` at the commit in `scripts/build.py`; update `scripts/core-checksums.json`
together with that commit.

## Release

1. Bump `<Version>` in `v2rayN/Simple/Simple.csproj` and `versionCode`/`versionName` in `mobile/android/app/build.gradle`.
2. Commit, then build every package from that commit (`BUILD-INFO.json` records the source commit):
   `python3 scripts/build.py --rid <osx-arm64|osx-x64|win-x64|win-arm64> --version X.Y.Z --output dist`,
   plus the Android APK (see `mobile/README.md`).
3. Run the smoke tests, the integration test on macOS, the Go tests and the Android instrumented tests.
4. Write `SHA256SUMS` for the packages, create the GitHub release `vX.Y.Z` with all packages and `SHA256SUMS`, then
   mirror the same files to `https://ai.z5a.com/clients/world-science/releases/vX.Y.Z/` and update its `latest.json`
   (same shape as the GitHub release API).

The desktop updater reads the mirror first and falls back to the GitHub API. It accepts packages only from
`github.com/robintheai/world-science/releases/download/` or the mirror's `/clients/world-science/releases/` folder,
verifies them against `SHA256SUMS`, and backs up settings before installing.

Mac packages are ad-hoc signed, not notarized. Android releases must always be signed with the same key.
