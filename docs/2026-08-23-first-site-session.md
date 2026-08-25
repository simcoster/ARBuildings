# First on-site geospatial session — 2026-08-23

The first time the model was placed against the real synagogue with VPS. Five bugs found,
four fixed, one still open. This is the full record, including the theories that turned out
to be wrong, because several of them were confidently argued and cost real time.

---

## Outcome in one paragraph

VPS localisation at this site is **excellent** — ±0.6–1.5 m horizontal, yaw 1.8° — and the
terrain anchor resolves to within a metre of the surveyed facade midpoint. Footprint mode,
`fitMetres` sizing and the device-copy config all work on device. `RemoteControl` drove the
entire session over adb, which is the workflow it was built for. The model still does not
land correctly on the building, and it flickers in and out as the camera tilts. The placement
arithmetic is not the problem; the remaining faults are in how the model is anchored and
rendered.

---

## What was verified on device

| Thing | Result |
|---|---|
| VPS horizontal accuracy | **±0.6 – 1.5 m** across the session |
| VPS yaw accuracy | **1.8°** |
| Terrain anchor | resolves, 28.4–29.4 m ahead, `ON SCREEN` |
| Anchor vs survey | camera→anchor computed 19.6 m S / 20.6 m E = 28.4 m at bearing 133.6°, matching the reported distance and the 130.1° camera heading |
| Baked coordinates | `32.0835708, 34.8152792` — **exactly** the midpoint of the two surveyed facade corners |
| Sizing | `×1.8701`, model height 15.91 m — as predicted |
| `RemoteControl` | drove preview, sun, rot, scale, offsets, toggles, capture — all by file push |
| Streetscape | 27 → 40 meshes streamed; **0 building meshes within 15 m**, nearest 65–84 m |

The last row independently confirms the earlier Street View finding: Google has no
reconstruction of this building, only the plaza it stands on. The `mesh` debug view colours
the terrain slab and nothing else, which looks like a broken button and is not.

---

## Bugs found and fixed

### 1. The model was extruded to the wrong side of the anchor

`BuildingLoader.RecenterOnAnchor` pinned `local.max.z` to the anchor. This model's facade is
its **−Z** extreme, so the BACK landed on the surveyed point and all 33.4 m of depth was
extruded out across the plaza toward the viewer.

Symptoms, all of which looked like different bugs:

| scale | depth toward camera | occupies | what you saw |
|---|---|---|---|
| 0.2 | 6.7 m | 20 → 26.6 m | a small block out on the pavement |
| 0.4 | 13.4 m | 13 → 26.6 m | bigger, closer |
| 1.0 | 33.4 m | **−7** → 26.6 m | camera inside the building |

Fixed by pinning `local.min.z`. Confirmed by the recentring log moving from `-72.54` to
`-39.14` — exactly 33.4 m.

### 2. Preview drifted because it was not anchored

`PreviewRoot` was a plain `new GameObject(...)`, fixed in the **session** frame. ARCore
refines its map continuously and every correction slid the world underneath it, which on
device looks like the model creeping across the floor.

The diagnostic that proved it: `anchor world pos` was **byte-identical** across samples while
the model visibly moved. If the app is not moving it, the frame under it is.

Fixed with `anchorManager.TryAddAnchorAsync(pose)` and reparenting with
`worldPositionStays: true`.

**The trap inside the fix:** the preview is a *child* of the anchor, so destroying the old
anchor before reparenting deletes PreviewRoot, the model and the shadow surfaces. The symptom
is a HUD still reporting `Loaded (17 renderers)` while nothing renders. Create the new anchor,
reparent, *then* destroy the old one.

### 3. Selecting the X or Y nudge deleted the model

`RangeOf` declared East/North as **logarithmic over −150…150**. `Mathf.Log(-150)` is NaN, and
`Mathf.Approximately(NaN, NaN)` is false — so the slider counted as "moved" on its first frame
and wrote NaN straight into the offset. A single NaN in a Transform poisons the hierarchy and
the model disappears with nothing in the log.

Fixed with a signed-log mapping (knee 0.5 m) that is defined across a range spanning zero,
plus a `float.IsNaN` guard in `DebugHud` and a non-finite refusal in `SetValue`.

### 4. The master occlusion switch never worked

`AR/StreetscapeOccluderShadow` has three passes and only `Occluder` tests
`_OccludersDisabled`; `ShadowReceive` still paints the mesh and `ShadowCaster` still writes the
shadow map. ARCore's terrain slab therefore covered the model **identically in both toggle
states** — the switch was a no-op, and the HUD said `OCC OFF` while occlusion was happening.

Fixed by making the switch disable the **renderers** instead of setting a shader global. It
cannot be defeated by a pass nobody remembered to guard. New streetscape meshes are born
respecting the switch, because they stream in continuously.

Trade, accepted deliberately: with occluders off the terrain no longer catches the model's
shadow either.

### 5. The sun renders the model nearly black outdoors

With forced daylight off, `LightingController.OnFrame` assigns ARCore's `mainLightColor`
directly to `sunLight.color`. That value is a relative white-balance-style correction, not an
absolute colour; outdoors it came back as `RGBA(0.056, 0.091, 0.135)` — nearly black. Work
around it with `sun on`. Not yet fixed properly.

---

## Still open

### ~~The model flickers in and out as the camera tilts~~ — CLOSED the same evening

Worse when the model sits high in frame; worse again when a table was visible at placement
time. Reproduces **at the desk in preview**, so it does not need another site visit.

Ruled out by measurement, not argument:

- **Not the anchor moving** — `anchor world pos` identical across six samples over 12 s.
- **Not streetscape occlusion** — `renderers drawing : 0 of 40`.
- **Not culling** — `cull bounds now` reports the bounds centre 0.03 m from where the model
  draws, and larger than the model. `RecalculateBounds()` on all 17 meshes changed nothing.
- **Not transparency** — a five-frame burst showed the model *entirely absent* in one frame
  and back in the next, not see-through.

Leading suspect at the time: the model hangs off an `ARAnchor`, and AR Foundation may
deactivate a trackable when its tracking degrades — taking every child with it. Tilting up
toward a featureless ceiling/window is exactly where tracking degrades. The table clue fits a
variant: an anchor attached to a small, unstable table plane rather than the floor.

`anchor trackable : <state>, active=<bool>, pending=<bool>` was added to the state dump and
distinguishes them in one reading.

#### Answered — and FIXED — the same evening: it is DEPTH occlusion, and it was never the anchor

**Confirmed on device at 20:02, build stamp `2026-08-23 19:58:05`: the flicker is gone.**

The build that carried the `anchor trackable` diagnostic was already on the phone. It reads
`Tracking, active=True, pending=False` — **the anchor is fine.** The real occluder was the one
nobody had counted:

- The scene carries an **`AROcclusionManager`**, enabled, `m_EnvironmentDepthMode: 1`
  (Fastest), temporal smoothing on. `CLAUDE.md` said the Depth API was "deliberately
  deferred". It was in the scene the whole time.
- It needs **no shader of ours**. `ARCoreBackground.shader` draws the camera feed with
  `ZWrite On` and writes `gl_FragDepth` from `_EnvironmentDepth`, so ARCore's depth map is in
  the depth buffer before any scene geometry, and every opaque object is depth-tested against
  it. Nothing opts in.
- ARCore depth is useful ~0.5–5 m, valid to ~8 m. **The building is 28 m away.**
- And on this device the depth pipeline is failing about **ten times a second**:
  `spherical_rectifier.cc:159 RET_CHECK failure … Only kUnrectifiedOriginal is supported for
  ComputeDisparity`, tag `native`, straight from the app's own pid.

That is the only hypothesis consistent with every measurement above. `renderers drawing : 0 of
40` counts *streetscape* renderers, and the camera background is not one — which is precisely
why "occlusion is off" read true while occlusion was happening, for the **second** time this
project (see wrong theory 2, which was about the other switch).

**The lesson worth keeping:** an "occlusion off" reading is only as broad as the list of
occluders you know about. Twice now that list has been short by one.

Fixed by `DepthOcclusion`, off by default, `depth on|off` over `RemoteControl`, reporting
`depth manager` / `depth requested` / `depth current` / `depth texture` into the state dump.
The lever is `AROcclusionManager.enabled` rather than the depth mode, because the background
material's depth keyword is only ever pushed on a frame event — stop the frames and the
keyword stays stuck on, while disabling the manager makes the package fire one last event that
clears it. Kept as a toggle rather than deleted because being able to A/B an occluder on site
without a rebuild is what found this in the first place.

Measured across the two builds:

| | old build (depth on) | new build (depth off) |
|---|---|---|
| `depth current` | Fastest | **Disabled**, preference NoOcclusion |
| `depth texture` | present | **none** |
| motion-stereo `RET_CHECK` errors | 82 in ~12 s (**~10/s**) | 9 in 49 s (**~0.2/s**) |
| flicker | yes | **no** |

`depth current` is the line to read, not `depth requested` — it is the subsystem agreeing
rather than a request having been made.

One detail worth keeping: the depth errors did **not** stop, they dropped ~50×. Something else
in ARCore still pokes the pipeline occasionally, most likely Streetscape Geometry. That
sharpens the diagnosis rather than weakening it — the flicker stopped while those errors
continued, so the fault was never the errors themselves, it was the depth **texture** reaching
the depth buffer through the camera background pass.

### Other open items

- The **occluder cutout clips the model**, which it should never do — it is meant to carve
  occluders, and it still had an effect with occluders fully disabled.
- `modelFrontOffsetDeg` is still `0` in `buildings.json`; whether it needs changing depends on
  re-testing after fix #1.
- Exposure is still uncalibrated (`0.00 EV`, `intensityDivisor` untouched at 1000). ARCore
  reports `estimated lumens: 0`, so `DriveExposure` likely never runs.

---

## Theories that were wrong

Recorded because each was argued confidently and cost time.

1. **"`modelFrontOffsetDeg` should be 180."** Wrong — and it was documented in `CLAUDE.md`
   before being tested. A 180° yaw rotates facade and body *together*, so it trades a
   wrong-side body for a facade pointing away from the street. The real fault was the
   `max.z`/`min.z` extrusion side.
2. **"The occlusion master switch is backwards."** Based on one screenshot pair that differed
   because a car moved and streetscape restreamed between frames — not because of the toggle.
   Single-frame comparisons in a changing scene are not evidence.
3. **"The model is semi-transparent."** It was flickering, not translucent.
4. **"Premature frustum culling from bad bounds."** Killed by the very diagnostic added to
   test it — which is the diagnostic earning its build.
5. **Nudging along "north"/"east" to correct placement.** Those offsets are **Unity world
   axes, not compass directions** (`north offset: 125.3°` here). Six nudge iterations failed
   partly for this reason, and nudging could never have been right anyway: it translates the
   whole model, so aligning the mass necessarily puts the decorated facade 33 m into the block.

---

## Diagnostics added

Every dead end above was invisible from the device. These make the same questions one-line
reads next time:

| Line | Answers |
|---|---|
| `build stamp` | is the change I just made actually on the phone? |
| `renderers drawing : N of M` | can streetscape be hiding the model? |
| `cull bounds now` | are the culling bounds where the model draws? |
| `anchor trackable` | is the anchor tracking, and is it still active? |
| `material shaders` | which shader did the importer choose? |
| `catcher on\|off` | is the shadow catcher sampling the shadow map at all? |

The build stamp is written by an `IPreprocessBuildWithReport`, so it stamps **every** build
path, not just `Build → Android APK`. A build started from File → Build Settings previously
reported `unstamped`.

---

## Measured build times

Same laptop, warm `Library`:

| change | time |
|---|---|
| C# only | **~4 min 20 s** |
| anything touching a `.shader` | **~15 min 30 s** |

Shader variant compilation is the entire difference. Put new knobs behind **material
properties** rather than shader edits — flipping a float over `RemoteControl` is free.
Batchmode is also faster than the Editor's Build & Run, which pays a cold shader cache.

---

## Process lessons

- **Screenshot before changing anything visual**, including when the symptom was described in
  words. A description is a hypothesis to check. This is now a rule in `CLAUDE.md`.
- **Pull `adb logcat` before leaving the site.** The buffer had already rotated by the time
  the laptop was back on power — the `[Loader]` lines that would have settled reload-vs-culling
  were gone, and only 63 lifecycle lines survived.
- **A reinstall may wipe `/sdcard/Android/data/…`** — inconsistently. It takes `buildings.json`
  *and* `adjustments.json` (baked coordinates included). Pull both before every install.
- **Restored `adjustments.json` can fight a config fix.** A saved `scale 0.67` from an earlier
  session — tuned before `fitMetres` existed — silently shrank the corrected model to 67%.
- **Don't trust a screenshot pair taken 8 s apart.** Cars move, people walk through, and
  streetscape geometry restreams.

---

## State of the device at end of session

Adjustments reset to zero, nothing saved, occluders off, cutout off, `sun on`. The device
`buildings.json` was restored to the repo copy (`modelFrontOffsetDeg: 0`). 22 capture files
(11 matched PNG + state pairs) were pulled to the laptop before leaving.

---

## Evening follow-up — state at 20:08, and what to do first next time

Build stamp on the phone: **`2026-08-23 19:58:05`**, carrying the depth switch. Verified from
`state.txt`: `depth occlusion : OFF`, `depth manager : enabled=False`, `depth current :
Disabled, preference NoOcclusion`, `depth texture : none`, `occluders enabled : False`,
`anchor trackable : Tracking, active=True, pending=False`. Flicker gone.

**Do these two before trusting any placement reading:**

1. **`reset`.** The device's `adjustments.json` still carries `scale 0.670` — the
   pre-`fitMetres` value. It has now silently shrunk the model to 67% across two sessions.
2. **Read `depth current`, not `depth requested`.** The first is the subsystem agreeing; the
   second is only what was asked for. Same distinction as `cfg:device` vs `cfg:apk`.

Still open, unchanged by this evening: the occluder cutout clipping the model, whether
`modelFrontOffsetDeg` needs 180 now that the extrusion side is fixed, the missing shadow, and
uncalibrated exposure.

Left uncommitted on `geospatial-bringup`: `Assets/Scripting/DepthOcclusion.cs` (new), the
`depth` command in `RemoteControl`, the depth section in `DebugCapture`, and these docs.
Unity's own build also touched `ProjectSettings/ProjectSettings.asset`,
`ProjectSettings/EditorBuildSettings.asset` and `Assets/Plugins/Android/proguard-user.txt` —
those are the Editor's, not this work's.

The **iPad / Unity Build Automation** target was scoped here but **dropped on 2026-08-25** —
Cloud Build was tried and did not work. Android is the only target.
