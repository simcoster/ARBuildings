---
name: desk-simulator-postmortem
description: Why the Cesium-backed desk simulator was built and removed on the same day (2026-08-22), what it did prove about heading and fitMetres arithmetic, and the Cesium/ARCore version traps if it is ever resurrected. Load before proposing any Editor-side simulator, Geospatial Creator, or Google 3D-tiles work.
---

# The desk simulator — do not rebuild it expecting a different answer

CLAUDE.md carries the one-line verdict. This is the full postmortem, kept out of always-loaded
context because it only matters when simulator or 3D-tiles work is on the table.

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


