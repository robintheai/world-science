# Windows TUN acceptance

Run only in a disposable, elevated Windows VM with a fresh `V2RAYN_SIMPLE_DATA_DIR` for each case. This test changes real virtual-machine routes and DNS. Do not run on a user's desktop or CI host with other workloads.

Publish this project self-contained for `win-x64`, copy the release `bin/` directory beside `WorldScience.WindowsTests.exe`, and provide a SOCKS5 TCP relay at `10.0.2.2:18766` (QEMU's isolated host gateway). The relay must be able to reach the public connectivity/DNS endpoints. No production account or subscription is used.

Run with `--disposable-vm`; repeat with `--sing-box` and `--legacy-native` in fresh data directories. Each case exercises the real Simple controller, driver setup, tunnel health, system HTTPS, IPv6 TCP DNS, disconnect, and reconnect. The legacy option seeds the old native-Xray setting and checks that Simple normalizes it before connecting.

The process exits unsuccessfully on any failed assertion. Inspect stdout, stderr and the isolated data directory's logs; terminate the VM after acceptance. Windows ARM64 packages require separate hardware/runtime acceptance.
