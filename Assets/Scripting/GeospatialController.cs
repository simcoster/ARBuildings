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

    [Tooltip("Echo the status to the log once a second. A poor man's on-screen readout — " +
             "watch it with: adb logcat -s Unity:V")]
    [SerializeField] bool logStatus = true;

    bool _placed;
    float _logTimer;

    /// <summary>
    /// Wire this to a debug Text element. Debugging geospatial without visible numbers
    /// is guesswork (Step 12).
    /// </summary>
    public string DebugReadout { get; private set; } = "waiting for Earth tracking";

    void Start()
    {
        if (!debugPlaceWithoutLocalization) return;

        // No ARCore, no VPS, no waiting — just build the hierarchy somewhere visible.
        var debugAnchor = new GameObject("DebugAnchor").transform;
        debugAnchor.SetPositionAndRotation(debugPlaceOffset, placement.AnchorRotation);

        BuildHierarchy(debugAnchor);
        _placed = true;
        DebugReadout = "DEBUG: placed without localization";
    }

    void Update()
    {
        if (_placed) return;

        if (earthManager.EarthTrackingState != TrackingState.Tracking)
        {
            DebugReadout = $"tracking: {earthManager.EarthTrackingState}\n" +
                           "Point your camera at buildings and move slowly";
            return;
        }

        var pose = earthManager.CameraGeospatialPose;

        DebugReadout =
            $"tracking: Tracking\n" +
            $"horizontal: {pose.HorizontalAccuracy:F2} m (need <= {maxHorizontalAccuracy})\n" +
            $"yaw: {pose.OrientationYawAccuracy:F1}° (need <= {maxYawAccuracy})";

        if (pose.HorizontalAccuracy > maxHorizontalAccuracy ||
            pose.OrientationYawAccuracy > maxYawAccuracy)
            return;

        _placed = true;
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

        var promise = anchorManager.ResolveAnchorOnTerrainAsync(
            latitude, longitude, altitudeAboveTerrain, rotation);

        StartCoroutine(WaitForAnchor(promise));
    }

    System.Collections.IEnumerator WaitForAnchor(ResolveAnchorOnTerrainPromise promise)
    {
        yield return promise;
        var result = promise.Result;

        if (result.TerrainAnchorState != TerrainAnchorState.Success || result.Anchor == null)
        {
            DebugReadout = $"terrain anchor FAILED: {result.TerrainAnchorState}";
            Debug.LogError($"Terrain anchor failed: {result.TerrainAnchorState}");

            // Let the gate re-arm so a later, better localization can try again.
            _placed = false;
            yield break;
        }

        BuildHierarchy(result.Anchor.transform);
        DebugReadout = "anchor resolved — loading model";
    }

    void BuildHierarchy(Transform anchor)
    {
        // Manual offsets must NOT go on the anchor — ARCore overwrites that transform on
        // every re-localization. NudgeRoot rides along instead.
        var nudgeRoot = new GameObject("NudgeRoot").transform;
        nudgeRoot.SetParent(anchor, false);

        // Rotation 2: correcting the GLB's own local axes.
        var alignmentRoot = placement.CreateAlignmentRoot(nudgeRoot);

        if (nudge != null) nudge.Bind(nudgeRoot, siteId);
        if (streetscapeShadows != null) streetscapeShadows.SetTarget(anchor);

        buildingLoader.LoadInto(alignmentRoot);
    }
}
