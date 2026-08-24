# AR Building Overlay — Unity Tutorial

Overlay 3D building models (fetched at runtime from a Drive URL) onto real buildings.

> **Confidence note:** the Unity/AR Foundation/ARCore Extensions ecosystem changes package APIs
> every few releases. Steps 0–5 (setup) are stable and I'm confident in them. Exact C# method
> names in Steps 6–7 have shifted between ARCore Extensions versions — treat them as
> *shape-correct, name-verify-against-your-installed-version*. I've flagged each spot.


---

## Project file layout

Every code block below is labelled with the file it belongs in. This is the whole set:

```
Assets/
├── Scripts/
│   ├── GeospatialController.cs   Step 7    localization, anchoring, builds the hierarchy
│   ├── BuildingPlacement.cs      Step 7.5  the two rotations (world heading + model offset)
│   ├── BuildingLoader.cs         Step 8    fetches and instantiates the GLB
│   ├── AlignmentNudge.cs         Step 9    on-site manual correction + persistence
│   ├── SolarPosition.cs          Step 11.1 static maths class, NOT a MonoBehaviour
│   ├── LightingController.cs     Step 11   sun, light estimation, exposure, fog
│   ├── StreetscapeShadowSetup.cs Step 11.4 assigns materials to streetscape meshes
│   └── AdaptiveQuality.cs        Step 13   device tiering
├── Shaders/
│   ├── ShadowCatcher.shader          Step 11.4
│   ├── StreetscapeOccluderShadow.shader  Step 10  (occlusion + shadow, two passes)
│   └── GhostWireframe.shader         Step 10.5
├── StreamingAssets/
│   └── buildings.json
└── ARCoreExtensionsConfig.asset      Step 5
```

Every `.shader` file also needs a **Material** created from it (right-click the shader →
Create → Material). Scripts get assigned to Materials, not shaders — assigning the shader
directly is a common early mistake.

### Where the components go in the scene

```
AR Session
XR Origin
├── AR Camera  (ARCameraManager, ARCameraBackground)
├── [components] ARAnchorManager, ARPlaneManager, AROcclusionManager,
│                ARStreetscapeGeometryManager, LightingController,
│                StreetscapeShadowSetup, AdaptiveQuality
Directional Light   ← driven by LightingController
ARCoreExtensionsManager
└── [components] ARCoreExtensions, AREarthManager
GeospatialController
└── [components] GeospatialController, BuildingPlacement, AlignmentNudge, BuildingLoader
```

### Runtime hierarchy the code builds

```
BuildingAnchor      ← ARCore owns this transform; never write to it
└── NudgeRoot       ← AlignmentNudge writes manual offsets here
    └── AlignmentRoot   ← BuildingPlacement applies modelFrontOffsetDeg here
        └── [GLB instantiates here]
```

---

## Step 0 — Pick your anchoring approach (decide this first)

| Approach | How it localizes | Accuracy (real-world) | Cost / friction | Best when |
|---|---|---|---|---|
| **ARCore Geospatial API (VPS)** | Matches camera frames against Google Street View imagery | ~0.5–2 m horizontal, ~2–5° heading when it locks well | Free tier via Google Cloud, needs API key + billing account | Outdoor, urban, Street View coverage exists |
| **Vuforia Area Targets** | Matches against a 3D scan *you* make of the building | Very tight (cm) inside the scanned volume | Paid licence; you must scan the site (Matterport / iPhone LiDAR) | One specific known building, repeat visits, no internet |
| **Manual tap-to-place + nudge** | User aligns it by hand | Whatever the user manages | Free, ~2 hours of work | Demo/proof-of-concept, or as a fallback layer |

**My recommendation:** build on **ARCore Geospatial**, and ship manual nudge controls anyway
(Step 9). Every serious geospatial AR app has manual nudge, because VPS localization fails often
enough that you can't make it the only path.

**Blocking check before you invest a day in this:** Geospatial needs **VPS coverage** at the exact
site, which is *roughly* — but not exactly — Google Street View coverage. Street View exists in
Israel, but that doesn't guarantee VPS at Pavel's specific building. Verify before building:

- Google publishes a **VPS coverage map** — check the site on it.
- Better: call `AREarthManager.CheckVpsAvailabilityAsync(latitude, longitude)` in a 20-line throwaway
  app and run it standing at the building. This is the ground truth; the coverage map is a summary.

If VPS isn't available there → switch to Vuforia Area Targets or manual placement. Don't fight it.

### The other fork: what is the model *for*?

- **Proposed building on an empty/cleared lot** (architectural viz) → easy case. Nothing real to
  conflict with. Ground/terrain anchor is enough.
- **Model overlaid on an existing standing building** (as-built vs as-designed comparison, BIM
  inspection) → harder. You need the model to line up with a physical structure to within ~30 cm or
  it looks obviously wrong, and you need occlusion (Step 10) or the model floats visibly in front
  of the real facade.

The second case is where projects like this usually die. If that's Pavel's case, budget real time
for alignment UX.

---

## Step 1 — Install the Editor

Unity Hub 3.20 is just the launcher — you still need an Editor version.

- In Hub → **Installs → Install Editor** → choose **Unity 6 LTS (6000.0.x)**.
  - *Why not 2022.3?* AR Foundation 6.x requires Unity 6. AR Foundation 5.x on 2022.3 also works
    and is arguably better-documented right now, but you'll hit end-of-support sooner. Either is
    defensible; pick Unity 6 unless something forces otherwise.
- Check these **modules** during install:
  - **Android Build Support** → expand it → **Android SDK & NDK Tools** *and* **OpenJDK** (both
    sub-boxes; people miss these and then spend an hour on cryptic Gradle errors)
  - **iOS Build Support** — only if you're targeting iPhone. Requires a Mac to actually build.

**Device requirement:** an ARCore-certified Android phone (Google publishes the supported-device
list) or an ARKit-capable iPhone. Geospatial specifically needs a reasonably recent device — an
old ARCore-listed phone can pass the ARCore check but localize badly.

---

## Step 2 — Create the project

New project → **3D (URP)** template.

Use URP, not Built-in. AR occlusion shaders, the Geospatial Creator preview, and most current AR
sample code assume URP. Retrofitting later is annoying.

---

## Step 3 — Install packages

**Window → Package Manager**, then:

### From Unity Registry
- `AR Foundation`
- `Google ARCore XR Plugin`
- `Apple ARKit XR Plugin` (iOS only)
- `glTFast` — search for **glTFast**; the package ID is `com.unity.cloud.gltfast`. (It was
  `com.atteneder.gltfast` before Unity adopted it. If your Package Manager only shows the old one,
  either works — the API in Step 8 is the same.)

### ARCore Extensions (not in the registry)
This is the package that provides Geospatial. Get it from Google's **arcore-unity-extensions**
GitHub repo:

- Download the release `.tgz`
- Package Manager → **+ → Add package from tarball**

*Or* add Google's scoped registry to `Packages/manifest.json` if you prefer version pinning.

> **Version-matching warning — this is the #1 time sink on this project.** ARCore Extensions must
> match your ARCore XR Plugin major version, which must match your AR Foundation version. Check the
> Extensions release notes for the compatibility table *before* installing, and pin all three. A
> mismatch produces build errors that read like unrelated compile failures.

---

## Step 4 — Player Settings (Android)

**Edit → Project Settings → Player → Android**:

| Setting | Value | Why |
|---|---|---|
| Minimum API Level | **24** (Android 7.0) | ARCore's floor |
| Scripting Backend | **IL2CPP** | Required for ARM64 |
| Target Architectures | **ARM64** only (uncheck ARMv7) | Play Store requirement; ARCore needs it |
| Graphics APIs | Remove **Vulkan**, leave **OpenGLES3** | *Historically* ARCore was OpenGLES3-only. Vulkan support has improved and may now work on your device — but OpenGLES3 is the zero-risk setting. Try Vulkan only if you need the perf. |
| Internet Access | **Require** | You're fetching models over HTTP |

Also add **Camera Usage Description** (iOS) / confirm the camera permission is auto-added (Android
— AR Foundation injects it into the manifest).

---

## Step 5 — XR Plug-in Management + API key

**Project Settings → XR Plug-in Management**:
- Android tab → tick **Google ARCore**
- iOS tab → tick **Apple ARKit**

**Project Settings → XR Plug-in Management → ARCore Extensions**:
- Set **Optional/Required** for ARCore → **Required**
- Tick **Geospatial**
- **Android Authentication Strategy** → *API Key* → paste your key
- **iOS Authentication Strategy** → *API Key* (or Authentication Token for production)

### Getting the API key
1. Google Cloud Console → create a project
2. **Enable the ARCore API** (and **Map Tiles API** if you want the Editor preview in Step 11)
3. Credentials → Create API Key
4. Restrict it: Android apps → your package name + SHA-1 fingerprint. An unrestricted key on a
   billing-enabled project is a real financial risk if it leaks.

Billing must be enabled on the Cloud project even to use free-tier quota.

---

## Step 6 — Scene setup

Delete the default `Main Camera`. Then:

- **GameObject → XR → AR Session**
- **GameObject → XR → XR Origin (Mobile AR)**

On the **XR Origin** object, add:
- `ARAnchorManager`
- `ARPlaneManager` (optional — useful for a ground-plane fallback)
- `AROcclusionManager` (see Step 10)

Create an empty GameObject `ARCoreExtensionsManager` and add:
- `ARCoreExtensions` — wire up its **Session**, **Session Origin/XR Origin**, and **Camera Manager**
  fields
- `AREarthManager`

Add your own `GeospatialController` MonoBehaviour — that's where the next step's code goes.

---

## Step 7 — Wait for localization, then anchor

Never place the model the instant the app starts. VPS localization takes anywhere from 2 to 30+
seconds, and placing early puts the building in the wrong place with no way for the user to know
why.

**File:** `Assets/Scripts/GeospatialController.cs` — new file. Add this component to the
`GeospatialController` GameObject you made in Step 6.

```csharp
using UnityEngine;
using Google.XR.ARCoreExtensions;
using UnityEngine.XR.ARFoundation;

public class GeospatialController : MonoBehaviour
{
    [SerializeField] AREarthManager earthManager;
    [SerializeField] ARAnchorManager anchorManager;

    // Pavel's building — get these from Google Earth / a survey, not from a phone GPS reading
    [SerializeField] double latitude;
    [SerializeField] double longitude;
    [SerializeField] double altitudeAboveTerrain = 0;  // metres above ground at that lat/lng
    [SerializeField] double headingDegrees;            // model's facing, degrees clockwise from north

    // Don't place until localization is this good
    const double MaxHorizontalAccuracy = 2.0;   // metres
    const double MaxYawAccuracy        = 10.0;  // degrees

    bool placed;

    void Update()
    {
        if (placed) return;

        if (earthManager.EarthTrackingState != TrackingState.Tracking)
        {
            // Show "Point your camera at buildings and move slowly" in the UI
            return;
        }

        var pose = earthManager.CameraGeospatialPose;

        if (pose.HorizontalAccuracy > MaxHorizontalAccuracy ||
            pose.OrientationYawAccuracy > MaxYawAccuracy)
        {
            // Show live accuracy numbers — hugely useful for debugging on-site
            return;
        }

        PlaceBuilding();
        placed = true;
    }

    void PlaceBuilding()
    {
        // ARCore's geospatial frame is EUS (East-Up-South).
        // This is the documented heading -> quaternion conversion:
        var rotation = Quaternion.AngleAxis(180f - (float)headingDegrees, Vector3.up);

        var promise = anchorManager.ResolveAnchorOnTerrainAsync(
            latitude, longitude, altitudeAboveTerrain, rotation);

        StartCoroutine(WaitForAnchor(promise));
    }

    System.Collections.IEnumerator WaitForAnchor(ResolveAnchorOnTerrainPromise promise)
    {
        yield return promise;
        var result = promise.Result;

        if (result.TerrainAnchorState == TerrainAnchorState.Success)
        {
            // result.Anchor.transform is now correctly placed in world space.
            // Load and parent the model here (Step 8).
            BuildingLoader.Instance.LoadInto(result.Anchor.transform);
        }
        else
        {
            Debug.LogError($"Terrain anchor failed: {result.TerrainAnchorState}");
        }
    }
}
```

> **Verify against your version:** `ResolveAnchorOnTerrainAsync`, `ResolveAnchorOnRooftopAsync`,
> and the promise/result types were introduced and then renamed across Extensions 1.3x–1.4x.
> Older versions used `ARAnchorManager.AddAnchor(lat, lng, altitude, quaternion)` with raw WGS84
> altitude. Open the package's API docs in `Packages/` and confirm before you fight the compiler.

### Which anchor type
- **Terrain anchor** — altitude relative to ground level. Use this. You supply "3 m above the
  ground here," Google resolves the actual elevation. Robust.
- **Rooftop anchor** — resolves to the top of the building at that lat/lng. Useful for placing
  something *on* a roof.
- **WGS84 anchor** — you supply absolute altitude above the ellipsoid. Avoid unless you have
  surveyed elevation data. Getting this wrong by 20 m is very easy and the building ends up
  underground or in the sky.

### Getting good lat/lng/heading
Don't read them off a phone. Use **Google Earth Pro** or the Geospatial Creator (Step 11) to pick
the exact corner point and read off coordinates to 6+ decimal places. Heading is the compass
bearing the model's "front" should face. A 5° heading error is very visible at 50 m.

### Step 7.5 — Orienting the model

Two separate rotations are in play, and conflating them is the classic cause of a building that
ends up rotated by some baffling amount. Keep them as two serialized fields so that when it's wrong
on site you know which one to touch.

```
BuildingAnchor            ← ARCore orients this to world/compass space (rotation 1)
 └ AlignmentRoot          ← your correction for the GLB's own local axes (rotation 2)
    └ [GLB instantiates here]
```

**File:** `Assets/Scripts/BuildingPlacement.cs` — new file. Sits alongside `GeospatialController`
on the same GameObject; `GeospatialController` reads `AnchorRotation` from it.

```csharp
public class BuildingPlacement : MonoBehaviour
{
    [Header("Rotation 1 — where the building faces in the world")]
    [Tooltip("True-north azimuth, degrees clockwise. N32°W = 328.")]
    [SerializeField] float buildingHeadingDeg = 328f;

    [Header("Rotation 2 — correcting the model's own axes")]
    [Tooltip("Degrees to spin the GLB so its intended front faces local +Z. 0 if exported to spec.")]
    [SerializeField] float modelFrontOffsetDeg = 0f;

    [Header("Manual on-site nudge (Step 9)")]
    [SerializeField] float headingNudgeDeg = 0f;

    public Quaternion AnchorRotation =>
        Quaternion.AngleAxis(180f - (buildingHeadingDeg + headingNudgeDeg), Vector3.up);

    public void AttachModel(Transform anchor, Transform glbRoot)
    {
        var alignmentRoot = new GameObject("AlignmentRoot").transform;
        alignmentRoot.SetParent(anchor, false);
        alignmentRoot.localPosition = Vector3.zero;
        alignmentRoot.localRotation = Quaternion.Euler(0f, modelFrontOffsetDeg, 0f);

        glbRoot.SetParent(alignmentRoot, false);
        glbRoot.localPosition = Vector3.zero;
        glbRoot.localRotation = Quaternion.identity;
    }
}
```

#### Nail down the bearing before writing code

"North-west 32 degrees" has two readings about 45° apart:

- **N32°W** — surveyor's quadrant bearing → azimuth **328°**
- 32° *past* north-west → azimuth **347°**

Settle on one azimuth, 0–360 clockwise from true north. Also confirm what "face X" refers to: the
model's local +X axis, or a facade someone labelled X on a drawing? Frequently not the same thing.

#### Where to get the real number

1. **The site plan.** If this is a real project the drawing has a north arrow and a building grid
   orientation. Authoritative, free, already exists — ask Pavel first.
2. **Google Earth Pro ruler tool.** Draw a line along the facade; it reports the heading.
3. **Phone compass.** Last resort, and see the declination warning below.

#### Three things that silently cost you degrees

**True vs magnetic north.** ARCore geospatial works in *true* north. Compass readings and older
drawings are often magnetic. Look up the site's declination (NOAA's calculator) and correct for it.
Depending on location that's a several-degree error — clearly visible at building scale.

**Pivot placement matters as much as rotation.** If the GLB's origin is at the building centroid
but your lat/lng is a facade corner, you're off by half a building no matter how perfect the
heading. Pick one identifiable ground point — e.g. the north-west ground corner — and make sure the
model origin *and* the lat/lng both refer to that same point.

**Fix it at export, not in code.** Give Pavel an export spec:

> Origin at the north-west ground corner. Front facade facing **+Z**. **Y-up**. Units in **metres**.
> No parent transform offsets. Apply all transforms before export.

Then `modelFrontOffsetDeg` stays 0 forever and you only ever debug one number. Far less
error-prone than correcting arbitrary model orientations at runtime.

#### How you'll actually land it

Empirically. Ship the heading nudge from Step 9, go to the site, align by eye against the real
building, read the value back, bake it in. Log the final number so it isn't lost.

For verification, render **Streetscape Geometry** in wireframe — Google's mesh of the real building
is a far more reliable alignment reference than eyeballing a facade edge.

---

## Step 8 — Loading the model from the Drive URL

### Format: it must be glTF/GLB
Unity **cannot import FBX, OBJ, Revit, or SketchUp at runtime** — those are editor-time importers.
Runtime loading realistically means **.glb** (binary glTF).

If Pavel's models are Revit or FBX, you need a conversion step:
- Revit → glTF exporter plugin, or
- FBX → Blender → export **glTF 2.0 (.glb)**

While you're there: architectural models are usually absurdly heavy for mobile. Apply **Draco** or
**meshopt** compression on export, and decimate. A 200 MB Revit export will not run on a phone.
Target under ~30 MB and under ~500k triangles as a starting budget.

### The Drive URL problem
A normal Drive share link (`https://drive.google.com/file/d/FILE_ID/view`) returns an **HTML page**,
not the file. You need the direct-download form:

```
https://drive.google.com/uc?export=download&id=FILE_ID
```

**Caveats that will bite you:**
- Files above roughly 100 MB trigger Drive's virus-scan interstitial, which returns HTML with a
  confirm token instead of your file. Appending `&confirm=t` usually gets past it, but this is an
  undocumented behaviour Google has changed before.
- Rate limits and quota errors on shared links are silent-ish and return HTML, not an error code.
- File must be shared "Anyone with the link."

**More robust:** use the Drive API v3 media endpoint with your API key:
```
https://www.googleapis.com/drive/v3/files/FILE_ID?alt=media&key=YOUR_API_KEY
```

**Most robust:** don't use Drive. Put the GLB on Firebase Storage, Cloudflare R2, or any static
host. Drive is a document-sharing product, not a CDN, and it's the part of this project most likely
to break for reasons outside your control. If the "models come from Drive" requirement is Pavel's
workflow (he drops files in a folder), consider a small server that syncs Drive → proper storage.

### The loader

**File:** `Assets/Scripts/BuildingLoader.cs` — new file. One MonoBehaviour; put it on any
GameObject in the scene and set the fields in the inspector.

```csharp
using GLTFast;
using UnityEngine;
using System.Threading.Tasks;

public class BuildingLoader : MonoBehaviour
{
    public static BuildingLoader Instance;
    [SerializeField] string modelUrl;

    void Awake() => Instance = this;

    public async void LoadInto(Transform parent)
    {
        var gltf = new GltfImport();
        bool success = await gltf.Load(modelUrl);

        if (!success)
        {
            Debug.LogError("GLB load failed — check the URL returns binary, not HTML");
            return;
        }

        await gltf.InstantiateMainSceneAsync(parent);

        // Models often come in with wrong scale/origin. Normalise here.
        parent.GetChild(0).localPosition = Vector3.zero;
        parent.GetChild(0).localRotation = Quaternion.identity;
    }
}
```

**Debug tip:** if `Load` fails, `curl -L` the URL and check the first bytes. If it starts with
`<!DOCTYPE html` you've got the interstitial/permissions problem, not a glTFast problem. This
misdiagnosis costs people hours.

**Cache it.** Downloading a 30 MB model on mobile data every launch is bad. Write to
`Application.persistentDataPath` on first fetch, load from disk thereafter, with a version/etag
check.

---

## Step 9 — Manual nudge controls (do not skip this)

VPS gets you close. "Close" at building scale still looks wrong. Ship manual correction — two hours
of work, disproportionate payoff, and it's the difference between a demo that impresses and one that
makes the client wince.

**Offsets must NOT go on the anchor.** ARCore owns that transform and overwrites it on every
re-localization. That's what `NudgeRoot` is for — local offsets there ride along correctly when VPS
refines the anchor.

**File:** `Assets/Scripts/AlignmentNudge.cs` — new file. Add it to the `GeospatialController`
GameObject. Assign `arCamera` in the inspector; `nudgeRoot` is bound at runtime.

```csharp
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

public class AlignmentNudge : MonoBehaviour
{
    [SerializeField] Camera arCamera;
    [SerializeField] float panMetresPerPixel = 0.02f;

    Transform nudgeRoot;      // bound at runtime — the anchor doesn't exist at scene load
    string siteKey;
    bool dirty;

    Vector2 prevA, prevB;
    float prevTwistAngle;
    bool tracking;

    // Read these off your debug UI, then bake them into buildings.json
    public Vector3 PositionOffset { get; private set; }
    public float   HeadingOffset  { get; private set; }
    public float   HeightOffset   { get; private set; }

    void OnEnable()  { EnhancedTouchSupport.Enable(); }
    void OnDisable() { EnhancedTouchSupport.Disable(); if (dirty) Save(); }

    public void Bind(Transform root, string siteId)
    {
        nudgeRoot = root;
        siteKey = $"nudge_{siteId}";
        Load();
    }

    void Update()
    {
        if (nudgeRoot == null) return;   // anchor not resolved yet — this guard matters

        var touches = Touch.activeTouches;
        if (touches.Count != 2) { tracking = false; return; }

        Vector2 a = touches[0].screenPosition;
        Vector2 b = touches[1].screenPosition;
        float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;

        if (!tracking)
        {
            prevA = a; prevB = b; prevTwistAngle = angle;
            tracking = true;
            return;
        }

        // twist -> heading, with a dead zone (fingers rotate during any two-finger drag)
        float dAngle = Mathf.DeltaAngle(prevTwistAngle, angle);
        if (Mathf.Abs(dAngle) > 0.15f) HeadingOffset -= dAngle;

        // two-finger drag -> pan on the ground plane
        Vector2 centreDelta = ((a + b) - (prevA + prevB)) * 0.5f;
        Vector3 fwd   = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(arCamera.transform.right,   Vector3.up).normalized;

        float dist  = Vector3.Distance(arCamera.transform.position, nudgeRoot.position);
        float scale = panMetresPerPixel * Mathf.Clamp(dist / 20f, 0.5f, 4f);

        PositionOffset += (right * centreDelta.x + fwd * centreDelta.y) * scale;

        prevA = a; prevB = b; prevTwistAngle = angle;
        Apply();
    }

    public void SetHeight(float metres) { HeightOffset = metres; Apply(); }   // wire to a Slider

    void Apply()
    {
        nudgeRoot.localPosition = PositionOffset + Vector3.up * HeightOffset;
        nudgeRoot.localRotation = Quaternion.Euler(0f, HeadingOffset, 0f);
        dirty = true;
    }

    public void ResetNudge()
    {
        PositionOffset = Vector3.zero;
        HeadingOffset  = 0f;
        HeightOffset   = 0f;
        Apply();
        Save();
    }

    void Load()
    {
        PositionOffset = new Vector3(
            PlayerPrefs.GetFloat(siteKey + "_x", 0f), 0f,
            PlayerPrefs.GetFloat(siteKey + "_z", 0f));
        HeadingOffset = PlayerPrefs.GetFloat(siteKey + "_h", 0f);
        HeightOffset  = PlayerPrefs.GetFloat(siteKey + "_y", 0f);
        Apply();
        dirty = false;
    }

    void Save()
    {
        if (siteKey == null) return;
        PlayerPrefs.SetFloat(siteKey + "_x", PositionOffset.x);
        PlayerPrefs.SetFloat(siteKey + "_z", PositionOffset.z);
        PlayerPrefs.SetFloat(siteKey + "_h", HeadingOffset);
        PlayerPrefs.SetFloat(siteKey + "_y", HeightOffset);
        PlayerPrefs.Save();
        dirty = false;
    }

    // Android kills apps without reliably calling OnApplicationQuit.
    void OnApplicationPause(bool paused) { if (paused && dirty) Save(); }
    void OnApplicationFocus(bool focus)  { if (!focus && dirty) Save(); }
}
```

**File:** `GeospatialController.cs` — replaces the placement block inside `WaitForAnchor`, and adds
`[SerializeField] AlignmentNudge nudge;` plus `[SerializeField] BuildingLoader buildingLoader;`.

```csharp
var nudgeRoot = new GameObject("NudgeRoot").transform;
nudgeRoot.SetParent(result.Anchor.transform, false);

var alignmentRoot = new GameObject("AlignmentRoot").transform;
alignmentRoot.SetParent(nudgeRoot, false);
alignmentRoot.localRotation = Quaternion.Euler(0f, modelFrontOffsetDeg, 0f);

nudge.Bind(nudgeRoot, "placeholder-01");    // the id from buildings.json
buildingLoader.LoadInto(alignmentRoot);
```

### UI

A vertical `Slider` (about −5 to +5 m) wired to `SetHeight`, a **Reset** button wired to
`ResetNudge`, and **a text readout of all three offsets**.

The readout is the actual point. The nudge isn't the feature — **capturing the corrected numbers
is.** Align by eye on site, read off `heading +7.4°`, then bake 335.4 into `buildings.json` so
nobody has to nudge again. PlayerPrefs is a per-user refinement on top of a correct baseline, not
where the authoritative value lives — that's one wipe away from gone.

### Three things to get right up front

**Don't add pinch-to-scale.** It's a real building with known dimensions. If scale looks wrong,
something else is wrong. A scale control lets people "fix" a distance or altitude error by shrinking
the model, which hides the real bug and makes it unreproducible.

**Set Active Input Handling.** Project Settings → Player → Other Settings. If it's "Input Manager
(Old)", `EnhancedTouch` won't work — set it to Input System or Both.

**Twist is noisy.** Pan bleeds into heading. If the dead zone isn't enough on device, use a modal
toggle — pan mode vs rotate mode — rather than trying to separate them gesturally. Less elegant,
far more controllable when you're squinting at a phone in bright sun.

## Step 10 — Occlusion (only matters for the "existing building" case)

If the model overlays a *standing* building, without occlusion your model renders on top of
everything — including the real wall, passing cars, and people. It looks like a sticker.

- **`AROcclusionManager` + environment depth** — uses the device depth sensor/estimation. Works
  roughly 0–8 m. Fine for people walking in front of the camera, **useless at building scale.**
- **ARCore Streetscape Geometry** — this is the real answer. Google serves you actual mesh geometry
  for buildings and terrain around you. Enable it in the ARCore Extensions config, then render the
  streetscape meshes with a **depth-only / colour-write-off shader**. Real buildings then correctly
  occlude your model.

Streetscape Geometry is also useful for sanity-checking your alignment: render it in wireframe
during development and you can see whether your model lines up with Google's idea of the building.

### Step 10.5 — Ghosting the building being replaced

If the new model is **shorter or smaller** than the building already standing there, the excess real
building is still visible above and around it. Two ways to handle that.

#### Why not erase it

Hiding the real building is *diminished reality* — inpainting the region with plausible background.
On a phone, in real time, it breaks in five predictable ways:

- **Anything that isn't sky behind it.** Trees, further buildings, hills — the fill pulls the wrong
  content in and reads as a smudge.
- **Clouds move; a static patch doesn't.** Gets worse the longer anyone looks.
- **The mask is coarse.** Streetscape Geometry is roughly extruded footprints — antennas, parapets,
  rooftop plant aren't in it, so they poke through exactly at the silhouette where the eye is
  already looking.
- **Parallax.** The mask must track the real building to the pixel as you walk. Coarse mesh plus
  VPS drift means it won't, and you get flickering slivers at the edges.
- **Exposure.** The patch must track auto-exposure in lockstep with the surrounding real sky or it
  visibly floats.

A failed erasure looks like a bug. Skip it. If you ever need a true "it's gone" shot, that's offline
compositing on captured video, not real-time on a phone.

#### Ghost it instead

Render the real building's streetscape mesh as a translucent wireframe volume, with the new shorter
model solid inside it. This is what architectural practice does, and it beats erasure on more than
just difficulty:

- It reads as **deliberate** rather than broken.
- It **communicates more** — the viewer sees what's being replaced and by how much, which is usually
  the actual point of the comparison.
- It works at every viewing angle, in any lighting, against any background.

**File:** `Assets/Shaders/GhostWireframe.shader` — new file, plus a Material made from it.

```hlsl
Shader "AR/GhostWireframe"
{
    Properties
    {
        _WireColour("Wire Colour",  Color) = (0.35, 0.75, 1.0, 1.0)
        _FillColour("Fill Colour",  Color) = (0.35, 0.75, 1.0, 0.06)
        _WireWidth ("Wire Width",   Range(0.5, 4)) = 1.4
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10"
               "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha One          // additive — ghost reads as light, not paint
            ZWrite Off
            ZTest Always                // draw over the solid model so the cage stays visible
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct V2G { float4 positionCS : SV_POSITION; };
            struct G2F { float4 positionCS : SV_POSITION; float3 bary : TEXCOORD0; };

            float4 _WireColour, _FillColour;
            float  _WireWidth;

            V2G vert(Attributes IN)
            {
                V2G o;
                o.positionCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                return o;
            }

            [maxvertexcount(3)]
            void geom(triangle V2G i[3], inout TriangleStream<G2F> stream)
            {
                G2F o;
                float3 bary[3] = { float3(1,0,0), float3(0,1,0), float3(0,0,1) };
                [unroll] for (int k = 0; k < 3; k++)
                {
                    o.positionCS = i[k].positionCS;
                    o.bary = bary[k];
                    stream.Append(o);
                }
            }

            half4 frag(G2F i) : SV_Target
            {
                // Screen-space-consistent line width via derivatives
                float3 d = fwidth(i.bary);
                float3 a = smoothstep(float3(0,0,0), d * _WireWidth, i.bary);
                float  wire = 1.0 - min(min(a.x, a.y), a.z);

                half4 col = lerp(_FillColour, _WireColour, wire);
                col.a = max(_FillColour.a, wire * _WireColour.a);
                return col;
            }
            ENDHLSL
        }
    }
}
```

**Geometry shaders are not free on mobile**, and some tile-based GPUs handle them badly. If this
tanks your frame rate, the fallback is to bake barycentric coordinates into a second UV channel at
mesh-build time and drop the geometry stage — more setup, much better performance. Profile before
optimising; on a mid-tier phone with a handful of streetscape meshes it may be fine.

Assign the material to the streetscape mesh for the **target building only** — not every mesh in
range, or you get a wireframe city:

**File:** `StreetscapeShadowSetup.cs` — **replaces** the `OnChanged` method shown in Step 11.4.
Add `ghostMaterial`, `occluderMaterial`, and `targetAnchor` as serialized fields.

```csharp
void OnChanged(ARStreetscapeGeometriesChangedEventArgs args)
{
    foreach (var geometry in args.Added)
    {
        var r = geometry.GetComponentInChildren<MeshRenderer>();
        if (r == null) continue;

        // Ghost only the mesh whose bounds contain the site; occlude everything else.
        bool isTarget = r.bounds.Contains(targetAnchor.position);
        r.material = isTarget ? ghostMaterial : occluderMaterial;

        r.shadowCastingMode = isTarget
            ? ShadowCastingMode.Off      // the replaced building shouldn't cast a real shadow
            : ShadowCastingMode.On;
        r.receiveShadows = !isTarget;
    }
}
```

> **Bounds containment is a crude selector.** Streetscape meshes are often merged across several
> buildings, so you may ghost more than you meant to. If that happens, a manual toggle — tap the
> building you want ghosted — is more reliable than any automatic test, and takes ten minutes.

**Turn off shadow casting on the ghosted building.** If the building you're notionally replacing
still casts a hard real-time shadow, the scene contradicts itself. Its occlusion should usually go
too, or the ghost will occlude your new model where they overlap.

---

## Step 11 — Photorealistic rendering (matching ambient light)

> **Framing:** if your reference is M-XR's AR shoe, note that their realism is mostly an *asset*
> achievement — measured PBR material capture — on a small object at ~0.5 m. A building at 50 m is a
> different problem. Materials matter, but they're not the top lever. Below is the order that
> actually moves the needle at building scale.

### Priority order

| # | Technique | Realism gain | Effort |
|---|---|---|---|
| 1 | Contact shadows on real ground | Very high | Low |
| 2 | Occlusion (Step 10) | Very high | Medium |
| 3 | Computed sun direction | High | Low |
| 4 | Exposure matching | High | Low |
| 5 | Aerial perspective / haze | Medium-high | Low |
| 6 | Camera-artifact post | Medium | Low |
| 7 | Material quality (the M-XR part) | Medium | High |
| 8 | Reflections / probes | Low-medium at this scale | Medium |

Do 1–6 before touching 7. It's tempting to start with materials because that's the visible
"quality" knob, but a perfectly-shaded building with no shadow looks fake and a mediocre building
with a correct shadow looks real.

> **Assembled version:** Steps 11.1–11.6 show fragments so each idea sits with its explanation.
> Step 11.8 has all of them combined into one copy-pasteable `LightingController.cs`.

### Step 11.0 — Project settings that gate everything else

Get these wrong and every technique below is subtly incorrect in ways that are painful to diagnose:

- **Project Settings → Player → Other Settings → Color Space = Linear** (not Gamma)
- **URP Asset → HDR = enabled**
- **URP Asset → Shadows**: max distance ~150 m for building scale (the default ~50 m will clip your
  shadow entirely), cascades = 4

That shadow distance default is a very common cause of "why is there no shadow" at this scale.

### Step 11.1 — Sun direction: compute it, don't estimate it

ARCore's light estimation derives direction from the near-field scene. Outdoors at building scale
it's noisy and it flickers as you pan. You already have lat/lng (needed for anchoring) and the
device clock — so solve for the actual sun.

**File:** `Assets/Scripts/SolarPosition.cs` — new file. A plain static class, **not** a
MonoBehaviour — don't attach it to anything.

```csharp
using UnityEngine;

public static class SolarPosition
{
    // Low-precision NOAA algorithm. Accurate to well under a degree — far better
    // than anything light estimation gives you outdoors.
    public static void Compute(double latDeg, double lonDeg, System.DateTime utc,
                               out float azimuthDeg, out float elevationDeg)
    {
        double d = (utc - new System.DateTime(2000, 1, 1, 12, 0, 0,
                    System.DateTimeKind.Utc)).TotalDays;

        double L = (280.460 + 0.9856474 * d) % 360.0;   // mean longitude
        double g = (357.528 + 0.9856003 * d) % 360.0;   // mean anomaly
        if (L < 0) L += 360;
        if (g < 0) g += 360;

        double gRad   = g * Mathf.Deg2Rad;
        double lambda = (L + 1.915 * System.Math.Sin(gRad)
                           + 0.020 * System.Math.Sin(2 * gRad)) * Mathf.Deg2Rad;
        double eps    = (23.439 - 0.0000004 * d) * Mathf.Deg2Rad;

        double ra  = System.Math.Atan2(System.Math.Cos(eps) * System.Math.Sin(lambda),
                                       System.Math.Cos(lambda));
        double dec = System.Math.Asin(System.Math.Sin(eps) * System.Math.Sin(lambda));

        double gmst = (18.697374558 + 24.06570982441908 * d) % 24.0;
        if (gmst < 0) gmst += 24;
        double lmst = gmst * 15.0 + lonDeg;
        double ha   = (lmst - ra * Mathf.Rad2Deg) * Mathf.Deg2Rad;

        double latRad = latDeg * Mathf.Deg2Rad;
        double el = System.Math.Asin(
            System.Math.Sin(latRad) * System.Math.Sin(dec) +
            System.Math.Cos(latRad) * System.Math.Cos(dec) * System.Math.Cos(ha));
        double az = System.Math.Atan2(
            -System.Math.Sin(ha),
            System.Math.Tan(dec) * System.Math.Cos(latRad) -
            System.Math.Sin(latRad) * System.Math.Cos(ha));

        azimuthDeg   = (float)((az * Mathf.Rad2Deg + 360.0) % 360.0);
        elevationDeg = (float)(el * Mathf.Rad2Deg);
    }
}
```

Applying it to the directional light:

**File:** `Assets/Scripts/LightingController.cs` — new file (MonoBehaviour, on the XR Origin).
Needs a `[SerializeField] Light sunLight;` plus the site's `latitude`/`longitude`. Call `UpdateSun()`
once on placement, then about once a minute — not every frame.

```csharp
void UpdateSun()
{
    SolarPosition.Compute(latitude, longitude, System.DateTime.UtcNow,
                          out float az, out float el);

    if (el <= 0f) { sunLight.enabled = false; return; }  // sun below horizon
    sunLight.enabled = true;

    float azRad = az * Mathf.Deg2Rad;
    float elRad = el * Mathf.Deg2Rad;

    // ARCore geospatial world frame is EUS: +X East, +Y Up, +Z South.
    // So North is -Z. This is the direction TOWARD the sun.
    Vector3 toSun = new Vector3(
        Mathf.Cos(elRad) * Mathf.Sin(azRad),
        Mathf.Sin(elRad),
        -Mathf.Cos(elRad) * Mathf.Cos(azRad));

    sunLight.transform.rotation = Quaternion.LookRotation(-toSun);
}
```

> **Verify this on site.** The EUS convention is what ARCore documents, but sign errors here are
> easy and produce a shadow pointing the wrong way — which reads as obviously wrong to anyone
> looking at it. Test: stand at the site around local noon, drop a simple cube, and check its
> virtual shadow runs parallel to real shadows on the ground. If it's mirrored, flip the Z sign.

Call `UpdateSun()` once on placement and then maybe once a minute — not every frame. The sun moves
~0.25°/minute.

### Step 11.2 — Light estimation for colour and intensity

Use ARCore for what it's genuinely good at: how bright and what colour the ambient light is
(overcast vs golden hour vs deep shade).

Set the mode on your `ARCameraManager`:

**File:** `LightingController.cs` — same script as above. Add a
`[SerializeField] ARCameraManager cameraManager;` and set the mode in `Start()`.

```csharp
// LightEstimation is a [Flags] enum. `EnvironmentalHDR` belongs to the DEPRECATED
// LightEstimationMode enum — using it here is a compile error.
cameraManager.requestedLightEstimation =
    LightEstimation.AmbientSphericalHarmonics |
    LightEstimation.MainLightDirection |
    LightEstimation.MainLightIntensity;
```

Then:

**File:** `LightingController.cs` — same script, added below `UpdateSun()`.

```csharp
void OnEnable()  => cameraManager.frameReceived += OnFrame;
void OnDisable() => cameraManager.frameReceived -= OnFrame;

void OnFrame(ARCameraFrameEventArgs args)
{
    var le = args.lightEstimation;

    // Sun colour and intensity — but NOT direction (we compute that ourselves)
    if (le.mainLightColor.HasValue)
        sunLight.color = le.mainLightColor.Value;

    if (le.mainLightIntensityLumens.HasValue)
        sunLight.intensity = le.mainLightIntensityLumens.Value / 1000f; // tune this divisor

    // Ambient — the L2 spherical harmonics are the good bit
    if (le.ambientSphericalHarmonics.HasValue)
        RenderSettings.ambientProbe = le.ambientSphericalHarmonics.Value;

    // Drive exposure (Step 11.3)
    if (le.averageBrightness.HasValue)
        TargetExposureFromBrightness(le.averageBrightness.Value);
}
```

**Caveat:** these flags are a request, not a guarantee. Check
`cameraManager.currentLightEstimation` after the first frame — on some devices you'll silently fall
back to ambient-intensity-only, in which case `mainLightColor` and the SH will be null and the
code above quietly does nothing. Log it.

### Step 11.3 — Exposure matching (the one people skip)

Phone auto-exposure re-meters constantly. Fixed render exposure means your building drifts brighter
or darker than the background every time the camera adjusts. It reads as fake instantly even when
everything else is right.

Add a URP **Volume** with **Color Adjustments**, and drive `postExposure` from the estimated
brightness:

**File:** `LightingController.cs` — same script again. Drag your post-processing Volume into the
`volume` field in the inspector.

```csharp
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[SerializeField] Volume volume;
ColorAdjustments colorAdj;
float smoothedExposure;

void Start() => volume.profile.TryGet(out colorAdj);

void TargetExposureFromBrightness(float avgBrightness)
{
    // Calibrate this curve on device — the constants are a starting point, not gospel
    float target = Mathf.Log(Mathf.Max(avgBrightness, 0.001f) / 0.18f, 2f);

    // Heavy smoothing: matching the camera's *settled* exposure beats chasing it.
    // Chasing every frame produces visible pumping.
    smoothedExposure = Mathf.Lerp(smoothedExposure, target, Time.deltaTime * 1.5f);
    if (colorAdj != null) colorAdj.postExposure.value = smoothedExposure;
}
```

The smoothing constant matters more than the curve. Too fast and the model pulses; too slow and it
lags visibly when you pan from shade to sun. Tune on device, outdoors.

### Step 11.4 — Shadow catcher

Invisible geometry that receives shadows but writes no colour. Assign this material to your
**Streetscape Geometry** meshes (Step 10) so the model's shadow lands on real terrain and buildings.

**File:** `Assets/Shaders/ShadowCatcher.shader` — new file. Create via right-click in the Project
window → Create → Shader → Unlit Shader, then replace its whole contents. Then create a Material
from it (right-click the shader → Create → Material).

```hlsl
Shader "AR/ShadowCatcher"
{
    Properties { _ShadowStrength("Shadow Strength", Range(0,1)) = 0.7 }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-100"
               "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            float _ShadowStrength;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = p.positionCS;
                OUT.positionWS = p.positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light  mainLight   = GetMainLight(shadowCoord);
                half   atten       = mainLight.shadowAttenuation;
                return half4(0, 0, 0, (1.0 - atten) * _ShadowStrength);
            }
            ENDHLSL
        }
    }
}
```

Notes:
- `_ShadowStrength` around 0.6–0.75 usually looks right. A pure-black shadow is too strong because
  real shadows are filled by skylight.
- Tint the shadow slightly blue rather than pure black if you want to be precise — outdoor shadow
  fill is sky-coloured. Change the `half4(0,0,0, …)` to a shallow blue.
- If shadows don't appear: check URP shadow distance (Step 11.0) before debugging the shader.

#### Mutual shadowing with neighbouring buildings

Streetscape Geometry returns **building meshes, not just terrain**. So the same catcher gets you
your model shadowing the neighbours — and, more importantly, the reverse.

**The reciprocal case matters more.** If the real building next door puts your site in shade at
4 pm but your model renders in full sun, that reads as fake immediately — noticeably more than a
missing shadow *from* your model. So set the streetscape meshes to cast as well as receive:

**File:** `Assets/Scripts/StreetscapeShadowSetup.cs` — new file. Add to the XR Origin; assign the
ShadowCatcher **material** (not the shader) in the inspector.

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using Google.XR.ARCoreExtensions;

public class StreetscapeShadowSetup : MonoBehaviour
{
    [SerializeField] ARStreetscapeGeometryManager streetscapeManager;
    [SerializeField] Material shadowCatcher;   // AR/ShadowCatcher from above

    void OnEnable()  => streetscapeManager.StreetscapeGeometriesChanged += OnChanged;
    void OnDisable() => streetscapeManager.StreetscapeGeometriesChanged -= OnChanged;

    void OnChanged(ARStreetscapeGeometriesChangedEventArgs args)
    {
        foreach (var geometry in args.Added)
            Configure(geometry);
    }

    void Configure(ARStreetscapeGeometry geometry)
    {
        var renderer = geometry.GetComponentInChildren<MeshRenderer>();
        if (renderer == null) return;

        renderer.material = shadowCatcher;

        // Receive your model's shadow AND cast onto it.
        // ShadowsOnly = contributes to the shadow map but writes no colour,
        // which is what you want for geometry standing in for the real world.
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
    }
}
```

> **API name check:** `ARStreetscapeGeometryManager`, the event name, and the args type have moved
> around across Extensions versions — verify against your installed package. The *shape* (subscribe
> to a changed event, configure the added meshes) is stable.

**Four things that bite:**

**The meshes are coarse.** Streetscape Geometry is roughly extruded footprints — flat tops, no
balconies, setbacks, or facade relief. Your shadow lands on an approximation. Fine at distance,
visibly wrong when the shadow falls across a facade with real depth.

**Shadow distance vs resolution is the real constraint.** A shadow landing on a building 80 m away
needs URP shadow distance ≥ 80 m, and a 2048 shadow map stretched over that range goes soft and
blocky. This limits you more than anything else in this section. Tune shadow distance to the actual
geometry of your site — don't set it large "to be safe," you're spending resolution you need.

**Range limit.** Streetscape Geometry is only served within a radius of the camera. A tall building
200 m away that should be shading your site has no mesh, so it neither casts nor receives.

**Cost.** Making these meshes casters *and* receivers adds real draw calls and shadow-map work.
This is a Tier A / Tier B feature (Step 13) — on the low tier, keep ground shadow only.

**Shadow acne:** vertical surfaces near-parallel to the sun direction will show it. Raise **normal
bias** in the URP shadow settings before concluding the shader is broken. This is the expected
first symptom, not a sign something is wrong.

### Step 11.5 — Aerial perspective

At 50–200 m real objects desaturate and shift toward sky colour. Full-saturation CG looks pasted on
even with correct geometry and lighting. This is cheap and it's a large part of why matte paintings
read as real.

- URP Asset / Lighting settings → enable **Fog**, Linear mode
- Start ~30 m, End ~400 m — tune to your actual viewing distance
- Fog colour: don't hardcode it. Derive from the light estimation ambient (roughly the sky
  contribution), or sample the camera image directly:

**File:** `LightingController.cs` — same script. Runs on a timer, not per frame.

```csharp
// Sample the top strip of the camera feed for sky colour.
// Do this every ~30 frames, NOT per frame — CPU image acquisition is expensive.
if (cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
{
    using (image)
    {
        // Downsample hard (e.g. 32x32), average the top rows, assign to RenderSettings.fogColor
    }
}
```

### Step 11.6 — Camera-artifact post-processing

Your render is too clean. The camera feed is soft, noisy, slightly aberrated. On the same Volume:

| Override | Setting |
|---|---|
| Tonemapping | **Neutral** |
| Film Grain | Thin type, intensity 0.15–0.3, raised in low light |
| Chromatic Aberration | 0.05–0.15 (subtle) |
| Bloom | Low threshold, low intensity |

**Tonemapping subtlety:** the camera feed arrives *already tonemapped* by the phone's ISP. ACES
applied on top creates a mismatch against the background. Neutral generally sits closer to phone
camera response — but eyeball this on device rather than trusting the recommendation.

**Real complication worth knowing up front:** in AR Foundation the camera background is rendered
into the frame, so full-screen post-processing affects the *camera feed too*, not just your model.
Grain gets applied to already-grainy footage.

- **In practice this is mostly fine** and I'd ship it. Slight double-grain is far less noticeable
  than no grain.
- If it isn't good enough, the fix is rendering the model to a separate render texture via a second
  camera, or a custom URP Renderer Feature with a layer mask, and compositing. That's a real chunk
  of work — don't start there.

Also add a slight blur or reduced sharpening on the model. A razor-sharp CG silhouette cutting
against soft photographic background is a strong fake cue.

### Step 11.7 — Materials (the M-XR part)

Now the asset work. The specific problem: **Revit and SketchUp exports have effectively no usable
PBR data** — flat diffuse colours, no roughness variation, no normal detail. Whatever comes out of
Pavel's pipeline, you will likely be authoring materials from scratch.

For each surface type you need real measured-ish values, not eyeballed ones:
- Concrete: roughness ~0.7–0.9, non-metallic, needs normal/detail maps at close range
- Metal cladding: metallic 1.0, roughness driven by finish (brushed vs anodised differ a lot)
- Glass: the hard case, see below

**Glass is where this will look weakest, and that's expected.** Real building glass mirrors its
surroundings. Getting that right needs reflection probes fed from the camera feed
(`AREnvironmentProbeManager`), which only capture what the camera has already seen — so they're
always partial and often stale. Budget for glass being the compromise, and consider art-directing
around it (slightly more opaque/tinted glass reads better than bad reflections).

### Step 11.8 — The complete `LightingController.cs`

Steps 11.1–11.6 are split into fragments so each idea is explained where it belongs. This is all of
them assembled into one file you can paste straight in.

**File:** `Assets/Scripts/LightingController.cs` — new file. Add it to the **XR Origin**. Assign
`sunLight` (your Directional Light), `cameraManager` (on the AR Camera), and `postVolume`
(your post-processing Volume) in the inspector.

Requires `SolarPosition.cs` from Step 11.1.

```csharp
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class LightingController : MonoBehaviour
{
    [Header("Scene references")]
    [SerializeField] Light sunLight;
    [SerializeField] ARCameraManager cameraManager;
    [SerializeField] Volume postVolume;

    [Header("Site (must match buildings.json)")]
    [SerializeField] double latitude  = 32.081234;
    [SerializeField] double longitude = 34.812345;

    [Header("Sun")]
    [Tooltip("Sun moves ~0.25 deg/min. No need to recompute every frame.")]
    [SerializeField] float sunUpdateIntervalSeconds = 60f;
    [Tooltip("Lumens -> Unity intensity. Calibrate on device; this is a starting point.")]
    [SerializeField] float intensityDivisor = 1000f;

    [Header("Exposure matching")]
    [SerializeField] bool  driveExposure    = true;
    [Tooltip("Lower = smoother. Too fast and the model visibly pulses.")]
    [SerializeField] float exposureSmoothing = 1.5f;
    [SerializeField] float middleGrey        = 0.18f;

    [Header("Aerial perspective")]
    [SerializeField] bool driveFogColour          = true;
    [SerializeField] int  skySampleIntervalFrames = 30;
    [Range(0, 0.5f)]
    [SerializeField] float skySampleTopFraction   = 0.2f;

    ColorAdjustments colorAdjustments;
    float smoothedExposure;
    float sunTimer;
    int   frameCounter;
    bool  loggedEstimationMode;

    // Wire this to a debug Text element — you will want it on site.
    public string DebugReadout { get; private set; } = "";

    void Start()
    {
        if (postVolume != null && !postVolume.profile.TryGet(out colorAdjustments))
            Debug.LogWarning("LightingController: Volume has no Color Adjustments override — " +
                             "exposure matching will do nothing.");

        // Flags enum. NOT LightEstimation.EnvironmentalHDR — that member lives on the
        // deprecated LightEstimationMode enum and will not compile here.
        if (cameraManager != null)
            cameraManager.requestedLightEstimation =
                LightEstimation.AmbientSphericalHarmonics |
                LightEstimation.MainLightDirection |
                LightEstimation.MainLightIntensity;

        UpdateSun();
    }

    void OnEnable()
    {
        if (cameraManager != null) cameraManager.frameReceived += OnFrame;
    }

    void OnDisable()
    {
        if (cameraManager != null) cameraManager.frameReceived -= OnFrame;
    }

    void Update()
    {
        sunTimer += Time.deltaTime;
        if (sunTimer >= sunUpdateIntervalSeconds)
        {
            sunTimer = 0f;
            UpdateSun();
        }
    }

    // ---------------------------------------------------------------- sun

    void UpdateSun()
    {
        if (sunLight == null) return;

        SolarPosition.Compute(latitude, longitude, System.DateTime.UtcNow,
                              out float az, out float el);

        if (el <= 0f)                       // below the horizon
        {
            sunLight.enabled = false;
            return;
        }
        sunLight.enabled = true;

        float azRad = az * Mathf.Deg2Rad;
        float elRad = el * Mathf.Deg2Rad;

        // ARCore geospatial world frame is EUS: +X East, +Y Up, +Z South => North is -Z.
        // This is the direction TOWARD the sun.
        Vector3 toSun = new Vector3(
            Mathf.Cos(elRad) * Mathf.Sin(azRad),
            Mathf.Sin(elRad),
           -Mathf.Cos(elRad) * Mathf.Cos(azRad));

        sunLight.transform.rotation = Quaternion.LookRotation(-toSun);
    }

    // ---------------------------------------------------- per-frame estimation

    void OnFrame(ARCameraFrameEventArgs args)
    {
        if (!loggedEstimationMode)
        {
            loggedEstimationMode = true;
            Debug.Log($"Light estimation granted: {cameraManager.currentLightEstimation}");
        }

        var le = args.lightEstimation;

        // Sun COLOUR and INTENSITY from estimation — direction stays computed.
        if (sunLight != null)
        {
            if (le.mainLightColor.HasValue)
                sunLight.color = le.mainLightColor.Value;

            if (le.mainLightIntensityLumens.HasValue)
                sunLight.intensity = le.mainLightIntensityLumens.Value / intensityDivisor;
        }

        // Ambient — the L2 spherical harmonics are the useful part.
        if (le.ambientSphericalHarmonics.HasValue)
            RenderSettings.ambientProbe = le.ambientSphericalHarmonics.Value;

        if (driveExposure && le.averageBrightness.HasValue)
            DriveExposure(le.averageBrightness.Value);

        if (driveFogColour && ++frameCounter % skySampleIntervalFrames == 0)
            SampleSkyColour();

        DebugReadout =
            $"est: {cameraManager.currentLightEstimation}\n" +
            $"exposure: {smoothedExposure:F2} EV\n" +
            $"sun: {(sunLight != null && sunLight.enabled ? "up" : "down")}";
    }

    // ------------------------------------------------------------ exposure

    void DriveExposure(float averageBrightness)
    {
        float target = Mathf.Log(Mathf.Max(averageBrightness, 0.001f) / middleGrey, 2f);

        // Heavy smoothing: match the camera's SETTLED exposure. Chasing it per frame pumps.
        smoothedExposure = Mathf.Lerp(smoothedExposure, target,
                                      Time.deltaTime * exposureSmoothing);

        if (colorAdjustments != null)
            colorAdjustments.postExposure.value = smoothedExposure;
    }

    // ------------------------------------------------------ aerial perspective

    void SampleSkyColour()
    {
        if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return;

        using (image)
        {
            const int N = 32;                       // downsample hard — this runs on the CPU
            var conversion = new XRCpuImage.ConversionParams
            {
                inputRect        = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(N, N),
                outputFormat     = TextureFormat.RGBA32,
                transformation   = XRCpuImage.Transformation.None
            };

            int size = image.GetConvertedDataSize(conversion);
            var buffer = new NativeArray<byte>(size, Allocator.Temp);
            try
            {
                image.Convert(conversion, buffer);

                int rows = Mathf.Max(1, Mathf.RoundToInt(N * skySampleTopFraction));
                long r = 0, g = 0, b = 0;
                int  n = 0;

                for (int y = 0; y < rows; y++)
                for (int x = 0; x < N; x++)
                {
                    int i = (y * N + x) * 4;
                    r += buffer[i]; g += buffer[i + 1]; b += buffer[i + 2];
                    n++;
                }

                var sky = new Color(r / (n * 255f), g / (n * 255f), b / (n * 255f), 1f);
                RenderSettings.fogColor = Color.Lerp(RenderSettings.fogColor, sky, 0.25f);
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }
}
```

#### Four things to check when you first run it

**Which end of the image is "up".** `Transformation.None` means row 0 may be the top *or* the
bottom depending on device and screen orientation. If your fog turns brown, you are averaging the
road, not the sky — flip the loop to read the last `rows` rows instead.

**These flags are a request, not a guarantee.** The `Debug.Log` on the first frame prints what
was actually granted. On devices that fall back, `mainLightColor` and the spherical harmonics come
back null and those lines silently do nothing — no error, just flat ambient.

**`intensityDivisor` and `middleGrey` are uncalibrated.** They're plausible starting points, not
measured values. Tune outdoors, on device, watching the model against the real background.

**`exposureSmoothing` matters more than the exposure curve.** Too fast and the model pulses as
auto-exposure re-meters; too slow and it lags visibly when you pan from shade into sun. This is the
one number worth spending real time on.

### Performance reality check

You are asking a phone to run, simultaneously: VPS localization, streetscape geometry, depth
occlusion, real-time shadows at 150 m, and post-processing — while held up outdoors in direct sun.
Thermal throttling is a hard limit here, not a theoretical one.

Profile early on the actual target device, in the actual heat. When you have to cut, my suggested
order:
1. Reflection probes (least payoff per ms at this scale)
2. Depth occlusion (keep streetscape occlusion, drop per-frame device depth)
3. Shadow cascade count 4 → 2
4. Post-processing effects individually

Keep shadows and exposure matching to the end. They're cheap and they're doing most of the work.

---

## Step 12 — Test loop

**In the Editor:** ARCore's **Geospatial Creator** lets you place and preview geospatial content
against Google's photorealistic 3D tiles inside the Unity Editor. Enable it in the Extensions
settings; it needs the **Map Tiles API** enabled on the same Cloud project. This is the only way to
iterate on positioning without walking to the site, and it's worth the setup.

It is *not* a substitute for on-site testing — the tiles are a model of reality, not reality.

**On device:** `File → Build Settings → Android → Build and Run` with the phone in developer mode.

**On-site reality checks that catch people out:**
- VPS won't localize through glass. Don't test from a car or a window.
- Poor light, heavy rain, and featureless facades all degrade it.
- If Street View imagery at the site is 8 years stale and the streetscape changed, localization
  quality drops.
- Put a live debug overlay in the app showing `EarthTrackingState`, horizontal accuracy, and yaw
  accuracy. Debugging geospatial without visible numbers is guesswork.

---

## Rough effort estimate

| Phase | Time |
|---|---|
| Steps 1–6 (setup, packages, scene) | half a day — *if* versions match; a full day if they don't |
| Step 7 (geospatial anchoring working on device) | 1–2 days |
| Step 8 (Drive → GLB pipeline, incl. model conversion/decimation) | 1 day, more if models are heavy Revit exports |
| Steps 9–10 (nudge UX + occlusion) | 2–3 days |
| On-site tuning | open-ended |

The setup is the fast part. Alignment quality is where the time actually goes.

---

## Step 13 — Scaling quality across devices

Yes, the spread is large. ARCore-certified devices run from budget Snapdragon 4-series up to
current flagships, and the GPU gap across that range is easily 10x. A scene that holds 60 fps on a
flagship can sit at 12 fps on a mid-range phone from three years ago. You have to tier.

### The AR-specific twist: stability beats peak

In normal games, higher frame rate is straightforwardly better. In AR it's different — **frame rate
*consistency* matters more than the number**, because judder makes the model appear to swim
relative to the real world, which destroys the registration illusion far more than low fps does.

A locked, stable 30 fps looks *more* convincing than a fluctuating 40–55. So:

**File:** `AdaptiveQuality.cs` (or any `Start()` that runs once).

```csharp
Application.targetFrameRate = 30;
```

is a legitimate default for all tiers, not a compromise. The camera feed is often 30 fps anyway, and
capping frees a lot of thermal headroom.

### Benchmark sustained, not peak

Phones throttle hard after 5–10 minutes held up outdoors in direct sun. A benchmark taken in the
first 30 seconds indoors tells you almost nothing about how the app behaves in real use. **Test for
ten minutes, outside, in the sun**, and tune to what the device does at minute eight — not minute
one.

This is the single most commonly skipped step in mobile AR perf work, and it's why apps that
demoed fine fall apart at a site visit.

### Tier definitions

Create three URP Assets under **Project Settings → Quality**, each with its own renderer:

| | **Tier A** flagship | **Tier B** mid | **Tier C** low |
|---|---|---|---|
| Render scale | 1.0 | 0.85 | 0.7 |
| Target fps | 30 (or 60) | 30 | 30 |
| Shadow cascades | 4 | 2 | 1 |
| Shadow distance | 150 m | 100 m | 60 m |
| Shadow resolution | 2048 | 1024 | 512 |
| Depth occlusion | on | streetscape only | streetscape only |
| Reflection probes | on | off | off |
| MSAA | 4x | 2x | off |
| Post: grain + tonemap | yes | yes | yes |
| Post: chromatic aberration, bloom | yes | bloom only | no |

**Render scale is the single biggest lever.** Dropping to 0.7 is roughly half the pixels, and on a
phone screen held at arm's length it's much less noticeable than you'd expect — especially since the
camera feed behind it is soft anyway.

**Keep shadows and exposure matching at every tier.** They're carrying most of the realism (Step 11)
and they're comparatively cheap. If Tier C can't afford real-time shadows, degrade to a soft blob
shadow projector rather than removing shadow entirely — a crude shadow beats none by a wide margin.

### Detecting the tier

Don't maintain a device-name allowlist. It's a treadmill and it's wrong for phones released after
you ship. Use a rough capability guess for the *initial* tier, then correct from measured
performance.

**File:** `Assets/Scripts/AdaptiveQuality.cs` — new file. Put it on a GameObject that persists for
the whole session (the XR Origin is fine).

```csharp
using UnityEngine;
using UnityEngine.Rendering;

public class AdaptiveQuality : MonoBehaviour
{
    [SerializeField] int startTier = -1;      // -1 = auto-guess
    int tier;
    float accum; int samples;
    const float Window = 5f;                  // seconds per evaluation
    const float BudgetMs = 36f;               // 30fps + headroom

    void Start()
    {
        Application.targetFrameRate = 30;
        tier = startTier >= 0 ? startTier : GuessTier();
        QualitySettings.SetQualityLevel(tier, true);
    }

    static int GuessTier()
    {
        // Crude but adequate as a starting point — measurement corrects it below.
        int mem = SystemInfo.systemMemorySize;          // MB
        int cores = SystemInfo.processorCount;
        if (mem >= 7000 && cores >= 8) return 0;        // Tier A
        if (mem >= 4000) return 1;                      // Tier B
        return 2;                                       // Tier C
    }

    void Update()
    {
        accum += Time.unscaledDeltaTime * 1000f;
        samples++;
        if (accum < Window * 1000f) return;

        float avgMs = accum / samples;
        accum = 0; samples = 0;

        // Step DOWN only. Stepping back up causes visible oscillation as the
        // device heats and cools — pick a floor and stay there.
        if (avgMs > BudgetMs && tier < 2)
        {
            tier++;
            QualitySettings.SetQualityLevel(tier, true);
            Debug.Log($"Dropped to quality tier {tier} (avg {avgMs:F1} ms)");
        }
    }
}
```

**Why down-only:** as the phone heats, performance degrades; as it cools, it recovers. A
bidirectional controller will oscillate between tiers, and a visible quality flip mid-session is
worse than just running at the lower tier. Pick a floor and stay.

Skip the first ~10 seconds before evaluating — ARCore's initial VPS localization is a genuine spike
and will push you down a tier for no good reason.

### Unity's Adaptive Performance package

Worth knowing about: `com.unity.adaptiveperformance` exposes real thermal and CPU/GPU bottleneck
data rather than inferring from frame time, which is strictly better information.

**The catch:** it needs a hardware provider, and provider coverage is limited — the Samsung Android
provider is the mature one, with Qualcomm support more recent. On a device without a provider you
fall back to a generic mode that gives you much less. Worth adding if your target devices are
Samsung; not worth blocking on otherwise.

*I haven't verified current provider coverage — check the package docs against your actual target
devices before committing to it.*

### What ARCore costs you regardless

There's a floor you can't optimise below. Streetscape Geometry, depth, and VPS localization all
have fixed costs, and on a low-tier device they may consume most of your budget before your own
rendering starts. If Tier C still can't hold 30 fps with everything cut, the honest answer is that
the device is out of scope — not that you need to optimise harder.

---

1. **Android, iOS, or both?** iOS adds a Mac requirement and ARCore Extensions on iOS is a slightly
   less-travelled path.
2. **Is VPS available at the site?** Blocking — check first (Step 0).
3. **Existing building or empty lot?** Determines whether occlusion and tight alignment are
   required.
4. **What format are Pavel's models in, and how large?** If they're Revit at full LOD, the
   conversion/decimation pipeline is a project in itself.
5. **Who picks which model to show?** One fixed building, or a list the user chooses from? Affects
   whether you need a manifest file alongside the GLBs.
