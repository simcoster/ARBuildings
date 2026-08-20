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
    static readonly int OccludersOffId = Shader.PropertyToID("_OccludersDisabled");

    [Header("Occluder cutout")]
    [Tooltip("Stop the real world occluding inside the volume the replacement building " +
             "occupies. Cuts by VOLUME rather than by whole mesh, so it works even where " +
             "ARCore serves no Building geometry at all and there is nothing to ghost.")]
    [SerializeField] bool cutoutEnabled = true;

    [Tooltip("Master switch. Off means NOTHING real can hide the model, which separates " +
             "'the model is in the wrong place' from 'the model is behind something'. " +
             "Shadows are unaffected.\n\n" +
             "DEFAULTS OFF while placement is being sorted out: ARCore has no mesh for the " +
             "target building itself, so occlusion here can only ever come from the terrain " +
             "and the neighbours, and both were eating chunks out of the model faster than " +
             "they were adding realism. Turn it back on from the HUD once the building lands " +
             "in the right place.")]
    [SerializeField] bool occludersEnabled = false;

    [Tooltip("Multiplier on the model's own box. A little over 1 covers the gap between a " +
             "coarse streetscape mesh and the model's true surface.")]
    [SerializeField] float cutoutMargin = 1.08f;

    [Tooltip("Extra metres added on every side, mostly to clear the terrain right at the base.")]
    [SerializeField] float cutoutPaddingMetres = 1.5f;

    /// <summary>
    /// Toggleable at runtime so the cutout can be A/B tested against the real world on site.
    /// Where the target building has no mesh of its own there is nothing to cut away, and an
    /// over-large box only switches off occlusion the NEIGHBOURS should still be providing.
    /// </summary>
    public bool CutoutEnabled
    {
        get => cutoutEnabled;
        set => cutoutEnabled = value;
    }

    /// <summary>Master occlusion switch, toggleable from the HUD.</summary>
    public bool OccludersEnabled
    {
        get => occludersEnabled;
        set => occludersEnabled = value;
    }

    /// <summary>Human-readable cutout state for the capture report.</summary>
    public string CutoutReadout { get; private set; } = "off";

    /// <summary>
    /// Rebuilt every frame rather than cached: the anchor moves whenever ARCore re-localizes,
    /// and a cutout left behind at the old pose would carve a hole in the wrong place.
    /// </summary>
    void LateUpdate()
    {
        Shader.SetGlobalFloat(OccludersOffId, occludersEnabled ? 0f : 1f);

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

    /// <summary>
    /// The decisive test for "does ARCore reconstruct THIS building", as opposed to merely
    /// serving Building meshes somewhere nearby.
    ///
    /// The count alone cannot answer it: a junction can serve dozens of Building meshes that
    /// are all the block across the street. Reading a non-zero count as coverage of the target
    /// is exactly how this site got documented as reconstructed and as not-reconstructed on
    /// separate occasions. This measures the only thing that settles it — how far the nearest
    /// Building mesh actually is from the model.
    ///
    /// MeshRenderer.bounds is a world-space AABB, so "contains" is conservative and an L-shaped
    /// block can report an overlap its actual surface does not have. A large `nearest` is
    /// therefore conclusive; a zero is strong but not proof.
    /// </summary>
    public string BuildingProximityReadout
    {
        get
        {
            if (streetscapeManager == null) return "no manager";

            var loader = BuildingLoader.Instance;
            if (loader == null || loader.State != BuildingLoader.LoadState.Loaded ||
                loader.LoadedParent == null)
                return "no model loaded";

            Vector3 centre = loader.LoadedParent.TransformPoint(loader.LocalBounds.center);

            int building = 0, containing = 0, within15 = 0;
            float nearest = float.MaxValue;

            foreach (var kv in _renderers)
            {
                var geometry = streetscapeManager.GetStreetscapeGeometry(kv.Key);
                if (geometry == null ||
                    geometry.streetscapeGeometryType != StreetscapeGeometryType.Building)
                    continue;

                building++;
                if (kv.Value == null) continue;

                float d = Mathf.Sqrt(kv.Value.bounds.SqrDistance(centre));
                if (d < nearest) nearest = d;
                if (d <= 0.01f) containing++;
                else if (d < 15f) within15++;
            }

            if (building == 0) return "no Building meshes served here";

            string verdict = containing > 0
                ? $"{containing} CONTAIN the model - this building IS reconstructed"
                : nearest < 15f
                    ? $"nearest {nearest:F1} m - close, none contain it"
                    : $"nearest {nearest:F1} m - all elsewhere, NOT reconstructed";

            return $"{verdict}\n" +
                   $"    {building} building, {containing} overlapping, {within15} within 15 m";
        }
    }

    /// <summary>Streetscape and occlusion state for the capture button.</summary>
    public string StateReport =>
        $"streetscape meshes : {MeshCount}\n" +
        $"by type            : {GeometryTypeBreakdown}\n" +
        $"target coverage    : {BuildingProximityReadout}\n" +
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
