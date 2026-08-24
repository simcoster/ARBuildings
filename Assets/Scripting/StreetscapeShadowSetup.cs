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

    /// <summary>
    /// Streetscape meshes actually being drawn. The number that matters when the model
    /// disappears: 0 means nothing streetscape can be hiding it, whatever the HUD claims.
    /// </summary>
    public int DrawingCount
    {
        get
        {
            int n = 0;
            foreach (var r in _renderers.Values)
                if (r != null && r.enabled) n++;

            return n;
        }
    }

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

    /// <summary>
    /// Master occlusion switch, toggleable from the HUD. OFF means NOTHING streetscape can
    /// hide the model — no depth, no paint, no shadow.
    ///
    /// This disables the RENDERERS rather than setting a shader global, because the global
    /// never worked. `AR/StreetscapeOccluderShadow` has three passes and only "Occluder"
    /// tests `_OccludersDisabled`; "ShadowReceive" still paints the mesh over whatever is
    /// behind it and "ShadowCaster" still writes the shadow map. On site 2026-08-23 that made
    /// the toggle a no-op — ARCore's terrain slab sat between the camera and the model and
    /// covered it identically in both states, which cost most of a site visit to pin down.
    /// Turning the renderer off cannot be defeated by a pass nobody remembered to guard.
    ///
    /// The trade is real and deliberate: with occluders off the terrain no longer catches the
    /// model's shadow either. Correctness of "can I see the model at all" outranks it.
    /// </summary>
    public bool OccludersEnabled
    {
        get => occludersEnabled;
        set
        {
            occludersEnabled = value;
            ApplyOccluderVisibility();
        }
    }

    /// <summary>
    /// Applies the master switch to every streamed mesh. Called on toggle and on every Add,
    /// because streetscape meshes keep arriving and a new one must not sneak in enabled.
    /// </summary>
    void ApplyOccluderVisibility()
    {
        // Visualisation wins: `mesh on` exists to show where ARCore's geometry actually is,
        // and it has to keep working while occluders are off — that combination is what
        // proved the terrain slab was the thing swallowing the model.
        bool visible = occludersEnabled || visualiseMeshes;

        foreach (var r in _renderers.Values)
            if (r != null) r.enabled = visible;
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
        $"occluders enabled  : {occludersEnabled}\n" +
        $"renderers drawing  : {DrawingCount} of {MeshCount}  (0 = nothing can hide the model)\n" +
        $"occluder cutout    : {CutoutReadout}\n" +
        $"ghosting enabled   : {ghostTargetBuilding}\n" +
        $"target anchor set  : {targetAnchor != null}\n" +
        $"visualise meshes   : {visualiseMeshes}\n" +
        $"materials          : occluder={(occluderMaterial != null)} ghost={(ghostMaterial != null)} " +
        $"debug={(debugMaterial != null)}\n";

    // ------------------------------------------------------------------- probe

    /// <summary>One streetscape mesh, described without handing out its renderer.</summary>
    public struct GeometryInfo
    {
        public TrackableId id;
        public StreetscapeGeometryType type;
        public StreetscapeGeometryQuality quality;

        /// <summary>World-space AABB, so once rotated it is a superset of the real mesh.</summary>
        public Bounds bounds;

        public int triangleCount;
    }

    /// <summary>Where a ray met a streetscape mesh, and what it met.</summary>
    public struct ProbeHit
    {
        public TrackableId id;
        public StreetscapeGeometryType type;
        public StreetscapeGeometryQuality quality;
        public Vector3 point;

        /// <summary>World normal of the struck triangle.</summary>
        public Vector3 normal;

        public float distance;

        /// <summary>Triangles in the whole mesh — a footprint extrusion has very few.</summary>
        public int triangleCount;

        /// <summary>
        /// What was struck, from the triangle normal. A facade ray that comes back "roof" has
        /// hit the flat lid of an extruded footprint from above, not a wall.
        /// </summary>
        public string Surface =>
            normal.y > 0.7f ? "roof" :
            normal.y < -0.7f ? "underside" :
            Mathf.Abs(normal.y) < 0.35f ? "wall" : "sloped";
    }

    /// <summary>
    /// TrackableIds print as two 16-digit hex words, which is unreadable on a phone screen.
    /// The low digits are the part that differs between trackables in one session.
    /// </summary>
    public static string ShortId(TrackableId id)
    {
        string text = id.ToString();
        return text.Length <= 6 ? text : text.Substring(text.Length - 6);
    }

    /// <summary>Every streamed mesh, refilled into the caller's list so nothing allocates.</summary>
    public void CollectGeometries(List<GeometryInfo> results)
    {
        results.Clear();
        if (streetscapeManager == null) return;

        foreach (var kv in _renderers)
        {
            if (kv.Value == null) continue;

            var geometry = streetscapeManager.GetStreetscapeGeometry(kv.Key);
            if (geometry == null) continue;

            results.Add(new GeometryInfo
            {
                id = kv.Key,
                type = geometry.streetscapeGeometryType,
                quality = geometry.quality,
                bounds = kv.Value.bounds,
                triangleCount = TriangleCount(kv.Key, kv.Value),
            });
        }
    }

    /// <summary>
    /// Ray-cast the streetscape on the CPU, nearest first.
    ///
    /// No colliders: the meshes are rebuilt as ARCore refines them, and cooking a MeshCollider
    /// per update is far more expensive than a handful of triangle tests a few times a second.
    /// The world AABB rejects most meshes before any triangle is touched.
    /// </summary>
    public void Probe(Ray ray, float maxDistance, List<ProbeHit> results)
    {
        results.Clear();
        if (streetscapeManager == null) return;

        foreach (var kv in _renderers)
        {
            var renderer = kv.Value;
            if (renderer == null) continue;

            if (!renderer.bounds.IntersectRay(ray, out float boundsDistance) ||
                boundsDistance > maxDistance)
                continue;

            var data = MeshFor(kv.Key, renderer);
            if (data == null) continue;

            var geometry = streetscapeManager.GetStreetscapeGeometry(kv.Key);
            if (geometry == null) continue;

            // Streetscape poses carry no scale, so a local-space distance IS a world-space
            // distance and needs no conversion back.
            var t = renderer.transform;
            var local = new Ray(t.InverseTransformPoint(ray.origin),
                                t.InverseTransformDirection(ray.direction));

            if (!RaycastMesh(data, local, maxDistance, out float distance, out Vector3 localNormal))
                continue;

            results.Add(new ProbeHit
            {
                id = kv.Key,
                type = geometry.streetscapeGeometryType,
                quality = geometry.quality,
                point = ray.origin + ray.direction * distance,
                normal = t.TransformDirection(localNormal).normalized,
                distance = distance,
                triangleCount = data.triangles.Length / 3,
            });
        }

        results.Sort((a, b) => a.distance.CompareTo(b.distance));
    }

    /// <summary>Mesh geometry read back once per mesh instance rather than per probe.</summary>
    sealed class MeshData
    {
        public Mesh mesh;
        public Vector3[] vertices;
        public int[] triangles;
    }

    readonly Dictionary<TrackableId, MeshData> _meshCache = new();

    MeshData MeshFor(TrackableId id, MeshRenderer renderer)
    {
        var filter = renderer.GetComponent<MeshFilter>();
        var mesh = filter != null ? filter.sharedMesh : null;
        if (mesh == null) return null;

        if (_meshCache.TryGetValue(id, out var data) && data.mesh == mesh)
            return data;

        // mesh.vertices and mesh.triangles both allocate a fresh array, which is exactly why
        // this is cached and invalidated on update rather than read per ray.
        data = new MeshData
        {
            mesh = mesh,
            vertices = mesh.vertices,
            triangles = mesh.triangles,
        };

        _meshCache[id] = data;
        return data;
    }

    int TriangleCount(TrackableId id, MeshRenderer renderer)
    {
        var data = MeshFor(id, renderer);
        return data == null ? 0 : data.triangles.Length / 3;
    }

    /// <summary>Möller–Trumbore against every triangle, keeping the nearest.</summary>
    static bool RaycastMesh(MeshData data, Ray ray, float maxDistance,
                            out float distance, out Vector3 normal)
    {
        distance = maxDistance;
        normal = Vector3.up;

        var vertices = data.vertices;
        var triangles = data.triangles;
        bool hit = false;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]];
            Vector3 b = vertices[triangles[i + 1]];
            Vector3 c = vertices[triangles[i + 2]];

            Vector3 e1 = b - a;
            Vector3 e2 = c - a;
            Vector3 p = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, p);

            // Two-sided: streetscape walls are single-sided and can be met from either face.
            if (Mathf.Abs(det) < 1e-8f) continue;

            float invDet = 1f / det;
            Vector3 tv = ray.origin - a;

            float u = Vector3.Dot(tv, p) * invDet;
            if (u < 0f || u > 1f) continue;

            Vector3 q = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(ray.direction, q) * invDet;
            if (v < 0f || u + v > 1f) continue;

            float t = Vector3.Dot(e2, q) * invDet;
            if (t < 0.01f || t >= distance) continue;

            distance = t;
            normal = Vector3.Cross(e1, e2);
            hit = true;
        }

        return hit;
    }

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
        _meshCache.Clear();

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

        // Born respecting the master switch: meshes stream in continuously, and one arriving
        // enabled while occluders are off would silently start hiding the model again.
        renderer.enabled = occludersEnabled;

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

        // ARCore may refine a mesh in place, keeping the same Mesh instance, so the probe
        // cache cannot detect the change on its own.
        _meshCache.Remove(geometry.trackableId);

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
        _meshCache.Remove(geometry.trackableId);
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

            // Visualisation forces the renderers back on even with occluders off, and turning
            // it off must hand them back to the master switch rather than leave them drawing.
            ApplyOccluderVisibility();
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
