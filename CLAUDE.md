# AR_Buildings

Unity 6 (6000.5.7f1) ARCore Geospatial app that overlays a 3D building model onto the real
building at a surveyed coordinate. URP, AR Foundation 6.5, ARCore Extensions 1.54, glTFast
6.19. Android is the only target that has ever been built.

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
| `SiteCatalog` | reads `StreamingAssets/buildings.json`; **authoritative over the inspector** |
| `BuildingPlacement` | heading maths. Footprint mode derives heading from two coordinates |
| `BuildingLoader` | glTFast load, sizing (`FixedScale` / `TargetHeight` / `FootprintWidth`), recentring |
| `AlignmentNudge` | the five adjustment values + save/load to `adjustments.json` |
| `DebugHud` | IMGUI instrument panel. No Canvas, no wiring, can't be broken by scene edits |
| `LightingController` | sun position, light estimation, north alignment, forced daylight |
| `StreetscapeShadowSetup` | streetscape geometry → occluder / ghost / debug materials, tap-to-select, occluder cutout |
| `DebugCapture` | `capture` button: screenshot + full state dump to `persistentDataPath/captures` |
| `AdaptiveQuality` | frame-time tiering, switches URP asset between Tier A/B/C |

### Site configuration — edit the file, not the inspector

`Assets/StreamingAssets/buildings.json` is read at startup and **overrides inspector
values**. Placement waits for it. This is the way to configure a site; the only inspector
field that matters is `Site Id` on `GeospatialController`, which selects the entry.

Current site `synagogue-01`: two surveyed points that run **front-to-back** (corner A is on
the facade, corner B is the back corner), 41.19 m apart, bearing 141.95°, so heading =
141.95 + 180 = **321.95°** and the model's **depth (Z)** is fitted to 41.19 m.

> Note: the `placeholder-01` entry currently also points at `synagogue.glb` but has **no
> footprint block**, so it uses the old placeholder lat/lng and a manual 328° heading, and
> `FootprintWidth` sizing silently falls back. If the scene's `Site Id` is still
> `placeholder-01`, that is what is running.

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

Right column: `hide` · `mesh` (streetscape debug material) · `pick` (tap-to-ghost) · `auto` ·
`light` (sky dome).
Left: `preview` · `recenter` · `sun` (forced daylight) · `place on floor`.
Bottom: `rot | scale | X | Y | up` selector, one slider, then `save` · `reset` · `clear saved`.

The light dome is a sky seen from above: centre = zenith, rim = horizon, up-screen = north.
Coloured cells are the ambient probe by direction, yellow square is the computed sun, small
tinted square is ARCore's estimate. `north … (from VPS)` vs `GUESSED` tells you whether
shadow direction means anything.

---

### Occlusion: two mechanisms

**Ghosting** swaps one streetscape mesh to `AR/GhostWireframe`. It requires ARCore to serve a
`Building` mesh for the site — and **it does not always**. At the synagogue, `mesh` visualisation
showed terrain only, so ghosting and tap-to-pick both had nothing to select.

**The cutout** is the general fix and is on by default. `StreetscapeShadowSetup.LateUpdate`
builds an oriented world-space box from `BuildingLoader.LocalBounds` (plus margin and
padding) and publishes it as globals `_OccluderCutoutWorldToLocal` / `_OccluderCutoutOn`.
All three passes of `AR/StreetscapeOccluderShadow` discard fragments inside it, so the real
world stops occluding, shadow-receiving and shadow-casting exactly where the replacement
building stands — regardless of how ARCore split the geometry. It is rebuilt every frame
because the anchor moves on re-localization.

Note `StreetscapeShadowSetup` disables itself in the Editor, so the cutout is device-only.

## Gotchas — all of these cost real time

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

---

## State

Works on device: VPS localization, terrain anchor, GLB load from StreamingAssets, camera
feed, streetscape streaming (~26–40 meshes outdoors), preview mode, floor placement,
shadows, the adjustment sliders, save/load.

Never verified on device: footprint mode end-to-end at the real site, coordinate baking with
a good fix, ghost wireframe rendering, streetscape ghost selection.

Open:
- `Site Id` may still be `placeholder-01`; `Size Mode` may still be `TargetHeight` in the scene.
- Ghost wireframe needs the barycentric rewrite before it can work on this device or on iOS.
- iOS is plausible (ARKit installed, iOS support and API key already set) but needs a Mac or
  cloud CI, the empty `locationUsageDescription` filled in, and the geometry-shader fix.
- Streetscape ghost selection over-selects on merged rows; `pick` (tap-to-select) is the
  workaround, a world-space box cutout in the occluder shader is the real fix.
