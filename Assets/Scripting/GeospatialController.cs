using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Waits for VPS localization to get good enough, resolves a terrain anchor, then builds
/// the placement hierarchy (Steps 7 and 9):
///
///     BuildingAnchor   ARCore owns this transform; never write to it
///     └ NudgeRoot      AlignmentNudge writes manual offsets here
///       └ AlignmentRoot  BuildingPlacement applies modelFrontOffsetDeg here
///         └ [GLB]
/// </summary>
public class GeospatialController : MonoBehaviour
{
    [Header("AR references")]
    [SerializeField] AREarthManager earthManager;
    [SerializeField] ARAnchorManager anchorManager;

    [Header("Placement")]
    [SerializeField] BuildingPlacement placement;
    [SerializeField] AlignmentNudge nudge;
    [SerializeField] BuildingLoader buildingLoader;

    [Tooltip("Optional. Told which anchor to ghost once it resolves.")]
    [SerializeField] StreetscapeShadowSetup streetscapeShadows;

    [Header("Site")]
    [Tooltip("Key for this site's saved nudge offsets — the id from buildings.json.")]
    [SerializeField] string siteId = "placeholder-01";

    // Get these from Google Earth / a survey, not from a phone GPS reading.
    [SerializeField] double latitude;
    [SerializeField] double longitude;
    [Tooltip("Metres above ground level at that lat/lng.")]
    [SerializeField] double altitudeAboveTerrain = 0;

    [Header("Localization gate")]
    // Google documents ~5 m / ~5 deg as TYPICAL VPS accuracy, so a 2 m gate can simply never
    // open — which looks identical to "broken". Start loose, watch the readout, then tighten.
    [Tooltip("Don't place until horizontal accuracy is at least this good, in metres.")]
    [SerializeField] double maxHorizontalAccuracy = 5.0;
    [Tooltip("Don't place until yaw accuracy is at least this good, in degrees.")]
    [SerializeField] double maxYawAccuracy = 15.0;

    [Header("Bring-up / debug")]
    [Tooltip("Skip localization entirely and place the model at a fixed offset. Use this to " +
             "prove the model, materials and hierarchy work — in the Editor, or indoors on " +
             "device. TURN THIS OFF before testing real geospatial placement.")]
    [SerializeField] bool debugPlaceWithoutLocalization = false;

    [Tooltip("Where to put the model in debug mode, relative to world origin.")]
    [SerializeField] Vector3 debugPlaceOffset = new Vector3(0f, 0f, 60f);

    [Header("Preview mode — see the building from anywhere (indoors, off site)")]
    [Tooltip("Skip localization and drop a scale model of the building in the room with " +
             "you. It is shrunk so it subtends exactly the angle the real building would " +
             "from 'preview view distance' away, i.e. it looks like the real thing seen " +
             "from across a clear open space. Toggleable at runtime from the HUD.")]
    [SerializeField] bool previewMode = false;

    [Tooltip("The distance you are PRETENDING to stand at, in metres. This is what sets " +
             "the apparent size. 120 m ≈ across a city block.")]
    [SerializeField] float previewViewDistance = 120f;

    [Tooltip("How far in front of you the scale model actually sits, in metres. Keep it " +
             "smaller than the room you are standing in.")]
    [SerializeField] float previewStandoff = 2.5f;

    [Tooltip("Assumed eye height, metres. The miniature's base is dropped by this much " +
             "(scaled), so you look at it from the same angle as the real thing.")]
    [SerializeField] float previewEyeHeight = 1.6f;

    [Tooltip("Camera used to position the preview. Falls back to Camera.main.")]
    [SerializeField] Camera previewCamera;

    [Tooltip("AR/ShadowCatcher material. Preview has no ground, so without a surface under " +
             "the model there is nothing for its shadow to land on and it looks like " +
             "shadows are broken. Assign Assets/Shaders/ShadowCatcher.mat.")]
    [SerializeField] Material shadowCatcherMaterial;

    [Tooltip("Size of that invisible ground patch, as a multiple of the model's footprint.")]
    [SerializeField] float shadowGroundScale = 3f;

    [Tooltip("On placement, size the model so its height is this fraction of the distance " +
             "to it. 0.5 puts the whole building comfortably on screen and guarantees you " +
             "are never standing inside it. 0 = never auto-size.")]
    [SerializeField] float previewFitFraction = 0.5f;

    [Tooltip("Furthest a floor tap may place the model, in metres. Stops a near-horizontal " +
             "tap from landing it through the wall on the far side of the room.")]
    [SerializeField] float maxPlacementDistance = 8f;

    [Tooltip("Echo the status to the log once a second. A poor man's on-screen readout — " +
             "watch it with: adb logcat -s Unity:V")]
    [SerializeField] bool logStatus = true;

    [Header("Location service")]
    [Tooltip("Geospatial needs device location. Google's own sample starts Unity's location " +
             "service; without it Earth can sit at Tracking=None indefinitely.")]
    [SerializeField] bool startLocationService = true;

    public enum Phase { WaitingForEarth, WaitingForAccuracy, ResolvingAnchor, AnchorFailed, Placed }

    /// <summary>Where placement has got to. The HUD reads this to explain a no-show.</summary>
    public Phase CurrentPhase { get; private set; } = Phase.WaitingForEarth;

    /// <summary>The resolved anchor, so the HUD can report distance and bearing to it.</summary>
    public Transform AnchorTransform { get; private set; }

    bool _placed;
    bool _siteReady;   // buildings.json read; nothing places before this
    float _logTimer;
    string _locationStatus = "not started";

    // Everything we created under whatever we anchored to, so a mode switch can tear it
    // down again without touching the ARCore-owned anchor.
    GameObject _hierarchyRoot;
    GameObject _syntheticAnchor;   // a stand-in anchor we made ourselves, so ours to destroy
    Transform _previewRoot;
    bool _previewPlacePending;
    float _previewEyeY;   // camera height when the preview was placed, for the base offset

    // Floor placement: the model stands on a tapped point instead of floating at the
    // eye-level standoff, and the scale ratio is taken from that point's real distance.
    bool _previewOnFloor;
    Vector3 _previewFloorPoint;
    float _previewRefDistance;
    ARPlaneManager _planeManager;

    /// <summary>
    /// Wire this to a debug Text element. Debugging geospatial without visible numbers
    /// is guesswork (Step 12).
    /// </summary>
    public string DebugReadout { get; private set; } = "waiting for Earth tracking";

    void Start()
    {
        if (startLocationService) StartCoroutine(RunLocationService());

        StartCoroutine(LoadSiteThenBegin());
    }

    /// <summary>
    /// Site data comes from buildings.json before anything is placed, so the coordinates
    /// live in a reviewable file rather than only inside the scene. Placement waits for it —
    /// resolving an anchor at the inspector's coordinates first would put the building in
    /// the wrong place for a second and then move it.
    /// </summary>
    System.Collections.IEnumerator LoadSiteThenBegin()
    {
        SiteCatalog.Site site = null;
        yield return SiteCatalog.Load(siteId, loaded => site = loaded);

        if (site != null)
        {
            if (placement != null) placement.ApplySite(site);
            if (buildingLoader != null) buildingLoader.ApplySite(site);

            if (!site.HasFootprint && System.Math.Abs(site.latitude) > 0.0001)
            {
                latitude = site.latitude;
                longitude = site.longitude;
            }

            altitudeAboveTerrain = site.altitudeAboveTerrain;
        }

        _siteReady = true;

        if (previewMode)
        {
            EnterPreview();
            yield break;
        }

        if (debugPlaceWithoutLocalization) PlaceWithoutLocalization();
    }

    /// <summary>
    /// Re-reads buildings.json and re-places from scratch. With the device copy taking
    /// priority, this turns "the heading is wrong" into an adb push and a button press
    /// instead of a rebuild and a second trip to the site.
    /// </summary>
    public void ReloadSite() => StartCoroutine(ReloadRoutine());

    System.Collections.IEnumerator ReloadRoutine()
    {
        SiteCatalog.Site site = null;
        yield return SiteCatalog.Load(siteId, loaded => site = loaded);

        if (site != null)
        {
            if (placement != null) placement.ApplySite(site);
            if (buildingLoader != null) buildingLoader.ApplySite(site);

            if (!site.HasFootprint && System.Math.Abs(site.latitude) > 0.0001)
            {
                latitude = site.latitude;
                longitude = site.longitude;
            }

            altitudeAboveTerrain = site.altitudeAboveTerrain;
        }

        bool wasPreview = _previewRoot != null;
        TearDownHierarchy();

        if (wasPreview)
        {
            EnterPreview();
            yield break;
        }

        // Re-arm so the gate resolves a fresh anchor at whatever the file now says.
        _placed = false;
        CurrentPhase = Phase.WaitingForEarth;
        DebugReadout = $"site reloaded from {SiteCatalog.LastSource} — re-placing";
    }

    /// <summary>No ARCore, no VPS, no waiting — just build the hierarchy somewhere visible.</summary>
    void PlaceWithoutLocalization()
    {
        var debugAnchor = new GameObject("DebugAnchor").transform;
        debugAnchor.SetPositionAndRotation(debugPlaceOffset, placement.AnchorRotation);
        _syntheticAnchor = debugAnchor.gameObject;

        BuildHierarchy(debugAnchor);
        _placed = true;
        CurrentPhase = Phase.Placed;
        DebugReadout = "DEBUG: placed without localization";
    }

    /// <summary>
    /// ARCore needs the device's location, and the OS only supplies it reliably while
    /// something is actively requesting it. Mirrors Google's Geospatial sample.
    /// </summary>
    System.Collections.IEnumerator RunLocationService()
    {
        // Input.location is the legacy LocationService; it is not reimplemented by the new
        // Input System, but guard anyway so a throw can't take out placement entirely.
        bool enabledByUser;
        try
        {
            enabledByUser = UnityEngine.Input.location.isEnabledByUser;
        }
        catch (System.Exception e)
        {
            _locationStatus = "unavailable";
            Debug.LogWarning($"Location service unavailable: {e.Message}");
            yield break;
        }

        if (!enabledByUser)
        {
            // Device-level Location toggle is OFF. Geospatial cannot work in this state.
            _locationStatus = "DISABLED on device — turn Location on";
            Debug.LogError("Location is disabled on the device. Geospatial will never track.");
            yield break;
        }

        UnityEngine.Input.location.Start();
        _locationStatus = "starting";

        int guard = 0;
        while (UnityEngine.Input.location.status == LocationServiceStatus.Initializing && guard < 200)
        {
            guard++;
            yield return new WaitForSeconds(0.1f);
        }

        _locationStatus = UnityEngine.Input.location.status.ToString();
        Debug.Log($"[Geospatial] location service: {_locationStatus}");
    }

    void Update()
    {
        // The AR camera has no real pose on the frame the session starts, so the preview is
        // positioned one frame after it is asked for.
        if (_previewPlacePending)
        {
            _previewPlacePending = false;
            PositionPreview();
        }

        if (_placed)
        {
            // Preview skips localization, but it must not go blind to it: whether a fix has
            // arrived is exactly what decides if a save can record real coordinates.
            if (_previewRoot != null) RefreshPreviewEarthStatus();
            return;
        }

        if (!_siteReady) return;   // don't anchor at inspector coords the file will replace

        // EarthState is the diagnostic that matters: it separates "API key rejected" and
        // "Geospatial disabled" from "still warming up". EarthTrackingState collapses them
        // all into None, which is why a stuck session looks identical to a broken one.
        var earthState = earthManager.EarthState;
        if (earthState != EarthState.Enabled)
        {
            CurrentPhase = Phase.WaitingForEarth;
            DebugReadout = $"EarthState: {earthState}\nlocation: {_locationStatus}";
            return;
        }

        if (earthManager.EarthTrackingState != TrackingState.Tracking)
        {
            CurrentPhase = Phase.WaitingForEarth;
            DebugReadout = $"EarthState: Enabled\n" +
                           $"tracking: {earthManager.EarthTrackingState}\n" +
                           $"location: {_locationStatus}\n" +
                           "Point at building facades and walk sideways";
            return;
        }

        var pose = earthManager.CameraGeospatialPose;

        DebugReadout =
            $"tracking: Tracking\n" +
            $"lat/lng: {pose.Latitude:F6}, {pose.Longitude:F6}\n" +
            $"horizontal: {pose.HorizontalAccuracy:F2} m (need <= {maxHorizontalAccuracy})\n" +
            $"yaw: {pose.OrientationYawAccuracy:F1}° (need <= {maxYawAccuracy})";

        if (pose.HorizontalAccuracy > maxHorizontalAccuracy ||
            pose.OrientationYawAccuracy > maxYawAccuracy)
        {
            CurrentPhase = Phase.WaitingForAccuracy;
            return;
        }

        _placed = true;
        CurrentPhase = Phase.ResolvingAnchor;
        PlaceBuilding();
    }

    /// <summary>
    /// Keeps the Earth readout live while preview is up. Device GPS and a VPS fix are very
    /// different things — the phone can know its coordinates to within a few metres while
    /// ARCore still has no idea where the camera is, and only the latter is good enough to
    /// place a building — so both are reported.
    /// </summary>
    void RefreshPreviewEarthStatus()
    {
        if (earthManager == null) return;

        var earthState = earthManager.EarthState;
        var tracking = earthManager.EarthTrackingState;

        string line;
        if (earthState != EarthState.Enabled)
        {
            line = $"Earth: {earthState}";
        }
        else if (tracking != TrackingState.Tracking)
        {
            line = $"VPS: {tracking} — no fix yet\nlocation service: {_locationStatus}";
        }
        else
        {
            var pose = earthManager.CameraGeospatialPose;
            line = $"VPS: Tracking  {pose.Latitude:F6}, {pose.Longitude:F6}\n" +
                   $"±{pose.HorizontalAccuracy:F1} m (need ≤{maxHorizontalAccuracy}), " +
                   $"yaw ±{pose.OrientationYawAccuracy:F0}°";
        }

        DebugReadout = "preview: placed by hand, not by VPS\n" + line;
    }

    void LateUpdate()
    {
        if (!logStatus || _placed) return;

        _logTimer += Time.deltaTime;
        if (_logTimer < 1f) return;

        _logTimer = 0f;
        Debug.Log($"[Geospatial] {DebugReadout.Replace('\n', ' ')}");
    }

    void PlaceBuilding()
    {
        // Rotation 1: where the building faces in the world. BuildingPlacement owns the
        // heading -> EUS quaternion conversion so there is one place to touch on site.
        var rotation = placement.AnchorRotation;

        // Footprint mode anchors at corner A, so the coordinate and the heading come from
        // the same pair of measurements and can't disagree.
        double lat = latitude, lng = longitude;
        if (placement.TryGetAnchorLatLng(out double footLat, out double footLng))
        {
            lat = footLat;
            lng = footLng;
        }

        // A saved on-site alignment outranks both: those coordinates were measured by
        // standing there and lining the building up, which beats anything typed in.
        if (nudge != null && nudge.TryGetSavedCoordinates(siteId, out double savedLat, out double savedLng))
        {
            lat = savedLat;
            lng = savedLng;
            Debug.Log($"[Geospatial] using saved coordinates {lat:F7}, {lng:F7} for '{siteId}'");
        }

        var promise = anchorManager.ResolveAnchorOnTerrainAsync(
            lat, lng, altitudeAboveTerrain, rotation);

        StartCoroutine(WaitForAnchor(promise));
    }

    System.Collections.IEnumerator WaitForAnchor(ResolveAnchorOnTerrainPromise promise)
    {
        yield return promise;
        var result = promise.Result;

        if (result.TerrainAnchorState != TerrainAnchorState.Success || result.Anchor == null)
        {
            CurrentPhase = Phase.AnchorFailed;
            DebugReadout = $"terrain anchor FAILED: {result.TerrainAnchorState}";
            Debug.LogError($"Terrain anchor failed: {result.TerrainAnchorState}");

            // Let the gate re-arm so a later, better localization can try again.
            _placed = false;
            yield break;
        }

        BuildHierarchy(result.Anchor.transform);
        CurrentPhase = Phase.Placed;
        DebugReadout = "anchor resolved";
    }

    void BuildHierarchy(Transform anchor)
    {
        AnchorTransform = anchor;

        // Manual offsets must NOT go on the anchor — ARCore overwrites that transform on
        // every re-localization. NudgeRoot rides along instead.
        var nudgeRoot = new GameObject("NudgeRoot").transform;
        nudgeRoot.SetParent(anchor, false);
        _hierarchyRoot = nudgeRoot.gameObject;

        // Rotation 2: correcting the GLB's own local axes.
        var alignmentRoot = placement.CreateAlignmentRoot(nudgeRoot);

        // One site id for both modes: the adjustments you dial in during preview are the
        // same ones that must apply on site, which is the entire point of saving them.
        if (nudge != null) nudge.Bind(nudgeRoot, siteId);

        // Outdoors the streetscape meshes receive the model's shadow. In preview there is no
        // ground at all, so one gets made — otherwise the model appears to cast nothing.
        if (_previewRoot != null) CreateShadowGround(nudgeRoot);

        // Nothing to ghost in preview: there is no streetscape geometry indoors, and the
        // miniature's anchor point would land inside whatever mesh happened to exist.
        if (streetscapeShadows != null && _previewRoot == null) streetscapeShadows.SetTarget(anchor);

        buildingLoader.LoadInto(alignmentRoot);
    }

    /// <summary>
    /// An invisible quad at the model's feet that draws only the shadow falling on it.
    /// Sized generously, because a low sun throws a shadow far longer than the footprint.
    /// </summary>
    void CreateShadowGround(Transform parent)
    {
        // Resources first, because it is the only source that survives a player build with
        // no inspector wiring: Shader.Find returns null on device for a shader nothing in a
        // scene references, since the build strips it. Assets/Resources/ShadowCatcher.mat is
        // always included, and it drags its shader in with it.
        if (shadowCatcherMaterial == null)
            shadowCatcherMaterial = Resources.Load<Material>("ShadowCatcher");

        if (shadowCatcherMaterial == null)
        {
            var shader = Shader.Find("AR/ShadowCatcher");
            if (shader != null) shadowCatcherMaterial = new Material(shader);
        }

        if (shadowCatcherMaterial == null)
        {
            Debug.LogWarning("GeospatialController: no shadow catcher material — the preview " +
                             "model will cast onto nothing. Assign ShadowCatcher.mat.");
            return;
        }

        var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
        ground.name = "ShadowGround";

        // The collider would intercept the floor-placement raycast and let the model be
        // placed on its own shadow plane.
        Destroy(ground.GetComponent<Collider>());

        ground.transform.SetParent(parent, false);
        // MINUS 90, not plus: a Quad's normal is +Z, and rotating +90 about X sends it to
        // -Y — face down, back-face culled, invisible from above. -90 sends it to +Y.
        ground.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

        // In alignment-root space one unit is one metre, so this is metres of real building.
        float span = Mathf.Max(1f, shadowGroundScale * 30f);
        ground.transform.localScale = new Vector3(span, span, 1f);

        var renderer = ground.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = shadowCatcherMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;   // a receiver, never a caster
        renderer.receiveShadows = true;
    }

    /// <summary>
    /// Saves the adjustment, and on site also rewrites the site's coordinates to wherever
    /// the building has actually been dragged to.
    ///
    /// This only works with a live Earth fix. In preview there is no such thing as a
    /// coordinate — the model is sitting on your floor in the AR session's own frame, which
    /// has no relationship to anywhere on Earth — so preview saves the relative values only.
    /// </summary>
    public void SaveAdjustment()
    {
        if (nudge == null) return;

        // Always saves. With a fix it also records where the building is; without one it
        // stores scale, rotation and offsets, which are just as real without a coordinate.
        // The HUD says which happened — the failure to avoid is a silent one, not a partial.
        if (TryCaptureCoordinates(out double lat, out double lng))
        {
            // Logged BEFORE the bake, because BakeCoordinates zeroes east/north. If the
            // offsets below are non-zero but the baked coordinate comes back equal to the
            // anchor's, the offset is being dropped rather than converted — which is the
            // difference between "your correction was saved" and "your correction was
            // silently discarded", and the two are indistinguishable in the saved file.
            var nudgeT = _hierarchyRoot.transform;
            var anchorT = nudgeT.parent;
            Vector3 delta = anchorT != null ? nudgeT.position - anchorT.position : Vector3.zero;

            Debug.Log($"[Geospatial] baking '{siteId}': " +
                      $"offsets E {nudge.Current.eastMetres:F2} N {nudge.Current.northMetres:F2} " +
                      $"up {nudge.Current.heightMetres:F2} | " +
                      $"nudgeRoot-anchor delta ({delta.x:F2}, {delta.y:F2}, {delta.z:F2}) " +
                      $"= {delta.magnitude:F2} m | baked {lat:F7}, {lng:F7}");

            nudge.BakeCoordinates(lat, lng);
        }
        else
        {
            CanSaveCoordinates(out string reason);
            Debug.Log($"[Geospatial] saving without coordinates — {reason}");
        }

        nudge.Save();
    }

    /// <summary>
    /// Everything this component knows, for the capture button. Each class reports its own
    /// state so the dump cannot drift out of step with the code the way a central one would.
    /// </summary>
    public string StateReport
    {
        get
        {
            var report = $"site id            : {siteId}\n" +
                         $"phase              : {CurrentPhase}\n" +
                         $"readout            : {DebugReadout.Replace("\n", " | ")}\n" +
                         $"configured lat/lng : {latitude:F7}, {longitude:F7}\n" +
                         $"altitude above ter : {altitudeAboveTerrain}\n" +
                         $"location service   : {_locationStatus}\n";

            if (earthManager != null)
            {
                report += $"earth state        : {earthManager.EarthState}\n" +
                          $"earth tracking     : {earthManager.EarthTrackingState}\n";

                if (earthManager.EarthTrackingState == TrackingState.Tracking)
                {
                    var pose = earthManager.CameraGeospatialPose;
                    report += $"camera lat/lng     : {pose.Latitude:F7}, {pose.Longitude:F7}\n" +
                              $"camera altitude    : {pose.Altitude:F2} m\n" +
                              $"camera heading     : {pose.Heading:F1} deg\n" +
                              $"horiz accuracy     : {pose.HorizontalAccuracy:F2} m (gate {maxHorizontalAccuracy})\n" +
                              $"yaw accuracy       : {pose.OrientationYawAccuracy:F1} deg (gate {maxYawAccuracy})\n";
                }
            }

            CanSaveCoordinates(out string saveReason);
            report += $"can save coords    : {saveReason}\n";

            report += $"preview active     : {PreviewActive}\n";
            if (PreviewActive)
                report += $"preview            : {PreviewReadout.Replace("\n", " | ")}\n" +
                          $"last placement     : {PlacementSource}\n";

            if (AnchorTransform != null)
            {
                var camera = Camera.main;
                float distance = camera != null
                    ? Vector3.Distance(camera.transform.position, AnchorTransform.position)
                    : -1f;
                report += $"anchor world pos   : {AnchorTransform.position}\n" +
                          $"anchor distance    : {distance:F1} m\n";
            }

            return report;
        }
    }

    /// <summary>
    /// Whether a save can record real coordinates, and if not, what to do about it.
    ///
    /// The accuracy gate is the important part. ARCore will happily hand back a lat/lng
    /// while its own horizontal accuracy is tens of metres — your last outdoor reading was
    /// ±46 m — and baking that would overwrite a surveyed coordinate with a worse guess.
    /// </summary>
    public bool CanSaveCoordinates(out string reason)
    {
        // Preview is NOT excluded. It only skips *waiting* for localization; it does not
        // switch Earth off. Outdoors with preview on, ARCore often has a perfectly good fix,
        // and a model stood on the pavement then has a real coordinate worth recording.
        if (_hierarchyRoot == null)
        {
            reason = "nothing placed yet";
            return false;
        }

        if (earthManager == null)
        {
            reason = "no earth manager";
            return false;
        }

        var state = earthManager.EarthState;
        if (state != EarthState.Enabled)
        {
            reason = $"Earth: {state}";
            return false;
        }

        if (earthManager.EarthTrackingState != TrackingState.Tracking)
        {
            reason = "no VPS fix — point at facades and walk sideways";
            return false;
        }

        var pose = earthManager.CameraGeospatialPose;

        if (pose.HorizontalAccuracy > maxHorizontalAccuracy)
        {
            reason = $"GPS too rough: ±{pose.HorizontalAccuracy:F1} m " +
                     $"(need ≤{maxHorizontalAccuracy} m)";
            return false;
        }

        if (pose.OrientationYawAccuracy > maxYawAccuracy)
        {
            reason = $"heading too rough: ±{pose.OrientationYawAccuracy:F1}° " +
                     $"(need ≤{maxYawAccuracy}°)";
            return false;
        }

        reason = $"GPS ±{pose.HorizontalAccuracy:F1} m — ready";
        return true;
    }

    /// <summary>Where the model's own origin sits on Earth right now, if that is knowable.</summary>
    bool TryCaptureCoordinates(out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (!CanSaveCoordinates(out _)) return false;

        var root = _hierarchyRoot.transform;
        var pose = new Pose(root.position, root.rotation);
        var geospatial = earthManager.Convert(pose);

        latitude = geospatial.Latitude;
        longitude = geospatial.Longitude;
        return true;
    }

    // ------------------------------------------------------------------ preview

    /// <summary>Is the scale-model preview showing instead of the real placement?</summary>
    public bool PreviewActive => _previewRoot != null;

    /// <summary>
    /// How the last floor tap resolved: a real ARCore plane, the assumed-floor fallback, or
    /// why it refused. Shown in the HUD, because "a plane" and "a guess 1.6 m down" behave
    /// very differently and you cannot tell them apart from the result.
    /// </summary>
    public string PlacementSource { get; private set; } = "";

    /// <summary>
    /// The distance the preview is pretending you stand at. Driving this from a slider is
    /// the whole point: the model shrinks and grows as if you were walking away from it.
    /// </summary>
    public float PreviewViewDistance
    {
        get => previewViewDistance;
        set
        {
            previewViewDistance = Mathf.Max(1f, value);
            ApplyPreviewScale();
        }
    }

    public string PreviewReadout =>
        _previewRoot == null
            ? ""
            : $"PREVIEW: as seen from {previewViewDistance:F0} m\n" +
              $"  {(_previewOnFloor ? "on floor" : "eye level")} " +
              $"{_previewRefDistance:F1} m away " +
              $"at 1:{(previewViewDistance / Mathf.Max(0.01f, _previewRefDistance)):F0}";

    /// <summary>HUD toggle. Tears down whichever placement is up and builds the other.</summary>
    public void SetPreview(bool on)
    {
        if (on == PreviewActive) return;

        previewMode = on;   // keeps the inspector honest during play mode

        if (on) EnterPreview();
        else ExitPreview();
    }

    void EnterPreview()
    {
        TearDownHierarchy();

        _previewRoot = new GameObject("PreviewRoot").transform;
        _previewOnFloor = false;
        _previewRefDistance = previewStandoff;

        // Placement happens next frame, once the AR camera has a pose; do it now too so a
        // single-frame flash doesn't show it at full size on top of the viewer.
        ApplyPreviewScale();
        PositionPreview();
        _previewPlacePending = true;

        BuildHierarchy(_previewRoot);

        _placed = true;
        CurrentPhase = Phase.Placed;
        DebugReadout = "preview: placed by hand, not by VPS";
    }

    void ExitPreview()
    {
        TearDownHierarchy();

        if (debugPlaceWithoutLocalization)
        {
            PlaceWithoutLocalization();
            return;
        }

        // Re-arm the gate so the normal localize-and-resolve flow runs from the top.
        _placed = false;
        CurrentPhase = Phase.WaitingForEarth;
        DebugReadout = "preview off — waiting for Earth tracking";
    }

    /// <summary>
    /// Drops everything we built, leaving the ARCore anchor alone — it is owned by the
    /// session, not by us.
    /// </summary>
    void TearDownHierarchy()
    {
        if (_hierarchyRoot != null) Destroy(_hierarchyRoot);
        _hierarchyRoot = null;

        if (_previewRoot != null) Destroy(_previewRoot.gameObject);
        _previewRoot = null;

        if (_syntheticAnchor != null) Destroy(_syntheticAnchor);
        _syntheticAnchor = null;

        AnchorTransform = null;
        _previewPlacePending = false;
    }

    Camera PreviewCamera => previewCamera != null ? previewCamera : Camera.main;

    /// <summary>
    /// Puts the miniature in front of you, front facade toward you, and drops its base by a
    /// scaled eye height. At 120 m the base of a real building sits under a degree below
    /// your eyeline, so the miniature correctly floats near eye level rather than resting
    /// on the floor — that is what the far-away geometry actually looks like.
    /// </summary>
    public void PositionPreview()
    {
        if (_previewRoot == null) return;

        var cam = PreviewCamera;
        if (cam == null) return;

        // Recentring always returns to the eye-level standoff, undoing a floor placement.
        _previewOnFloor = false;
        _previewRefDistance = previewStandoff;

        // Same guarantee as a floor tap: whatever the slider says, the model ends up a size
        // you can actually see all of.
        FitPreviewToView();

        float scale = PreviewScale;

        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();

        _previewEyeY = cam.transform.position.y;

        Vector3 position = cam.transform.position + forward * previewStandoff;
        position.y = _previewEyeY - previewEyeHeight * scale;

        // Aim the model's front (+Z after Rotation 2) back at the viewer.
        var facing = Quaternion.LookRotation(-forward, Vector3.up) *
                     Quaternion.Euler(0f, -placement.ModelFrontOffsetDeg, 0f);

        _previewRoot.SetPositionAndRotation(position, facing);
    }

    /// <summary>
    /// Apparent size is the whole point: a model this many times smaller, at the distance
    /// it actually sits, subtends exactly the angle the real building would from
    /// previewViewDistance away.
    /// </summary>
    float PreviewScale =>
        Mathf.Max(0.01f, _previewRefDistance) / Mathf.Max(1f, previewViewDistance);

    void ApplyPreviewScale()
    {
        if (_previewRoot == null) return;

        _previewRoot.localScale = Vector3.one * PreviewScale;

        // The model must not jump sideways while you drag the distance slider. Standing on
        // the floor it doesn't move at all; floating at eye level only its height is
        // re-derived, because the base offset depends on scale.
        if (_previewOnFloor)
        {
            _previewRoot.position = _previewFloorPoint;
            return;
        }

        var p = _previewRoot.position;
        p.y = _previewEyeY - previewEyeHeight * PreviewScale;
        _previewRoot.position = p;
    }

    // -------------------------------------------------------- tap the floor to place

    /// <summary>
    /// Stands the model on the floor at the tapped point. The scale ratio is rebuilt from
    /// how far away that point actually is, so "as seen from N metres" still holds however
    /// near or far you put it.
    /// </summary>
    public bool TryPlacePreviewAt(Vector2 screenPosition)
    {
        if (_previewRoot == null) return false;

        var cam = PreviewCamera;
        if (cam == null) return false;

        var ray = cam.ScreenPointToRay(screenPosition);
        Vector3 point;

        if (TryRaycastPlanes(ray, out point))
        {
            PlacementSource = "plane";
        }
        else if (TryRaycastAssumedFloor(cam, ray, out point))
        {
            // A ray aimed just below the horizon travels a very long way before it meets a
            // plane 1.6 m down — straight through the wall in front of you, which is how the
            // model ended up embedded in one. Refuse rather than place somewhere absurd.
            float distance = Vector3.Distance(cam.transform.position, point);
            if (distance > maxPlacementDistance)
            {
                PlacementSource = $"missed — aim closer to your feet ({distance:F0} m)";
                return false;
            }

            PlacementSource = "assumed floor";
        }
        else
        {
            PlacementSource = "no floor — aim below the horizon";
            return false;
        }

        _previewOnFloor = true;
        _previewFloorPoint = point;
        _previewRefDistance = Vector3.Distance(cam.transform.position, point);

        // Adjustments are NOT cleared here any more: they are now deliberate, saved values
        // that must survive a re-placement. Use the reset button if one buries the model.
        FitPreviewToView();

        // Must be STRICTLY horizontal or the building tips over. Tapping near your own feet
        // collapses this to nothing, and the old fallback used the camera's forward — which,
        // while you are aiming down at the floor, points up and backwards, laying the model
        // on its side. Every fallback here is projected flat.
        Vector3 toCamera = Vector3.ProjectOnPlane(cam.transform.position - point, Vector3.up);

        if (toCamera.sqrMagnitude < 1e-3f)
            toCamera = Vector3.ProjectOnPlane(-cam.transform.forward, Vector3.up);

        if (toCamera.sqrMagnitude < 1e-3f)
            toCamera = Vector3.forward;

        _previewRoot.localScale = Vector3.one * PreviewScale;
        _previewRoot.SetPositionAndRotation(
            point,
            Quaternion.LookRotation(toCamera.normalized, Vector3.up) *
            Quaternion.Euler(0f, -placement.ModelFrontOffsetDeg, 0f));

        Debug.Log($"[Preview] placed on floor {_previewRefDistance:F2} m away, " +
                  $"scale 1:{(previewViewDistance / Mathf.Max(0.01f, _previewRefDistance)):F0}");
        return true;
    }

    /// <summary>
    /// Raises the pretend viewing distance until the model fits in view at the distance it
    /// is actually sitting. Without this, tapping a spot 1.5 m away with the slider near its
    /// 10 m minimum gives 1:7 — a 10 m building rendered 1.4 m tall in your face.
    /// Only ever increases it, so a deliberately distant setting is left alone.
    /// </summary>
    void FitPreviewToView()
    {
        if (previewFitFraction <= 0f) return;

        float height = buildingLoader != null ? buildingLoader.ModelHeightMetres : 0f;
        if (height <= 0.01f) return;   // model still loading — the slider stays in charge

        // scale = standoff / viewDistance, and we want height * scale <= standoff * fraction.
        // The standoff cancels: how big it looks depends only on height and view distance.
        float needed = height / previewFitFraction;

        if (previewViewDistance >= needed) return;

        Debug.Log($"[Preview] fitting {height:F1} m model to view: " +
                  $"distance {previewViewDistance:F0} -> {needed:F0} m");

        previewViewDistance = needed;
    }

    /// <summary>
    /// Hits ARCore's detected horizontal planes. Done by hand because the scene has an
    /// ARPlaneManager but no ARRaycastManager, and this needs no new scene wiring.
    /// </summary>
    bool TryRaycastPlanes(Ray ray, out Vector3 point)
    {
        point = default;

        if (_planeManager == null) _planeManager = FindAnyObjectByType<ARPlaneManager>();
        if (_planeManager == null) return false;

        float nearest = float.MaxValue;
        bool found = false;

        foreach (var plane in _planeManager.trackables)
        {
            if (plane == null) continue;
            if (plane.alignment != PlaneAlignment.HorizontalUp) continue;

            var mathPlane = new Plane(plane.transform.up, plane.transform.position);
            if (!mathPlane.Raycast(ray, out float distance) || distance >= nearest) continue;

            // The infinite plane is not the plane: a tap past the rug's edge should miss.
            Vector3 hit = ray.GetPoint(distance);
            Vector3 local = plane.transform.InverseTransformPoint(hit);
            if (!InsideBoundary(plane, new Vector2(local.x, local.z))) continue;

            nearest = distance;
            point = hit;
            found = true;
        }

        return found;
    }

    static bool InsideBoundary(ARPlane plane, Vector2 local)
    {
        var boundary = plane.boundary;
        if (boundary.Length < 3) return false;

        // Standard crossing count: walk the polygon edges and count how many the ray crosses.
        bool inside = false;
        for (int i = 0, j = boundary.Length - 1; i < boundary.Length; j = i++)
        {
            Vector2 a = boundary[i], b = boundary[j];
            if (a.y > local.y == b.y > local.y) continue;
            if (local.x < (b.x - a.x) * (local.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }

        return inside;
    }

    /// <summary>
    /// Fallback for when ARCore hasn't found a plane yet — assume the floor is one eye
    /// height below the camera. Wrong if you're sitting, but it always answers.
    /// </summary>
    bool TryRaycastAssumedFloor(Camera cam, Ray ray, out Vector3 point)
    {
        point = default;

        var floor = new Plane(Vector3.up,
                              new Vector3(0f, cam.transform.position.y - previewEyeHeight, 0f));

        if (!floor.Raycast(ray, out float distance) || distance <= 0f) return false;

        point = ray.GetPoint(distance);
        return true;
    }
}
