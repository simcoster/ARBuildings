using Google.XR.ARCoreExtensions;
using UnityEngine;
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
    float _logTimer;
    string _locationStatus = "not started";

    // Everything we created under whatever we anchored to, so a mode switch can tear it
    // down again without touching the ARCore-owned anchor.
    GameObject _hierarchyRoot;
    GameObject _syntheticAnchor;   // a stand-in anchor we made ourselves, so ours to destroy
    Transform _previewRoot;
    bool _previewPlacePending;
    float _previewEyeY;   // camera height when the preview was placed, for the base offset

    /// <summary>
    /// Wire this to a debug Text element. Debugging geospatial without visible numbers
    /// is guesswork (Step 12).
    /// </summary>
    public string DebugReadout { get; private set; } = "waiting for Earth tracking";

    void Start()
    {
        if (startLocationService) StartCoroutine(RunLocationService());

        if (previewMode)
        {
            EnterPreview();
            return;
        }

        if (debugPlaceWithoutLocalization) PlaceWithoutLocalization();
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

        if (_placed) return;

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

        // Preview nudges are scratch work at 1/50 scale — keep them out of the real site's
        // saved offsets, which are surveyed values you bake into buildings.json.
        if (nudge != null) nudge.Bind(nudgeRoot, _previewRoot != null ? siteId + "-preview" : siteId);

        // Nothing to ghost in preview: there is no streetscape geometry indoors, and the
        // miniature's anchor point would land inside whatever mesh happened to exist.
        if (streetscapeShadows != null && _previewRoot == null) streetscapeShadows.SetTarget(anchor);

        buildingLoader.LoadInto(alignmentRoot);
    }

    // ------------------------------------------------------------------ preview

    /// <summary>Is the scale-model preview showing instead of the real placement?</summary>
    public bool PreviewActive => _previewRoot != null;

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
              $"  {previewStandoff:F1} m away at 1:{(previewViewDistance / Mathf.Max(0.01f, previewStandoff)):F0}";

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

        // Placement happens next frame, once the AR camera has a pose; do it now too so a
        // single-frame flash doesn't show it at full size on top of the viewer.
        ApplyPreviewScale();
        PositionPreview();
        _previewPlacePending = true;

        BuildHierarchy(_previewRoot);

        _placed = true;
        CurrentPhase = Phase.Placed;
        DebugReadout = "preview mode — localization skipped";
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

    float PreviewScale => previewStandoff / Mathf.Max(1f, previewViewDistance);

    void ApplyPreviewScale()
    {
        if (_previewRoot == null) return;

        _previewRoot.localScale = Vector3.one * PreviewScale;

        // Only the height is re-derived: the model must not jump sideways while you drag
        // the distance slider, and standoff is fixed, so a distance change is a size change.
        var p = _previewRoot.position;
        p.y = _previewEyeY - previewEyeHeight * PreviewScale;
        _previewRoot.position = p;
    }
}
