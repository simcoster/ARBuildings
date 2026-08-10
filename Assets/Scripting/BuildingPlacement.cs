using UnityEngine;

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