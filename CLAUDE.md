# AR_Buildings

Unity 6 (6000.5.7f1) ARCore Geospatial app that overlays a 3D building model onto the real
building at a surveyed coordinate. Android is the only target that has ever been built.
Package versions live in `Packages/manifest.json`.

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

**Screenshot BEFORE changing anything visual — including when the symptom was described to
you.** A description is a hypothesis to check, not a spec to code against. "A grey square
round the model" was the contact-shadow quad, not the shadow catcher, and only measuring the
patch against `contact shadow:` / `shadow ground:` in the capture dump told the two apart —
a coin-flip that costs a 15-minute rebuild to get wrong. Crop and enlarge with .NET
`System.Drawing` from PowerShell (there is no PIL here), and add a `ColorMatrix` contrast
boost when looking for something faint like a shadow.

**Compile check without opening Unity** (seconds, catches all C# errors):

```bash
dotnet build Assembly-CSharp.csproj -v q --nologo 2>&1 | grep -E "error|Build succeeded"
```

A brand-new `.cs` file is missing from the generated `.csproj` until Unity imports it, so
that build will report "type not found" for it. Add a `<Compile Include=...>` line manually
to verify, or ignore that one error. The csproj is gitignored and Unity regenerates it.

### Building without the Editor — and what it costs

`Assets/Editor/BuildAndroid.cs` builds from the command line, so no Editor session is needed
(the two are mutually exclusive: the Editor holds a project lock). Scenes come from
`EditorBuildSettings`, so it cannot drift from what Build & Run would produce, and it exits
non-zero on failure — a failed batch build otherwise reads exactly like a success.

```bash
"C:/Program Files/Unity/Hub/Editor/6000.5.7f1/Editor/Unity.exe" -quit -batchmode \
  -projectPath C:/dev/ARBuildings -buildTarget Android \
  -executeMethod BuildAndroid.Build -outputPath C:/dev/ARBuildings/builds/current.apk \
  -logFile build.log
```

**Measured build times on this laptop, warm `Library`:**

| change | time |
|---|---|
| C# only | **~4 min 20 s** |
| anything touching a `.shader` | **~15 min 30 s** |

Shader variant compilation is the entire difference, so **put new knobs behind material
properties rather than shader edits** — flipping a float over `RemoteControl` costs nothing,
recompiling a shader costs a quarter of an hour. The Editor's own Build & Run is slower again
than batchmode (cold shader cache, full refresh); prefer the command line.

Two batchmode traps seen here: the first run after an Editor session died silently mid-compile
with exit 1 and an empty log (a second run succeeded), and `-nographics` was in use for that
failed run — drop it. Kill a build mid-IL2CPP and you lose the in-flight cache, so the next one
is slower.

**A reinstall may wipe `/sdcard/Android/data/com.pavel.arbuildings/files/`** — sometimes it
does, sometimes it doesn't, with no obvious pattern. That takes `buildings.json` **and**
`adjustments.json` (baked coordinates included) with it. Pull both before every install:

```bash
adb pull $DEV/buildings.json .; adb pull $DEV/adjustments.json .
```

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
| `depth on\|off` | real-world DEPTH occlusion (ARCore/LiDAR). **A different occluder from `occlude`** — see below. Off by default |
| `catcher on\|off` | diagnostic: paint the shadow catcher by its shadow term — green lit, red shadowed |

**`preview on` over the remote does NOT turn forced daylight on**, though the HUD's preview
button does. Indoors or after dark that means a remote-driven preview has its sun switched
off below the horizon and shows an unlit model against a black scene. Always pair it:
`preview on` + `sun on`.

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

Button layout is whatever `DebugHud.OnGUI` draws — read it there, including for screen
coordinates when driving the HUD over `adb shell input tap`. `pick`/`auto` (tap-to-ghost) were
removed; see the occlusion section.

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

**There are TWO independent occluders, and for a whole site session only one of them was
known about.** `occlude` controls streetscape geometry. `depth` controls ARCore's depth map,
which reaches the depth buffer without any of our code or shaders being involved — see
[Depth occlusion](#depth-occlusion--the-second-invisible-occluder). Every "occlusion is off"
measurement taken before 2026-08-23 evening only ever measured the first one.

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

### Depth occlusion — the second, invisible occluder

The scene has always carried an **`AROcclusionManager`** with `m_EnvironmentDepthMode: 1`
(Fastest) and temporal smoothing on. Nothing in this project asked for it, no custom shader
samples it, and that is exactly why it was never suspected — **it does not need a shader of
ours to work.** `ARCoreBackground.shader` draws the camera feed with `ZWrite On` and writes
`gl_FragDepth` from `_EnvironmentDepth`, so ARCore's depth map is in the depth buffer before
any scene geometry is drawn, and **every opaque object is depth-tested against it**. Nothing
opts in; nothing can opt out except the switch.

Two reasons it is wrong on Android here:

- ARCore depth is useful ~0.5–5 m and valid to ~8 m. **The building is 28 m away.** No reading
  at that range can decide what is in front of what.
- On the A35 the depth pipeline **fails about ten times a second**, and has all along:
  `spherical_rectifier.cc:159 RET_CHECK failure … Only kUnrectifiedOriginal is supported for
  ComputeDisparity`, under tag `native`. Motion stereo errors per frame while its output is
  still written into the depth buffer per frame.

This was the cause of **the flicker** recorded on 2026-08-23, and switching it off **fixed it
— confirmed on device the same evening**. It is the only explanation that survives that day's
measurements: `renderers drawing : 0 of 40` counts *streetscape* renderers and the camera
background is not one; the culling bounds were right because culling was never involved; the
anchor read `Tracking, active=True` because the anchor was fine.

The depth errors dropped ~50× but did not stop, so something else in ARCore still requests
depth occasionally — most likely Streetscape Geometry. The flicker stopped anyway, which
localises the fault precisely: not the errors, but the depth **texture** reaching the depth
buffer through the background pass.

`DepthOcclusion` is the switch — **off by default**, `depth on|off` over `RemoteControl`, and
it reports `depth manager` / `depth requested` / `depth current` / `depth texture` into the
state dump so "the switch is off" can be told from "the switch did not take".

**The lever is `AROcclusionManager.enabled`, and which lever you pick matters.** The
background material's `ARCORE_ENVIRONMENT_DEPTH_ENABLED` keyword is only ever pushed by
`ARCameraBackground.OnOcclusionFrameReceived` — on a *frame event*. So anything that merely
stops depth frames arriving (requesting `EnvironmentDepthMode.Disabled` on its own) leaves the
keyword stuck **on** with a stale texture instead of turning it off. Disabling the manager is
the path the package explicitly supports: `AROcclusionManager.OnDisable` stops the subsystem,
destroys the textures and then deliberately fires one last frame event — its own comment says
*"because ARCameraBackground needs it to set the shader keywords"* — and with the subsystem
stopped that event carries the keyword in the **disabled** list.

So the order is: preference and depth mode first (while the manager is still enabled, so the
setters can reach the subsystem and ARCore stops computing depth), `enabled = false` last. A
watchdog re-applies once a second, because those setters are a **no-op until the subsystem
exists** — and it does not exist at `Start`.

Kept as a toggle rather than deleted because on an **iPad it is worth turning ON**: LiDAR
gives real depth at real range, and this is the only mechanism that can put people and cars in
front of the model.

### Confirmed: Google has no building geometry at this site

Street View depth maps at this junction carry **56 planes and not one of them is the
synagogue** — every historical pano back to 2011 has exactly one wall plane, and it is always
the hillside 80–90 m to the north. Google models the raised plaza the building stands on and
stops there. Panos 50–95 m away on the same street *do* carry near-field walls, so the
reconstruction pipeline works fine in the neighbourhood; it just has nothing for this address.
**The cutout is the only occlusion mechanism that will ever work at this site** — do not spend
more time on ghosting here.

Evidence tables, the caveat that Streetscape Geometry is not formally the same dataset, and
the `streetlevel` reproduction recipe: **`google-3d-coverage` skill**.

## The desk simulator — built 2026-08-22, and REMOVED the same day

A full Cesium-backed simulator was built and deleted the same day. It **settled the
arithmetic** — derived frame skew 0.0000, the anchor within 10 cm of the E/N offsets computed
by hand, `fitMetres` sizing and the 9.57° heading confirmed through ARCore's EUS convention
end to end — and was then useless for the only question that matters, because **Google's 3D
tiles have a DETAIL HOLE at this junction**: 190 m tiles at the site, 23.7 m tiles 424 m away,
same key and same Play session. That is the same hole, at the same address, as the Street View
finding above. **Do not build it again**, and note that ARCore's Geospatial Creator cannot work
on Unity 6.5 at all.

The measurements, every ruled-out explanation, and the Cesium 1.25 / ARCore 1.54 version traps:
**`desk-simulator-postmortem` skill**.

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

**Anything placed in AR must hang off a real `ARAnchor`, or it creeps.** `PreviewRoot` was a
plain `new GameObject(...)`, i.e. fixed in the SESSION's frame — and that frame is not fixed to
the room. ARCore refines its map continuously, and every correction slides the world under
anything unanchored; on device it looks like the model inching across the floor. The tell is
diagnostic, not visual: sample `anchor world pos` from `state.txt` a few seconds apart, and if
it is byte-identical while the model visibly moves, the app is not the thing moving it.
`AnchorPreviewRoot()` now creates one via `anchorManager.TryAddAnchorAsync(pose)` and reparents
with `worldPositionStays: true`. The GPS path never had this problem — it hangs off a resolved
terrain anchor already.

*And the trap inside the fix:* the preview is a CHILD of that anchor, so destroying the old
anchor before re-parenting deletes PreviewRoot, the model and the shadow surfaces with it. The
symptom is a HUD that still says `Loaded (17 renderers)` while nothing renders. Create the new
anchor, re-parent onto it, and only then destroy the old one.

**The facade was pointing the wrong way: `modelFrontOffsetDeg` is 0 and should be 180.**
Confirmed in preview on 2026-08-23 — at `rot 0` the near face is a blank wall, at `rot 180` it
is the portico with the stained-glass window. Both preview and GPS placement apply the same
offset (`LookRotation(toCamera) * Euler(0, -ModelFrontOffsetDeg, 0)`), so on site this aims the
facade INTO the building. Fix it in `buildings.json` + `reload` rather than dialling `rot 180`
by hand, which nobody will remember to re-enter. **Not yet applied.**

**Location off ⇒ no streetscape, and a locked phone ⇒ no AR session.** With location disabled
the HUD reads `streetscape: 0 meshes` and Earth never tracks. Separately the phone's 10-minute
screen timeout suspends the session mid-experiment; `adb shell settings put global
stay_on_while_plugged_in 3` keeps it awake while on USB.

**Preview does not disable Earth.** It only skips *waiting* for localization. Outdoors with
preview on, VPS may well be tracking and coordinates are saveable.

**The quality tiers do NOTHING to URP. Measured 2026-08-23.** `QualitySettings.asset` contains
**zero** `m_RenderPipeline` references, so no quality level is bound to a URP asset and
`AdaptiveQuality`'s `SetQualityLevel()` switches only *legacy* quality settings — most of which
URP ignores. `TierA/B/C_RPAsset.asset` are never loaded by anything. The pipeline in force is
always `GraphicsSettings.m_CustomRenderPipeline` = **`Mobile_RPAsset`**: shadow distance
**150 m**, **4** cascades, **1024** shadowmap.

Consequences, all of which cost time before this was found:
- The old note here — "Tier C drops shadow distance to 60 m" — was **wrong**. Nothing drops it.
- `QualitySettings.shadowDistance` is a legacy value URP does not read. Writing it changes the
  number `AdaptiveQuality`'s HUD line prints and **nothing on screen**; that is exactly how a
  cosmetic "preview shadow distance override" looked like it was working.
- To change shadow settings at runtime you must edit the **active `UniversalRenderPipelineAsset`**
  (`Mobile_RPAsset`), not `QualitySettings`.
- Frame-rate tiering is therefore not doing what the HUD claims. Binding the tier assets to the
  quality levels is a real fix and has never been done.

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

**`RemoteControl` is LIVE on device** (verified 2026-08-23, `[Remote] listening on …`). The
whole tripod workflow now works: preview, sun, rot, scale, offsets, toggles and `capture` are
all file pushes. `fitMetres` sizing, device-copy config and midpoint anchoring are all
confirmed running on device too — the "awaiting one rebuild" note that used to live here was
stale by at least one build.

Never verified on device: footprint mode landing correctly at the real site, the corrected
×1.87 scale, ghost wireframe rendering (and it never will here).

**The next session is a tripod session**: phone fixed and pointed at the facade, laptop on
adb, no hands on the device. Screenshots via `adb exec-out screencap`, state via
`adb pull state.txt`, adjustments via `adb push command.txt`. Bring the phone up, confirm
`[Remote] listening on …` in logcat, then work in nudges.

### Where the last session left off — 2026-08-23 evening

The **flicker is fixed** (build stamp `2026-08-23 19:58:05`): it was ARCore's depth map being
written into the depth buffer by the camera background pass, and `depth off` is now the
default. Full account in `docs/2026-08-23-first-site-session.md`; mechanism in
[Depth occlusion](#depth-occlusion--the-second-invisible-occluder). The anchor-deactivation
theory is dead — `anchor trackable` reads `Tracking, active=True` throughout.

Two things to do **before** trusting any placement reading next time:

1. **`reset` the adjustments.** The device's `adjustments.json` still carries `scale 0.670`,
   the pre-`fitMetres` value, which silently shrinks the corrected model to 67%. It has now
   fooled two sessions.
2. **Check `depth current : Disabled`** in the state dump, not `depth requested` — the first
   is the subsystem agreeing, the second is only what was asked for.

Uncommitted at the end of the session: `DepthOcclusion.cs`, the `depth` command in
`RemoteControl`, the depth section in `DebugCapture`, and these docs.

Open:
- **Verify the placement prediction on the phone.** The arithmetic half is settled
  (×1.8701 → 24.66 × 15.91 × 33.40 m, front face at 9.57° true, anchored at the facade
  midpoint, anchor within 10 cm of hand-calculated E/N offsets). The visual half was the point
  of the simulator and cannot be answered there — Google has no reconstruction of this
  building — so it needs the tripod.
- **The model casts no shadow, and the cause is narrowed to two.** `catcher on` paints the
  catcher **uniformly green** — including directly under the building — which proves the quad
  draws, sits correctly on the floor, and that the shadow lookup returns "lit" everywhere.
  Either (a) nothing is being rendered into the main light's shadow map, or (b) the shadow
  variant of `AR/ShadowCatcher` was stripped from the player build, so `MainLightRealtimeShadow`
  returns a constant 1.0 and would read green whether or not a shadow exists. **They look
  identical from a screenshot.** The experiment that separates them: swap `ShadowGround`'s
  material for a stock URP **Lit** one, whose variants are never stripped — a shadow there means
  the bug is ours. Already ruled out: light shadow type (soft, strength 1), light culling mask
  (all layers), `shadowCastingMode` (forced On in `BuildingLoader`), the glTFast graphs
  (`m_CastShadows: true`, opaque, and glTFast disables only motion-vector/transparent passes),
  and shadow distance (150 m — see the quality-tier gotcha).
- Lighting is uncalibrated: `intensityDivisor` is still the untouched 1000 and exposure reads
  0.00 EV with `driveExposure` on. Note the earlier guess that `colorAdjustments` is null looks
  **wrong** — `SampleSceneProfile` does contain a ColorAdjustments override and the scene does
  reference it. More likely ARCore supplies no `averageBrightness` in this estimation mode; it
  reports `estimated lumens: 0`, so `DriveExposure` never runs.
- Sun direction now follows the room: under forced daylight `LightingController` aims the sun
  along ARCore's `mainLightDirection` (`matchEstimatedLight`, smoothed), instead of the fixed
  135°/45° synthetic sun. Verified on device — `sun world forward` converges to `estimated dir`
  exactly. Its COLOUR is still forced pure white while ARCore estimates the room as warm
  (~1.60, 1.58, 1.41), which is one of the reasons the model reads as pasted on.
- Grounding: a contact-shadow quad was built and then removed on request. Worth knowing that a
  real reference object photographed on the carpet showed **no directional cast shadow at all** —
  just soft contact darkening — because a window is a broad source. Matching this room argues
  for a subtle contact term, not a crisp cast shadow.
- ~~`AROcclusionManager` / Depth API deliberately deferred~~ — **this note was wrong and cost a
  site session.** The manager was in the scene and enabled the whole time, and it needs no
  shader of ours: the camera background pass writes ARCore's depth into the depth buffer and
  every opaque object is tested against it. Now switched **off** by default and exposed as
  `depth on|off`. See [Depth occlusion](#depth-occlusion--the-second-invisible-occluder).
  Turning it **on** is the right call on LiDAR hardware.
- ~~Geospatial Creator / 3D tiles in the Editor~~ — **closed, do not reopen.** Built, measured,
  removed; Google has no reconstruction of this building and ARCore's Geospatial Creator cannot
  work on Unity 6.5 at all. See the simulator postmortem above.
- **The iPad target (Unity Build Automation), scoped 2026-08-23 but not started.** The iPad
  has **LiDAR and no GPS**, which flips two decisions:
  - **Turn `depth` ON there.** LiDAR gives real depth at real range, and it is the only
    mechanism that can put people and cars in front of the model — something streetscape
    geometry can never do. This is why the depth switch is a toggle and not a deletion.
  - **No GPS means no Geospatial.** VPS needs a coarse location prior and a Wi-Fi-only iPad
    has none outdoors. The iPad build is a **preview / manual-placement** app — which already
    works. Consider dropping ARCore Extensions from the iOS target entirely: it removes the
    CocoaPods / ARCore-iOS-SDK complications from cloud CI in one move.

  Four concrete blockers, all verified in the repo:
  1. `locationUsageDescription` is **empty** (`ProjectSettings/ProjectSettings.asset:584`) —
     a crash or a store rejection the moment anything asks for location.
  2. `GhostWireframe.shader` uses `#pragma geometry` and **Metal has no geometry stage**.
     Ghosting is already dead at this site, so excluding it from the iOS target is the cheap
     fix.
  3. `appleEnableAutomaticSigning: 0`, so Build Automation needs a provisioning profile and
     `.p12` uploaded.
  4. Already fine: `targetDevice: 2` (iPhone + iPad), iOS 15.0, `com.unity.xr.arkit` 6.5.0
     installed, and `ProjectSettings/ARCoreExtensionsProjectSettings.json` has
     `IsIOSSupportEnabled: true` with an iOS API key — relevant only if Extensions is kept.
- Ghosting is **dead at this site** — Google's reconstruction has no building geometry here
  (see the section above). Only matters if the app is ever pointed at another address.
