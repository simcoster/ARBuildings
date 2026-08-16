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

    /// <summary>How the ghosted mesh is chosen.</summary>
    public enum GhostSelection
    {
        /// <summary>Whichever building mesh's bounds contain the anchor. Guesses.</summary>
        AutoFromAnchor,

        /// <summary>Whichever mesh you tapped. Doesn't guess.</summary>
        Manual,
    }

    public GhostSelection SelectionMode { get; private set; } = GhostSelection.AutoFromAnchor;
    TrackableId _selectedId;

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

    /// <summary>
    /// True when visualisation is asked for but can't happen. Without this the toggle is a
    /// button that silently does nothing, which reads as a broken feature rather than an
    /// unassigned inspector field.
    /// </summary>
    public bool DebugMaterialMissing => visualiseMeshes && debugMaterial == null;

    /// <summary>Flip visualisation at runtime, from the HUD, without a rebuild.</summary>
    public bool VisualiseMeshes
    {
        get => visualiseMeshes;
        set
        {
            if (visualiseMeshes == value) return;
            visualiseMeshes = value;

            if (visualiseMeshes && debugMaterial == null)
                Debug.LogWarning("StreetscapeShadowSetup: mesh visualisation is on but no " +
                                 "debug material is assigned — assign AR/StreetscapeDebug to " +
                                 "XR Origin > Streetscape Shadow Setup > Debug Material.");

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
        if (!ghostTargetBuilding) return false;

        // An explicit tap beats any heuristic — and it is not restricted to Building meshes,
        // because if you deliberately tapped a terrain mesh you probably meant to.
        if (SelectionMode == GhostSelection.Manual)
            return geometry.trackableId == _selectedId;

        if (targetAnchor == null) return false;
        if (geometry.streetscapeGeometryType != StreetscapeGeometryType.Building) return false;

        // Crude: streetscape meshes are often merged across several buildings, and an
        // axis-aligned box around a diagonal footprint swallows the street next to it. The
        // anchor also sits at a facade CORNER at ground level — right on the boundary of the
        // box being tested — so this misses as easily as it over-selects. Hence tap-to-select.
        return renderer.bounds.Contains(targetAnchor.position);
    }

    // ------------------------------------------------------------- tap to select

    public string SelectionReadout =>
        SelectionMode == GhostSelection.Manual
            ? $"ghost: tapped ({GhostedCount})"
            : $"ghost: auto ({GhostedCount})";

    /// <summary>Back to picking by anchor containment.</summary>
    public void ClearSelection()
    {
        if (SelectionMode == GhostSelection.AutoFromAnchor) return;

        SelectionMode = GhostSelection.AutoFromAnchor;
        _selectedId = default;
        RefreshAllMaterials();
    }

    /// <summary>
    /// Ghosts the nearest streetscape mesh under the ray. Intersection is done by hand
    /// rather than with Physics.Raycast: that would need a MeshCollider on every geometry,
    /// re-baked every time ARCore updates it, which is a per-frame cost for something that
    /// only has to work at the moment of a tap.
    /// </summary>
    public bool TrySelectAt(Ray ray)
    {
        float best = float.MaxValue;
        TrackableId bestId = default;
        bool found = false;

        foreach (var kv in _renderers)
        {
            var renderer = kv.Value;
            if (renderer == null) continue;

            // Cheap reject first — the bounds test is the AABB, which is wrong for picking
            // but perfectly good for "this mesh is nowhere near the ray, skip it".
            if (!renderer.bounds.IntersectRay(ray)) continue;

            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) continue;

            if (RaycastMesh(ray, mesh, renderer.transform, out float distance) && distance < best)
            {
                best = distance;
                bestId = kv.Key;
                found = true;
            }
        }

        if (!found) return false;

        _selectedId = bestId;
        SelectionMode = GhostSelection.Manual;
        RefreshAllMaterials();

        Debug.Log($"[Streetscape] ghosting tapped mesh {bestId} at {best:F1} m");
        return true;
    }

    static bool RaycastMesh(Ray worldRay, Mesh mesh, Transform t, out float distance)
    {
        distance = float.MaxValue;

        // Streetscape objects are placed at world poses with no scaling, so a distance
        // measured in local space is already metres.
        Vector3 origin = t.InverseTransformPoint(worldRay.origin);
        Vector3 direction = t.InverseTransformDirection(worldRay.direction);

        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        bool hit = false;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            if (RayTriangle(origin, direction,
                            vertices[triangles[i]],
                            vertices[triangles[i + 1]],
                            vertices[triangles[i + 2]],
                            out float d) && d < distance)
            {
                distance = d;
                hit = true;
            }
        }

        return hit;
    }

    /// <summary>
    /// Möller–Trumbore, deliberately double-sided: streetscape meshes are not reliably
    /// wound outward, which is also why the debug shader draws with Cull Off.
    /// </summary>
    static bool RayTriangle(Vector3 origin, Vector3 direction,
                            Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        const float epsilon = 1e-7f;
        distance = 0f;

        Vector3 ab = b - a, ac = c - a;
        Vector3 p = Vector3.Cross(direction, ac);
        float det = Vector3.Dot(ab, p);

        if (det > -epsilon && det < epsilon) return false;   // ray parallel to the triangle

        float inv = 1f / det;
        Vector3 s = origin - a;

        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;

        Vector3 q = Vector3.Cross(s, ab);
        float v = Vector3.Dot(direction, q) * inv;
        if (v < 0f || u + v > 1f) return false;

        distance = Vector3.Dot(ac, q) * inv;
        return distance > epsilon;
    }
}
