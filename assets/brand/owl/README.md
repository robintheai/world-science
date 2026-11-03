# World Science owl

Vector sources for the World Science owl (moon silhouette).

- `owl-b-live.svg`: one file with all connection states. Switch with the root class `state-off` (day, asleep), `state-busy` (dusk, looking for signal) or `state-on` (night, glowing eyes). Add `waking` for about 2 s when switching to `state-on`, and `dozing` when switching to `state-off`. Motion stops under `prefers-reduced-motion`.
- `owl-b-on.svg`, `owl-b-busy.svg`, `owl-b-off.svg`: still pictures of each state. The app icon (`v2rayN/Simple/Assets/simple.*`) is the `on` state.
- `owl-b-small.svg`: simplified art for 16–64 px icons.
- `menubar/`: macOS template icons (22 × 16 pt, rendered at 44 × 32 px into `v2rayN/Simple/Assets`). `status` = disconnected (asleep), `status-still` = connected without traffic, `status-run-0..11` = one wing beat while traffic flows.

The connect button draws the same owl in code (`v2rayN/Simple/Controls/OwlDial.cs`); the Android icons are `mobile/android/app/src/main/res/drawable/logo.xml` and `notify.xml`.
