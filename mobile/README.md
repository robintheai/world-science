# World Science for Android

[中文安装教程](../INSTALL.md#android)

A native Java UI (`android/app`) over a Go core (`core`) that turns subscription share links into a sing-box
configuration and runs it through Android's `VpnService`. The core pins sing-box 1.14.3 and is built with gomobile
into `android/app/libs/mobilecore.aar` (not committed).

Subscriptions are stored in app-private storage encrypted with an Android Keystore key. Subscription URLs and node
credentials must never enter logs or build artifacts. Subscription refreshes use the underlying network, so a dead
node never blocks an update.

## Build

Requirements: JDK 17, Android SDK 35 with NDK 28, Go 1.25+, gomobile/gobind v0.1.13.

```bash
cd mobile/core
gomobile bind -ldflags="-checklinkname=0 -s -w" -target=android/arm64,android/amd64 -androidapi=26 -javapkg=io.github.robintheai.worldscience -tags=with_gvisor,with_quic,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0,with_low_memory -o ../android/app/libs/mobilecore.aar .
```

```bash
cd mobile/android && gradle assembleRelease
```

Sign the unsigned release APK with `mobile/scripts/sign-android.py`. It creates a signing key outside the repository on
first use; keep that key and its password backed up, because later updates must be signed with the same key. Never
commit the key, its password or a signed artifact.

The default APK contains ARM64 and x86_64 libraries; `-Pabis=arm64-v8a` builds an ARM64-only APK.

## Tests

```bash
cd mobile/core && go test -tags with_gvisor,with_quic,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0,with_low_memory -ldflags=-checklinkname=0 ./...
```

Instrumented tests (`android/app/src/linkTest`) run a local VLESS fixture (`scripts/vless-fixture.py`) and check the
VPN lifecycle, recovery after network changes and encrypted subscription storage:

```bash
cd mobile/android && gradle connectedAndroidTest
```
