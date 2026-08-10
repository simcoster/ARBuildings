using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

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
    const double MaxYawAccuracy = 10.0;  // degrees

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