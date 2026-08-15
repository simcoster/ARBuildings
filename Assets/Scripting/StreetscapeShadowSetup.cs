using System.Collections.Generic;
using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Renders ARCore Streetscape Geometry so the real world can occlude, shadow, and be
/// shadowed by the placed model (tutorial Steps 10.5 and 11.4).
///
/// ARStreetscapeGeometry is NOT a Component in ARCore Extensions 1.54 — it is a plain
/// ITrackable exposing mesh/pose/type. So we build and own one renderer GameObject per
/// geometry rather than fetching a MeshRenderer off it.
/// </summary>
public class StreetscapeShadowSetup : MonoBehaviour
{
    [SerializeField] ARStreetscapeGeometryManager streetscapeManager;

    [Header("Materials")]
    [Tooltip("Applied to every mesh standing in for the real world. " +
             "This is the AR/StreetscapeOccluderShadow (or AR/ShadowCatcher) material — " +
             "the material, not the shader.")]
    [SerializeField] Material occluderMaterial;

    [Tooltip("Applied to the building being replaced. AR/GhostWireframe material.")]
    [SerializeField] Material ghostMaterial;

    [Header("Ghosting")]
    [Tooltip("Leave off for the empty-lot case — then everything is a plain occluder.")]
    [SerializeField] bool ghostTargetBuilding = true;

    [Tooltip("The resolved building anchor. Bound at runtime by GeospatialController; " +
             "the mesh whose bounds contain this point gets ghosted.")]
    [SerializeField] Transform targetAnchor;

    [Header("Debug visualisation")]
    [Tooltip("Paint every streetscape mesh translucent so you can see what ARCore serves. " +
             "The tutorial recommends this as the most reliable alignment reference — " +
             "Google's mesh of the real building beats eyeballing a facade edge.")]
    [SerializeField] bool visualiseMeshes = false;

    [Tooltip("AR/StreetscapeDebug material.")]
    [SerializeField] Material debugMaterial;

    readonly Dictionary<TrackableId, MeshRenderer> _renderers = new();
    Transform _container;

    /// <summary>Streetscape meshes currently streamed. 0 outdoors means none arrived.</summary>
    public int MeshCount => _renderers.Count;

    /// <summary>How many got the ghost material — &gt;1 means merged meshes over-selected.</summary>
    public int GhostedCount { get; private set; }

    // --------------------------------------------------------------- lifecycle

    void OnEnable()
    {
        if (streetscapeManager == null)
        {
            Debug.LogError("StreetscapeShadowSetup: streetscapeManager not assigned.");
            enabled = false;
            return;
        }

#if UNITY_EDITOR
        // ARStreetscapeGeometryManager.Update() dereferences ARCoreExtensions._instance
        // without a null check (Extensions 1.54, ARStreetscapeGeometryManager.cs:65). The
        // Editor has no ARCore session, so _instance is null and it throws every frame.
        // Streetscape Geometry is device-only regardless — turn the pair off here.
        streetscapeManager.enabled = false;
        enabled = false;
        Debug.Log("StreetscapeShadowSetup: disabled in the Editor. Streetscape Geometry " +
                  "requires a real ARCore session; this has no effect on device.");
        return;
#pragma warning disable CS0162 // unreachable in the Editor, reached in a player build
#endif

        if (occluderMaterial == null)
            Debug.LogWarning("StreetscapeShadowSetup: no occluder material assigned — " +
                             "streetscape meshes will render with no material.");

        streetscapeManager.StreetscapeGeometriesChanged += OnChanged;
#if UNITY_EDITOR
#pragma warning restore CS0162
#endif
    }

    void OnDisable()
    {
        if (streetscapeManager != null)
            streetscapeManager.StreetscapeGeometriesChanged -= OnChanged;

        foreach (var r in _renderers.Values)
            if (r != null) Destroy(r.gameObject);

        _renderers.Clear();

        if (_container != null) Destroy(_container.gameObject);
    }

    /// <summary>
    /// Call once the geospatial anchor resolves, so already-added meshes are re-classified.
    /// </summary>
    public void SetTarget(Transform anchor)
    {
        targetAnchor = anchor;

        foreach (var kv in _renderers)
        {
            var geometry = streetscapeManager.GetStreetscapeGeometry(kv.Key);
            if (geometry != null && kv.Value != null)
                ApplyMaterial(kv.Value, geometry);
        }
    }

    // ----------------------------------------------------------------- events

    void OnChanged(ARStreetscapeGeometriesChangedEventArgs args)
    {
        foreach (var geometry in args.Added)
            Add(geometry);

        foreach (var geometry in args.Updated)
            UpdateGeometry(geometry);

        foreach (var geometry in args.Removed)
            Remove(geometry);
    }

    void Add(ARStreetscapeGeometry geometry)
    {
        if (_renderers.ContainsKey(geometry.trackableId)) return;

        if (_container == null)
        {
            // Root-level and never moved: streetscape poses are already in world space,
            // so parenting under the XR Origin would apply its transform twice.
            _container = new GameObject("StreetscapeGeometry").transform;
            _container.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        var go = new GameObject($"Streetscape_{geometry.streetscapeGeometryType}");
        go.transform.SetParent(_container, false);

        var filter = go.AddComponent<MeshFilter>();
        var renderer = go.AddComponent<MeshRenderer>();

        _renderers[geometry.trackableId] = renderer;

        ApplyMesh(filter, geometry);
        ApplyPose(go.transform, geometry);
        ApplyMaterial(renderer, geometry);
    }

    void UpdateGeometry(ARStreetscapeGeometry geometry)
    {
        if (!_renderers.TryGetValue(geometry.trackableId, out var renderer) || renderer == null)
        {
            Add(geometry);
            return;
        }

        ApplyMesh(renderer.GetComponent<MeshFilter>(), geometry);
        ApplyPose(renderer.transform, geometry);

        // Bounds move with the geometry, so target selection can change.
        ApplyMaterial(renderer, geometry);
    }

    void Remove(ARStreetscapeGeometry geometry)
    {
        if (!_renderers.TryGetValue(geometry.trackableId, out var renderer)) return;

        if (renderer != null) Destroy(renderer.gameObject);
        _renderers.Remove(geometry.trackableId);
    }

    // ------------------------------------------------------------------ apply

    static void ApplyMesh(MeshFilter filter, ARStreetscapeGeometry geometry)
    {
        var mesh = geometry.mesh;

        // Streetscape meshes ship without normals; shadow casting and any lit shading
        // need them. Recalculating is only worth it once per mesh instance.
        if (filter.sharedMesh != mesh)
        {
            mesh.RecalculateNormals();
            filter.sharedMesh = mesh;
        }
    }

    static void ApplyPose(Transform t, ARStreetscapeGeometry geometry)
    {
        var pose = geometry.pose;
        t.SetPositionAndRotation(pose.position, pose.rotation);
    }

    /// <summary>Flip visualisation at runtime, from the HUD, without a rebuild.</summary>
    public bool VisualiseMeshes
    {
        get => visualiseMeshes;
        set
        {
            if (visualiseMeshes == value) return;
            visualiseMeshes = value;
            RefreshAllMaterials();
        }
    }

    void RefreshAllMaterials()
    {
        foreach (var kv in _renderers)
        {
            var geometry = streetscapeManager.GetStreetscapeGeometry(kv.Key);
            if (geometry != null && kv.Value != null) ApplyMaterial(kv.Value, geometry);
        }
    }

    void ApplyMaterial(MeshRenderer renderer, ARStreetscapeGeometry geometry)
    {
        bool wasTarget = renderer.sharedMaterial != null && renderer.sharedMaterial == ghostMaterial;
        bool isTarget = IsTarget(renderer, geometry);

        if (isTarget && !wasTarget) GhostedCount++;
        else if (!isTarget && wasTarget) GhostedCount--;

        // Visualisation overrides everything — the point is to see the raw geometry.
        if (visualiseMeshes && debugMaterial != null)
        {
            renderer.sharedMaterial = debugMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = false;
            return;
        }

        var material = isTarget ? ghostMaterial : occluderMaterial;
        if (material != null) renderer.sharedMaterial = material;

        // The building being replaced shouldn't cast a real shadow or occlude the new
        // model — the scene would contradict itself. Everything else casts and receives,
        // so neighbours shade the model and the model shades them (Step 11.4).
        renderer.shadowCastingMode = isTarget ? ShadowCastingMode.Off : ShadowCastingMode.On;
        renderer.receiveShadows = !isTarget;
    }

    bool IsTarget(MeshRenderer renderer, ARStreetscapeGeometry geometry)
    {
        if (!ghostTargetBuilding || targetAnchor == null) return false;
        if (geometry.streetscapeGeometryType != StreetscapeGeometryType.Building) return false;

        // Crude: streetscape meshes are often merged across several buildings, so this can
        // ghost more than intended. A manual tap-to-select is more reliable if that happens.
        return renderer.bounds.Contains(targetAnchor.position);
    }
}
