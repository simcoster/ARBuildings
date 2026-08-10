using UnityEngine;

/// <summary>
/// The two rotations that decide where the building points (Step 7.5). Keeping them as
/// separate fields is the whole point: when it's wrong on site you know which one to touch.
/// </summary>
public class BuildingPlacement : MonoBehaviour
{
    [Header("Rotation 1 — where the building faces in the world")]
    [Tooltip("True-north azimuth, degrees clockwise. N32°W = 328.")]
    [SerializeField] float buildingHeadingDeg = 339.46f;

    [Header("Rotation 2 — correcting the model's own axes")]
    [Tooltip("Degrees to spin the GLB so its intended front faces local +Z. 0 if exported to spec.")]
    [SerializeField] float modelFrontOffsetDeg = 0f;

    [Header("Manual on-site nudge (Step 9)")]
    [Tooltip("Bake the value read off the nudge UI back into buildingHeadingDeg, don't leave it here.")]
    [SerializeField] float headingNudgeDeg = 0f;

    /// <summary>
    /// Heading -> ARCore EUS (East-Up-South) quaternion. This is the documented conversion.
    /// </summary>
    public Quaternion AnchorRotation =>
        Quaternion.AngleAxis(180f - (buildingHeadingDeg + headingNudgeDeg), Vector3.up);

    /// <summary>
    /// Creates the AlignmentRoot the GLB gets instantiated under. Called before the model
    /// finishes downloading, so it can't take the GLB transform as an argument.
    /// </summary>
    public Transform CreateAlignmentRoot(Transform parent)
    {
        var alignmentRoot = new GameObject("AlignmentRoot").transform;
        alignmentRoot.SetParent(parent, false);
        alignmentRoot.localPosition = Vector3.zero;
        alignmentRoot.localRotation = Quaternion.Euler(0f, modelFrontOffsetDeg, 0f);
        return alignmentRoot;
    }
}
