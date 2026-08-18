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

    // ------------------------------------------------------------------ cutout

    static readonly int CutoutMatrixId = Shader.PropertyToID("_OccluderCutoutWorldToLocal");
    static readonly int CutoutOnId = Shader.PropertyToID("_OccluderCutoutOn");

    [Header("Occluder cutout")]
    [Tooltip("Stop the real world occluding inside the volume the replacement building " +
             "occupies. Cuts by VOLUME rather than by whole mesh, so it works even where " +
             "ARCore serves no Building geometry at all and there is nothing to ghost.")]
    [SerializeField] bool cutoutEnabled = true;

    [Tooltip("Multiplier on the model's own box. A little over 1 covers the gap between a " +
             "coarse streetscape mesh and the model's true surface.")]
    [SerializeField] float cutoutMargin = 1.08f;

    [Tooltip("Extra metres added on every side, mostly to clear the terrain right at the base.")]
    [SerializeField] float cutoutPaddingMetres = 1.5f;

    /// <summary>Human-readable cutout state for the capture report.</summary>
    public string CutoutReadout { get; private set; } = "off";

    /// <summary>
    /// Rebuilt every frame rather than cached: the anchor moves whenever ARCore re-localizes,
    /// and a cutout left behind at the old pose would carve a hole in the wrong place.
    /// </summary>
    void LateUpdate()
    {
        var loader = BuildingLoader.Instance;

        bool usable = cutoutEnabled &&
                      loader != null &&
                      loader.State == BuildingLoader.LoadState.Loaded &&
                      loader.LoadedParent != null &&
                      loader.LocalBounds.size.sqrMagnitude > 0.001f;

        if (!usable)
        {
            Shader.SetGlobalFloat(CutoutOnId, 0f);
            CutoutReadout = cutoutEnabled ? "waiting for model" : "disabled";
            return;
        }

        var bounds = loader.LocalBounds;

        // Padding goes sideways and up, never DOWN past the base. Extending it below ground
        // would stop the terrain receiving shadow in a skirt around the building, and the
        // model's own shadow would appear detached from it by exactly that margin.
        Vector3 size = bounds.size * cutoutMargin + Vector3.one * (cutoutPaddingMetres * 2f);
        size.y -= cutoutPaddingMetres;

        Vector3 centre = bounds.center;
        centre.y += cutoutPaddingMetres * 0.5f;

        // TRS maps the unit cube onto the box; the parent's matrix carries it into the world
        // complete with the building's heading, so the box is oriented, not axis-aligned.
        Matrix4x4 boxToWorld = loader.LoadedParent.localToWorldMatrix *
                               Matrix4x4.TRS(centre, Quaternion.identity, size);

        Shader.SetGlobalMatrix(CutoutMatrixId, boxToWorld.inverse);
        Shader.SetGlobalFloat(CutoutOnId, 1f);

        CutoutReadout = $"on, {size.x:F1} x {size.y:F1} x {size.z:F1} m";
    }

    /// <summary>
    /// Counts meshes by type. The decisive question when ghosting finds nothing is whether
    /// ARCore is serving any Building geometry here at all — coverage is not universal, and
    /// a site with terrain only can never have its building ghosted or picked.
    /// </summary>
    public string GeometryTypeBreakdown
    {
        get
        {
            if (streetscapeManager == null) return "no manager";

            int building = 0, terrain = 0, other = 0, missing = 0;

            foreach (var id in _renderers.Keys)
            {
                var geometry = streetscapeManager.GetStreetscapeGeometry(id);
                if (geometry == null) { missing++; continue; }

                switch (geometry.streetscapeGeometryType)
                {
                    case StreetscapeGeometryType.Building: building++; break;
                    case StreetscapeGeometryType.Terrain:  terrain++;  break;
                    default:                               other++;    break;
                }
            }

            return $"building={building} terrain={terrain} other={other} unresolved={missing}";
        }
    }

    /// <summary>Streetscape and occlusion state for the capture button.</summary>
    public string StateReport =>
        $"streetscape meshes : {MeshCount}\n" +
        $"by type            : {GeometryTypeBreakdown}\n" +
        $"ghosted meshes     : {GhostedCount}  (only possible where a Building mesh exists)\n" +
        $"occluder cutout    : {CutoutReadout}\n" +
        $"ghosting enabled   : {ghostTargetBuilding}\n" +
        $"target anchor set  : {targetAnchor != null}\n" +
        $"visualise meshes   : {visualiseMeshes}\n" +
        $"materials          : occluder={(occluderMaterial != null)} ghost={(ghostMaterial != null)} " +
        $"debug={(debugMaterial != null)}\n";

    // --------------------------------------------------------------- lifecycle    // --------------------------------------------------------------- lifecycle

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

        if (targetAnchor == null) return false;
        if (geometry.streetscapeGeometryType != StreetscapeGeometryType.Building) return false;

        // Crude: streetscape meshes are often merged across several buildings, and an
        // axis-aligned box around a diagonal footprint swallows the street next to it. The
        // anchor also sits at a facade CORNER at ground level — right on the boundary of the
        // box being tested — so this misses as easily as it over-selects. Hence tap-to-select.
        return renderer.bounds.Contains(targetAnchor.position);
    }

}
