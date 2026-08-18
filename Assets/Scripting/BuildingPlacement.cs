using UnityEngine;

/// <summary>
/// Decides where the building points and sits (Step 7.5).
///
/// Two ways to specify it:
///  - a single heading you measured yourself, or
///  - FOOTPRINT MODE: two corner coordinates along one facade, from which the heading is
///    derived. That removes the compass, the magnetic-vs-true correction, and the
///    "N32°W or 347°?" ambiguity, and gives a free scale check.
/// </summary>
public class BuildingPlacement : MonoBehaviour
{
    [Header("Footprint mode — two corners along ONE facade")]
    [Tooltip("Derive the heading from two coordinates instead of measuring it separately.")]
    [SerializeField] bool useFootprint = false;

    [Tooltip("Corner A. The model is anchored here. 6+ decimal places.")]
    [SerializeField] double cornerALatitude;
    [SerializeField] double cornerALongitude;

    [Tooltip("The second measured point. Two ways to pick it, and the offset below is what " +
             "tells them apart:\n" +
             "  ALONG THE FACADE — A and B are two corners of the front wall, and the " +
             "distance is the building's WIDTH.\n" +
             "  FRONT TO BACK — A is on the facade and B is the back corner, and the " +
             "distance is the building's DEPTH.")]
    [SerializeField] double cornerBLatitude;
    [SerializeField] double cornerBLongitude;

    [Tooltip("Where the model's front (+Z) points, relative to the A->B line.\n" +
             "  -90 / +90 — A->B runs ALONG the facade; the front is to the right / left " +
             "as you walk A->B.\n" +
             "  180 — A->B runs FRONT TO BACK; the front faces back down the line, from B " +
             "towards A.\n" +
             "Whichever you use, BuildingLoader's footprint axis must match: X for a width " +
             "measurement, Z for a depth one.")]
    [SerializeField] float headingOffsetFromABDeg = -90f;

    [Header("Rotation 1 — used only when footprint mode is OFF")]
    [Tooltip("True-north azimuth, degrees clockwise. N32°W = 328.")]
    [SerializeField] float buildingHeadingDeg = 328f;

    [Header("Rotation 2 — correcting the model's own axes")]
    [Tooltip("Degrees to spin the GLB so its intended front faces local +Z. 0 if exported to spec.")]
    [SerializeField] float modelFrontOffsetDeg = 0f;

    [Header("Origin correction")]
    [Tooltip("Local offset from the anchor to the model origin, in metres. The placeholder's " +
             "origin is its REAR corner, so if your corners are on the FRONT facade set " +
             "Z to minus the building depth (-18.6).")]
    [SerializeField] Vector3 originOffsetLocal = Vector3.zero;

    [Header("Manual on-site nudge (Step 9)")]
    [Tooltip("Bake the value read off the nudge UI back into the heading, don't leave it here.")]
    [SerializeField] float headingNudgeDeg = 0f;

    /// <summary>Heading actually used: derived from the footprint, or the manual value.</summary>
    public float EffectiveHeadingDeg =>
        useFootprint
            ? Mod360((float)BearingDegrees(cornerALatitude, cornerALongitude,
                                           cornerBLatitude, cornerBLongitude)
                     + headingOffsetFromABDeg)
            : buildingHeadingDeg;

    /// <summary>A->B ground distance. Compare against the model's width as a sanity check.</summary>
    public double FootprintLengthMetres =>
        useFootprint
            ? DistanceMetres(cornerALatitude, cornerALongitude, cornerBLatitude, cornerBLongitude)
            : 0.0;

    public bool UseFootprint => useFootprint;

    /// <summary>Rotation 2, exposed so preview mode can aim the model's front at the viewer.</summary>
    public float ModelFrontOffsetDeg => modelFrontOffsetDeg;

    /// <summary>
    /// Applies site data read from buildings.json, overriding whatever the inspector holds.
    /// The file wins on purpose: it is the copy that can be reviewed and version-controlled.
    /// </summary>
    public void ApplySite(SiteCatalog.Site site)
    {
        if (site == null) return;

        modelFrontOffsetDeg = site.modelFrontOffsetDeg;

        if (site.HasFootprint)
        {
            useFootprint = true;
            cornerALatitude = site.footprint.cornerA.latitude;
            cornerALongitude = site.footprint.cornerA.longitude;
            cornerBLatitude = site.footprint.cornerB.latitude;
            cornerBLongitude = site.footprint.cornerB.longitude;
            headingOffsetFromABDeg = site.headingOffsetFromABDeg;

            Debug.Log($"[Sites] footprint mode: {FootprintLengthMetres:F2} m between corners, " +
                      $"heading {EffectiveHeadingDeg:F2}°");
        }
        else
        {
            useFootprint = false;
            buildingHeadingDeg = site.headingDeg;
            Debug.Log($"[Sites] manual heading {buildingHeadingDeg:F2}°");
        }
    }

    /// <summary>Where the anchor goes when footprint mode is on.</summary>
    public bool TryGetAnchorLatLng(out double latitude, out double longitude)
    {
        latitude = cornerALatitude;
        longitude = cornerALongitude;
        return useFootprint;
    }

    public string PlacementReadout =>
        useFootprint
            ? $"heading {EffectiveHeadingDeg:F1}° (from A→B)\nfacade {FootprintLengthMetres:F1} m"
            : $"heading {EffectiveHeadingDeg:F1}° (manual)";

    /// <summary>
    /// Heading -> ARCore EUS (East-Up-South) quaternion. This is the documented conversion.
    /// </summary>
    public Quaternion AnchorRotation =>
        Quaternion.AngleAxis(180f - (EffectiveHeadingDeg + headingNudgeDeg), Vector3.up);

    /// <summary>
    /// Creates the AlignmentRoot the GLB gets instantiated under. Called before the model
    /// finishes downloading, so it can't take the GLB transform as an argument.
    /// </summary>
    public Transform CreateAlignmentRoot(Transform parent)
    {
        var alignmentRoot = new GameObject("AlignmentRoot").transform;
        alignmentRoot.SetParent(parent, false);
        alignmentRoot.localPosition = originOffsetLocal;
        alignmentRoot.localRotation = Quaternion.Euler(0f, modelFrontOffsetDeg, 0f);
        return alignmentRoot;
    }

    // ------------------------------------------------------------------ geodesy

    static float Mod360(float d) => (d % 360f + 360f) % 360f;

    /// <summary>Initial great-circle bearing A->B, degrees clockwise from true north.</summary>
    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * Mathf.Deg2Rad;
        double p2 = lat2 * Mathf.Deg2Rad;
        double dl = (lon2 - lon1) * Mathf.Deg2Rad;

        double y = System.Math.Sin(dl) * System.Math.Cos(p2);
        double x = System.Math.Cos(p1) * System.Math.Sin(p2) -
                   System.Math.Sin(p1) * System.Math.Cos(p2) * System.Math.Cos(dl);

        return (System.Math.Atan2(y, x) * Mathf.Rad2Deg + 360.0) % 360.0;
    }

    /// <summary>Haversine distance in metres. Plenty accurate at building scale.</summary>
    public static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000.0;
        double p1 = lat1 * Mathf.Deg2Rad;
        double p2 = lat2 * Mathf.Deg2Rad;
        double dp = (lat2 - lat1) * Mathf.Deg2Rad;
        double dl = (lon2 - lon1) * Mathf.Deg2Rad;

        double a = System.Math.Sin(dp / 2) * System.Math.Sin(dp / 2) +
                   System.Math.Cos(p1) * System.Math.Cos(p2) *
                   System.Math.Sin(dl / 2) * System.Math.Sin(dl / 2);

        return 2 * R * System.Math.Asin(System.Math.Min(1.0, System.Math.Sqrt(a)));
    }
}
