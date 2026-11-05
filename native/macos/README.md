# Fixed macOS network service

The previous client called `osascript ... with administrator privileges` when recreating its TUN process. Reusing a process only within one GUI lifetime could not prevent another prompt after Quit. Lack of a Developer ID signature was not the cause of that launch logic.

Simple now uses an explicitly authorized, per-user launch daemon. First Connect installs a snapshot of the helper, sing-box, generated bridge configuration, local rules, and DNS lifecycle scripts in a root-owned directory. Normal GUI restarts and Connect/Disconnect communicate over a local Unix socket and do not invoke authorization. Installation fingerprints include the core, helper source version and immutable configuration/rules; interface-only releases do not replace this installation. Core/helper/configuration upgrades require a new deliberate authorization.

Security boundaries:

- The socket accepts only START, STOP and STATUS, never a path, configuration, PID or shell command. `getpeereid` must match the installing user. `LOCAL_PEERPID` supplies the owner; the request cannot select it. The client verifies that the server is root.
- Active sessions also match the owner's process birth time, preventing PID-reuse confusion. A different live process cannot take over or stop that session.
- Core and DNS commands use fixed root-owned files with restrictive modes. Local rule files are snapshotted and rewritten to installed paths; root does not parse mutable user rules on subsequent connections. A clean environment is used for the core.
- Disconnect and Quit stop the core and remove the temporary resolver. Owner death does the same. Helper restart cleans a recorded orphan before listening, then stays idle until explicit Connect. Idle helper presence does not mean VPN traffic is active.
- Nothing caches a password or modifies sudoers. The normal macOS administrator dialog performs installation authorization. This remains an ad-hoc-signed distribution; it is not Developer ID notarization or a signed SMAppService/XPC identity claim.

Validation: `tests/test_macos_helper.py` runs the actual helper unprivileged with an inert child and fake DNS actions. It covers reconnect, owner exit, reopening, core crash, helper crash, shutdown and malformed requests. It does not install a system daemon or exercise real TUN routes. Test path overrides are refused when running as root.

To remove a production installation, first Disconnect and Quit Owlpath. A Mac administrator can then run the following for the affected account's numeric UID (normally obtained using `id -u ACCOUNT_NAME`):

```sh
sudo launchctl bootout system/io.github.robintheai.worldscience.network.501
sudo rm /Library/LaunchDaemons/io.github.robintheai.worldscience.network.501.plist
sudo rm -r /Library/PrivilegedHelperTools/io.github.robintheai.worldscience.network-501
sudo rm -r /private/var/run/io.github.robintheai.worldscience.network-501
```

Replace 501 with that account's UID. Removing the helper does not delete the account or app settings; a future Connect asks to install it again. Do not remove the files while its launchd job is still running.
