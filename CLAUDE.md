# AR_Buildings

Unity 6 (6000.5.7f1) ARCore Geospatial app that overlays a 3D building model onto the real
building at a surveyed coordinate. URP, AR Foundation 6.5, ARCore Extensions 1.54, glTFast
6.19, Cesium for Unity 1.25. Android is the only target that has ever been built.

Site visits are the most expensive thing in this project. A desk simulator against Google's
3D tiles was built to avoid them and then removed, because Google has no usable reconstruction
of this building — see [The desk simulator](#the-desk-simulator--built-2026-08-22-and-removed-the-same-day),
which is kept as a record so it is not attempted twice. **The phone is the only thing that can
answer whether the model lands on the building**, so the tooling now aims at making a tripod
session cheap: see [Driving it from the laptop](#driving-it-from-the-laptop--remotecontrol).

Working branch `geospatial-bringup`. `main` is still the initial commit.

---

## Running it

Test device: Samsung Galaxy A35 (SM-A356E, serial `RFCXA03ERAV`), package
`com.pavel.arbuildings`. adb ships with Unity, not on PATH:

```
"C:/Program Files/Unity/Hub/Editor/6000.5.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
```

Diagnosing on device beats theorising, every single time this project has gone wrong:

```bash
adb logcat -d -s Unity:V | tail -40         # app logs: [Sites] [Loader] [Preview] [Adjust] [Geospatial]
adb logcat -d | grep -i "native"            # ARCore's own errors live under tag `native`, NOT Unity
adb exec-out screencap -p > shot.png        # then read the image; the HUD shows most state
```

**Compile check without opening Unity** (seconds, catches all C# errors):

```bash
dotnet build Assembly-CSharp.csproj -v q --nologo 2>&1 | grep -E "error|Build succeeded"
```

A brand-new `.cs` file is missing from the generated `.csproj` until Unity imports it, so
that build will report "type not found" for it. Add a `<Compile Include=...>` line manually
to verify, or ignore that one error. The csproj is gitignored and Unity regenerates it.

### Driving it from the laptop — `RemoteControl`

The phone goes on a tripod pointed at the facade and is not touched again: reaching past it to
press a HUD button moves the camera, and the camera is the one thing that must not move.
`RemoteControl` polls a text file four times a second, so every adjustment is a push.

```bash
DEV=/sdcard/Android/data/com.pavel.arbuildings/files
printf 'rot +2.5\neast -0.4\ncapture\n' > cmd.txt
adb push cmd.txt $DEV/command.txt          # applied within ~0.25 s
adb pull $DEV/remote_result.txt            # what it did, plus full state
adb pull $DEV/state.txt                    # full state alone, rewritten every second
```

One command per line; `#` comments and blank lines ignored. **A leading `+` or `-` means
relative**, anything else is absolute — nudging is what you do forty times a visit and it
should not require knowing the current value first.

| | |
|---|---|
| `rot` `scale` `scalev` `east` `north` `up` | the five adjustments, absolute or ± relative |
| `save` `reset` `clear` | save (bakes coordinates when the fix is good), reset all, delete the saved entry |
| `reload` | re-read `buildings.json` — pair it with pushing a new one |
| `preview on\|off` `occlude on\|off` `cut on\|off` `mesh on\|off` `sun on\|off` `aspect on\|off` | the HUD toggles |
| `capture` `recenter` `state` | screenshot + dump, recentre preview, force a state write |

The command file is deleted once handled, so pushing the same file again re-runs it. Bare
`occlude` with no argument means on.

This does not replace a rebuild — it removes the need for one for anything that is a number or
a toggle, which on past visits has been nearly everything.

**Reading what the Editor did, without the Editor.** Unity mirrors every `Debug.Log` into
`Logs/Editor.log`, which makes a Play session as diagnosable as a device run — the same habit
that makes `adb logcat` the first move on the phone:

```bash
grep -E "\[Sim\]|\[Geospatial\]|\[Sites\]|\[Loader\]" Logs/Editor.log | tail -40
grep -nE "error CS[0-9]+" Logs/Editor.log | tail             # compile failures
```

The Editor only imports changed files when its window gets focus, so a script edited from
outside does nothing until you click into Unity.

---

## Architecture

Placement hierarchy, built at runtime by `GeospatialController.BuildHierarchy`:

```
BuildingAnchor / PreviewRoot / DebugAnchor   the thing we anchor to; ARCore owns the real one
└ NudgeRoot        AlignmentNudge writes offset + heading + scale here
  ├ AlignmentRoot  BuildingPlacement applies modelFrontOffsetDeg and originOffsetLocal
  │ └ [GLB]        BuildingLoader normalises origin and scale here
  └ ShadowGround   preview only: a quad so the model has something to cast onto
```

| script | job |
|---|---|
| `GeospatialController` | site load → localization gate → terrain anchor → hierarchy. Owns preview mode, floor placement, coordinate baking |
| `SiteCatalog` | reads `buildings.json` — **device copy first, APK second** — and overrides the inspector |
| `BuildingPlacement` | heading maths. Footprint mode derives heading from two coordinates; anchors at their MIDPOINT |
| `BuildingLoader` | glTFast load, sizing (`FixedScale` / `TargetHeight` / `FootprintWidth`), recentring, front-face alignment |
| `AlignmentNudge` | the five adjustment values + save/load to `adjustments.json` |
| `DebugHud` | IMGUI instrument panel. No Canvas, no wiring, can't be broken by scene edits |
| `LightingController` | sun position, light estimation, north alignment, forced daylight |
| `StreetscapeShadowSetup` | streetscape geometry → occluder / debug materials, occluder cutout, master occlusion switch |
| `DebugCapture` | `capture` button: screenshot + full state dump to `persistentDataPath/captures` |
| `AdaptiveQuality` | frame-time tiering, switches URP asset between Tier A/B/C |
| `RemoteControl` | polls `command.txt` so the app can be nudged over adb without touching the phone |

### Site configuration — edit the file, not the inspector

`buildings.json` is read at startup and **overrides inspector values**; placement waits for
it. The only inspector field that matters is `Site Id` on `GeospatialController`.

**Two copies, device wins.** `persistentDataPath/buildings.json` is used if present,
otherwise the APK's `StreamingAssets` copy. That makes a coordinate fix a file push instead
of a rebuild:

```bash
adb push Assets/StreamingAssets/buildings.json \
    /sdcard/Android/data/com.pavel.arbuildings/files/buildings.json
```

…then press **`reload`** in the HUD: re-reads, re-applies, resolves a fresh anchor. The HUD
shows `cfg:device` or `cfg:apk` so there is never doubt which copy is live.

Fields beyond the obvious: `sizeMode`, `footprintAxis` (`X`/`Z`), and `fitMetres` — an
explicit target length, because **the pinned distance and the dimension worth fitting are
often not the same measurement** (see the survey gotcha below).

The only site is `synagogue-01`. Both corners are on the **front facade**, 16.72 m apart on
bearing 99.57°, so heading = 99.57 − 90 = **9.57°**, facing north. Depth is fitted separately
via `fitMetres: 33.4` on axis Z, giving ×1.87 ≈ 24.7 m wide × 15.9 m tall × 33.4 m deep.

### Adjustments — `Application.persistentDataPath/adjustments.json`

Five values per site: `headingDeg`, `scale`, `eastMetres`, `northMetres`, `heightMetres`.
One slider drives whichever is selected. Applies in preview and GPS mode alike.

Offsets are **world-axis aligned** (`parent.InverseTransformDirection`), so "east" stays east
however the building is rotated, and stay in real building metres because that call ignores
scale.

`save` also bakes coordinates when ARCore has a good fix: it converts the model's world pose
via `AREarthManager.Convert(Pose)` and writes lat/lng, **zeroing east/north** because they
are now baked in — without that the building walks further away on every save. Saved
coordinates then outrank `buildings.json` on the next run. `clear saved` deletes the entry.

---

## The HUD

Right column, top to bottom: `hide` · `mesh` (streetscape debug material) · `reload` (re-read
buildings.json) · `cut` (cutout on/off) · `light` (sky dome) · `capture` · `occlude` (master
occlusion switch, reads `OCC OFF` in orange when disabled).

Left: `preview` · `recenter` · `sun` (forced daylight) · `place on floor`.

Bottom: `rot | scale | X | Y | up` selector, one slider, then `save + GPS` · `reset` ·
`clear saved`. `pick`/`auto` (tap-to-ghost) were removed — see the occlusion section.

The light dome is a sky seen from above: centre = zenith, rim = horizon, up-screen = north.
Coloured cells are the ambient probe by direction, yellow square is the computed sun, small
tinted square is ARCore's estimate. `north … (from VPS)` vs `GUESSED` tells you whether
shadow direction means anything.

### `capture` — the remote-diagnosis button

Writes a matched pair to `persistentDataPath/captures/`: `capture_<stamp>.png` (the composited
frame *including* the HUD) and `.txt` (full state — device, quality tier, Earth state and
accuracies, camera lat/lng/heading, north offset and whether it was measured, sun angles,
light estimation, model file and applied scale, footprint length and effective heading, all
five adjustments plus baked coordinates, streetscape counts **by geometry type**, cutout size,
camera pose). Each component supplies its own `StateReport`, so the dump cannot drift out of
step with the code.

```bash
adb pull /sdcard/Android/data/com.pavel.arbuildings/files/captures/
```

---

## Occlusion — currently OFF by default

`occludersEnabled` defaults to **false**. With it off nothing real can hide the model, which
separates "the model is in the wrong place" from "the model is behind something" — the single
most useful diagnostic on site. **Shadows are unaffected**: the master switch only kills the
depth-only occluder pass, so terrain still catches the model's shadow and neighbours still
shade it. Toggle with `occlude` in the HUD.

Why off: ARCore has no mesh for the synagogue itself (proven below), so occlusion here can
only come from the terrain and the neighbouring buildings, and both removed chunks of the
model faster than they added realism.

Three mechanisms exist, in decreasing usefulness at this site:

**The master switch** (`_OccludersDisabled` global) makes the occluder pass discard
everything. The flag is **inverted deliberately** — an unset Unity global reads as 0, and 0
must mean "occluders behave normally", or occlusion would switch itself off wherever the
driving component has not run.

**The cutout** builds an oriented world-space box from `BuildingLoader.LocalBounds` (plus
margin and padding) in `StreetscapeShadowSetup.LateUpdate`, publishes it as
`_OccluderCutoutWorldToLocal` / `_OccluderCutoutOn`, and all three passes of
`AR/StreetscapeOccluderShadow` discard fragments inside it. Rebuilt every frame because the
anchor moves on re-localization. Padding goes sideways and **up, never down** past the base,
or the terrain stops receiving shadow in a skirt around the building and the model's shadow
looks detached. Caveat: the box is sized from the model, so an oversized model makes an
oversized box that switches off legitimate occlusion from the **neighbours**.

**Ghosting** swaps one streetscape mesh to `AR/GhostWireframe`. Needs a `Building` mesh for
the target — which does not exist here. `pick`/tap-to-select was removed along with its
raycast machinery; it is in git history if another site ever needs it.

`StreetscapeShadowSetup` disables itself in the Editor, so all of this is device-only.

### Confirmed: Google has no building geometry at this site

Checked 2026-08-20 with the Python `streetlevel` package against the Street View panorama
standing right outside the synagogue — `pW3oIq-OzNNDlks5mYZClg`, at 32.083688, 34.815228,
dated 2022-07, 13.9 m north of the surveyed facade midpoint. Its depth map has **56 planes
and not one of them is the synagogue**:

| planes | pixels | what |
|---|---|---|
| plane 0 | 49.7% | `INFINITELY_FAR` — literally everything above the horizon |
| 1–8, 10–55 | 50.1% | all near-horizontal (`\|n_z\| ≈ 1`), d = 2.4–20 m: road, pavement, the raised plaza |
| plane 9 | 0.24% | the **only** vertical plane — 89.4 m away at bearing 347°–1°, i.e. the hillside to the north |

In the building's own column band (cols 339–442, spanning cornerA→cornerB) 13180 of 13184
above-horizon pixels are plane 0. Below the horizon it is ground planes 1–2 (d ≈ 2.4–2.5 m,
the road) and 6–8 (d ≈ 2.9–3.4 m, the podium ~0.5–0.9 m up). Google models the raised plaza
the building stands on, and stops there.

Not a one-off capture — every historical pano at the same spot has exactly one wall plane and
it is always that same distant one: `Na7Kitj22j9zOWd9HNlELg` 2019-01 @ 87.2 m,
`RD5IvfXg0YOW8o2YgE79mQ` 2017-12 @ 82.4 m, `VTOiSl88U0TrQp9dQPRKew` 2015-02 @ 84.3 m,
`6bbvLjrbYIhLlBsx4Cm_jg` 2011-11 @ 87.1 m. Panos 50–95 m away on the same street *do* carry
near-field walls at 10–50 m, so the reconstruction pipeline works fine in the neighbourhood;
it just has nothing for this junction.

**Caveat**: ARCore Streetscape Geometry is served from Google's 3D building tiles, not from
Street View depth maps, so this is not formally the same dataset. But it is the same
reconstruction problem at the same address and it matches what the device already showed. Do
not spend more time on ghosting here — **the cutout is the only occlusion mechanism that will
ever work at this site**.

Reproducing it (installing `streetlevel` is the fiddly part — `pyfrpc`, a Mapy.cz dep, needs a
C toolchain, and pip's SSL trust is broken on this machine):

```bash
pip install --trusted-host pypi.org --trusted-host files.pythonhosted.org --no-deps streetlevel
pip install --trusted-host pypi.org --trusted-host files.pythonhosted.org \
    pyproj requests aiohttp pillow numpy bd09convertor CoordinatesConverter pyexiv2
```

`p.depth` only exposes the resolved per-pixel distance; the planes come from the internals:

```python
from streetlevel.streetview import api, depth as dpt
resp = api.find_panorama_by_id(PANOID, download_depth=True)
b64  = resp[1][0][5][0][5][1][2]          # longest base64 blob in the response
raw  = dpt.decode_b64(b64); h = dpt.parse_header(raw)
pl   = dpt.parse_planes(h, raw)           # {"planes": [{"n","d"}], "indices": [512*256]}
# a plane is a WALL when abs(n[2]) < 0.4, ground when abs(n[2]) > 0.85
```

Depth-map column ↔ true bearing, needed to aim at a surveyed coordinate. Column 0 is
`pano.heading + 180`, i.e. the **image centre is the heading**:

```python
bearing = (degrees(pano.heading) + 180 + (col + 0.5) / 512 * 360) % 360
```

## The desk simulator — built 2026-08-22, and REMOVED the same day

A full Cesium-backed simulator was built and then deleted. It is written up here so nobody
builds it a second time expecting a different answer.

**What it was.** `SiteSim.unity`, generated from an Editor menu item, standing you at
32.083775, 34.815095 inside Google's photorealistic 3D tiles with the real app running behind
an `IEarthSource` seam — `ArCoreEarthSource` on device, a Cesium-backed `SimEarthSource` at the
desk that faked VPS accuracies and resolved the terrain anchor by raycasting Google's tile
colliders. Everything downstream — the localization gate, footprint heading, `fitMetres`
sizing, the nudge sliders, coordinate baking, the HUD — ran its real code.

**What it proved, and this part still stands:**

```
derived frame   north (0,0,1) east (1,0,0) up (0,1,0)   skew 0.0000
sight line      bearing 142.5° true, 28.6 m away        (matches hand calculation)
anchor          Unity (17.39, 0.01, -22.64), bearing 9.56°
                within 10 cm of the E/N offsets computed by hand from the two pins
model           26.95 x 15.91 x 34.79 m world AABB, base on the ground, x1.8701
```

That settles the `fitMetres` arithmetic and clears `BuildingPlacement.AnchorRotation`
(`AngleAxis(180 − heading)`) of any part in the three bad site visits: a surveyed heading of
9.57° goes through ARCore's EUS convention and comes back out as 9.57° true.

**Why it was removed — Google's 3D tiles have a DETAIL HOLE at this junction.** The tileset
refuses to refine below roughly 100 m tiles here while serving building-scale geometry a few
hundred metres away. Measured by moving **only the camera** inside one Play session:

| camera position | altitude | finest tile nearby |
|---|---|---|
| the site | 1.6 m | 190 m |
| the site | 60 m | 190 m |
| 424 m north-east | 60 m | **23.7 m**, incl. an 11 x 15 x 14 m tile |

Returning to the site reverted it exactly. Ruled out: the API key (same key serves fine tiles
424 m away), screen-space error (16 → 8 → 4 changed nothing), altitude and fog culling (the
60 m control), Editor throttling, and network errors (none, ever). At 24 m with SSE 8 Cesium
would subdivide until geometric error fell under ~0.7 m; a 100 m tile is nowhere near that, so
it wants to refine and the data is not there.

**This is the same hole, at the same address, as the Street View finding above.** Two
independent Google datasets, one conclusion: there is no usable 3D reconstruction of this
building. So the simulator could verify arithmetic, heading, scale and the whole runtime path,
but never "does the model sit on the real building" — which was the entire reason to build it.

**If it is ever resurrected** (`git log` has everything), the traps that cost the most time:

- `unity.pkg.cesium.com` string-sorts versions, so `dist-tags.latest` reads **1.10.0** while
  1.25.0 is sitting right there. Only 1.25.0 compiles on Unity 6.5.
- Cesium 1.25 merged `CesiumRuntime` + `CesiumEditor` into one `CesiumForUnity` assembly, while
  ARCore Extensions 1.54 still references `CesiumRuntime` and switches its Geospatial Creator
  on whenever *any* `com.cesium.unity` is installed. Every Geospatial Creator file then fails
  CS0246 and the Editor cannot enter Play mode. Nothing can move: Cesium below 1.25 will not
  build on Unity 6.5 and the `arf6` branch head **is** 1.54. The fix was an Editor-only stub
  assembly named `CesiumRuntime`; type-forwarding does **not** work, because a forward is only
  followed by an assembly that also references the target (CS1069).
- **ARCore's Geospatial Creator can never work on this Unity version** regardless, for the same
  reason: it is pinned to a Cesium API no Unity-6.5-compatible Cesium still has.
- Cesium's Android native library is a **336 MB unstripped `.so`** against a 50 MB APK, and
  native plugins ship on platform compatibility, not on whether any code calls them.

The Map Tiles key question is settled and worth keeping: **the `AndroidCloudServicesApiKey`
already in `ARCoreExtensionsProjectSettings.json` works for Photorealistic 3D Tiles** —
`https://tile.googleapis.com/v1/3dtiles/root.json?key=…` returns 200. No Cesium ion account is
needed, and no second key.


## Gotchas — all of these cost real time

**The survey pins are the most dangerous input in the project.** Three separate site visits
were burned on misreading what two coordinates meant, and none of it looked like a bug —
placement was always arithmetically correct on the numbers it was given:

- *A facade point + a **back corner** is a **DIAGONAL**, not a depth.* Using it as depth
  inflated the model **and** put the heading 45° out. The tell was hand corrections of −43.3°,
  −46.9° and −48.89° on three visits: for a roughly square footprint the diagonal sits at
  exactly 45° to both walls. **Both pins must be on the same wall.**
- *The anchor must be the **midpoint** of the two corners*, not corner A. `BuildingLoader`
  centres the model across the facade, so anchoring at one end left it half a facade width
  (~8 m) off, constantly, invisibly.
- *The pinned dimension is not always the one to fit.* This building is a square block with a
  **narrower rectangle in front** (entrance + stairs). The facade pins span that entrance
  (16.7 m); the model's X spans the main mass. Fitting X to the pins made it far too small.
  Hence `fitMetres`, which fits **depth 33.4 m** on axis Z instead.
- *Check the ratios before trusting anything.* Model box 13.19 × 17.86 = **0.74**; block
  ~25 ÷ 33.4 = **0.75**. Agreement there is what confirmed the interpretation — a description
  of the building's actual shape was worth more than any capture.

**Gradle deps get stripped, and the app goes black.** The External Dependency Manager
periodically removes `play-services-location` from `Assets/Plugins/Android/mainTemplate.gradle`
and deletes `ProjectSettings/AndroidResolverDependencies.xml`. Without it ARCore's geo module
can't link: `EarthState: ErrorEarthNotReady` forever, session dead, **black camera**. Native
log says `ARPresto::Google Play Services location library is not linked`. This has recurred
three times. Check before every build:

```bash
grep -c play-services-location Assets/Plugins/Android/mainTemplate.gradle   # 1 = good, 0 = black screen
```

Restore with `git checkout HEAD -- Assets/Plugins/Android/`. Real fix is EDM →
Android Resolver → either Force Resolve, or disable Auto-Resolution so it stops touching
committed templates.

**The scene overrides C# defaults.** A `[SerializeField]` default only applies to fields the
scene has never stored. Changing a default in code does nothing for existing fields. And
**unsaved inspector edits are not in a build** — that caused an hour of "it's still loading
the old model".

**Never edit `.unity` or `ProjectSettings/*` on disk while the Editor is open** — it holds
them in memory and overwrites on save. Check `Temp/UnityLockfile`. Editing `.cs`, shaders,
StreamingAssets and gradle templates is fine.

**Don't zero a glTF root node's rotation.** Exporters bake the Z-up→Y-up conversion there
(Blender writes +90° about X). Zeroing it lays the building on its back. Same for scale —
multiply it, don't replace it.

**Model origins are often nowhere near the geometry.** The SimLab CAD export has nodes
translated by 10157 units. `recenterOnAnchor` (default on) fixes this; without it the model
places correctly and draws somewhere else entirely.

**`renderer.bounds` is a world-space AABB.** Once the parent carries a heading rotation its
"width" is a blend of width and depth. Height survives (yaw never tilts), which is why the
height fit worked and a facade fit would not have. Use `MeasureLocalBounds`.

**`Shader.Find` returns null in builds** for any shader no scene references — it gets
stripped. `Assets/Resources/ShadowCatcher.mat` exists to force the shadow-catcher shader into
the build.

**Unity's world frame is NOT EUS.** The session origin/heading is wherever the phone started;
that's why `AREarthManager.Convert()` exists. A true bearing must be rotated into Unity's
frame via `NorthOffsetDeg` (camera yaw − `pose.Heading`) before use, or every shadow points
somewhere arbitrary that changes per launch.

**Geometry shaders**: `GhostWireframe.shader` uses one. Metal has **no geometry stage at
all**, so it cannot work on iOS, and Mali support is shaky. The fix is to un-index the mesh
and bake barycentrics into a UV channel — `StreetscapeShadowSetup.Add()` already builds the
MeshFilter, so it's one place. **Not done yet.**

**A Unity Quad's normal is +Z.** `Euler(90,0,0)` points it down (back-face culled, invisible);
`Euler(-90,0,0)` points it up.

**Polling touch phases misses taps.** `Touch.activeTouches` with `phase == Began` misses any
tap that starts and ends between two `Update` calls — about 5 in 6 at 30 fps. Use the
`Touch.onFingerDown` event.

**Preview does not disable Earth.** It only skips *waiting* for localization. Outdoors with
preview on, VPS may well be tracking and coordinates are saveable.

**Tier C shadow distance is 60 m.** `AdaptiveQuality` drops to it under load, and buildings
further than that cast no shadow at all.

**`ScreenCapture.CaptureScreenshot` needs a RELATIVE path on Android.** It resolves against
`persistentDataPath` and silently writes nothing for an absolute one — the first batch of
captures produced `.txt` files and no `.png`.

**Watch for early returns that skip later work.** `UpdateNorthAlignment()` sat after
`if (el <= 0f) { sunLight.enabled = false; return; }`, so north was never measured at night
and stayed `GUESSED` until sunrise — while VPS had a perfect fix the whole time.

**Beware apostrophes in `python -c '…'` from Bash**, and never put `\n` inside a Python
heredoc that writes C# — both have mangled string literals in this repo. Prefer the Edit tool
for anything containing quotes or escapes.

**Unity throttles Play mode when its window is in the background, and it looks exactly like a
hang.** `Time.time` advanced 37 s across five minutes of wall clock while the Editor sat
behind another window; Cesium streams tiles on frame ticks, so the tileset froze at a fixed
tile count and every knob turned — screen-space error, georeference position — appeared to do
nothing. The cure is `Application.runInBackground = true` set from a `MonoBehaviour.Awake` —
`PlayerSettings.runInBackground` does **not** reach a Play session that has already started,
and read back `False` at runtime while the project setting said `True`. Focused is still
several times faster. **When driving the Editor over MCP, check `Time.time` against the wall
clock before believing anything is stuck.** Unity also defers script compilation entirely
while unfocused, so a newly written `.cs` will not exist as a type — `AssetDatabase.Refresh`
and `CompilationPipeline.RequestScriptCompilation` both queue and do nothing until you click
into the window.

**The shell keeps its working directory between commands, and `Library/PackageCache` is one
`cd` away.** A `cd` into a package to read its source, followed later by a *relative*
`cat > Assets/…`, writes into that package instead of the project. It happened here: two files
landed in
`com.google.ar.core.arfoundation.extensions@…/Runtime/Scripts/Assets/Scripting/Sim/`, and the
empty `Assets/Scripting/Sim/` that resulted read exactly like a tool silently failing. Unity
then reports it as *"has no meta file, but it's in an immutable folder"*, which is the tell.
**Use absolute paths for every write**, and prefer the Write tool.

**ARCore ships a broken `Google.Protobuf` that poisons `EditorApplication.isCompiling`.**
`Editor/Scripts/Internal/Analytics/Google.Protobuf.dll` is version **0.0.0.0, unsigned, and
missing `IBufferMessage`**; Unity's own `MsBuildCompilation` typerefs `Google.Protobuf
3.23.0.0, PublicKeyToken=a7d26565bac4d604`. Same simple name, so ARCore's wins the Editor
domain and any call to `isCompiling` throws `TypeLoadException`. Four ARCore Editor files
compile against it, so it cannot simply be excluded. This is why **CoplayDev's unity-mcp could
never start** — its `EditorStateCache` calls `isCompiling` from a static constructor, so the
type poisons itself and the HTTP listener never binds. Unity's own MCP server
(`com.unity.ai.assistant`, an external relay) is unaffected. The exception predates all of
this work; it will bite the next Editor tool that touches compilation state. Local unblock, if
one is ever needed, is to overwrite ARCore's copy with Unity's
`Editor/Data/Managed/Google.Protobuf.dll` — back the original up first, and know it is lost
whenever the package re-resolves.

---

## State

Works on device: VPS localization (routinely **±0.6–1.4 m, yaw ±1.6–2°** at this site), terrain
anchor, GLB load, camera feed, streetscape streaming (27 meshes: 24 building + 3 terrain),
preview mode, floor placement, shadows, the adjustment sliders, save/load, coordinate baking,
capture, hot-reloadable config.

**Awaiting one rebuild** (as of 2026-08-22) — written but never run on device: midpoint
anchoring, `fitMetres` depth fitting, device-copy config + `reload`, `occlude`/`cut` toggles,
occluders defaulting off, and **`RemoteControl`**. Until that build the device runs the old
code, cannot see a pushed `buildings.json`, and will ignore `command.txt` entirely. The next
build is the gate on the whole tripod workflow.

Never verified on device: footprint mode landing correctly at the real site, the corrected
×1.87 scale, ghost wireframe rendering (and it never will here).

**The next session is a tripod session**: phone fixed and pointed at the facade, laptop on
adb, no hands on the device. Screenshots via `adb exec-out screencap`, state via
`adb pull state.txt`, adjustments via `adb push command.txt`. Bring the phone up, confirm
`[Remote] listening on …` in logcat, then work in nudges.

Open:
- **Verify the placement prediction on the phone.** The arithmetic half is settled
  (×1.8701 → 24.66 × 15.91 × 33.40 m, front face at 9.57° true, anchored at the facade
  midpoint, anchor within 10 cm of hand-calculated E/N offsets). The visual half was the point
  of the simulator and cannot be answered there — Google has no reconstruction of this
  building — so it needs the tripod.
- Lighting is uncalibrated: `intensityDivisor` is still the untouched 1000 and exposure reads
  0.00 EV with `driveExposure` on, so `colorAdjustments` may be null on the Volume.
- `AROcclusionManager` / Depth API would occlude cars and people, which streetscape never can.
  Deliberately deferred — every custom shader would need to sample the depth texture.
- ~~Geospatial Creator / 3D tiles in the Editor~~ — **closed, do not reopen.** Built, measured,
  removed; Google has no reconstruction of this building and ARCore's Geospatial Creator cannot
  work on Unity 6.5 at all. See the simulator postmortem above.
- iOS is plausible (ARKit installed, iOS support and API key already set) but needs a Mac or
  cloud CI, the empty `locationUsageDescription` filled in, and the geometry-shader fix.
- Ghosting is **dead at this site** — Google's reconstruction has no building geometry here
  (see the section above). Only matters if the app is ever pointed at another address.
