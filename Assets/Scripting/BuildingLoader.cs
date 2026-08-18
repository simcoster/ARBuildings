using GLTFast;
using UnityEngine;

/// <summary>
/// Fetches the GLB and instantiates it under the AlignmentRoot (Step 8).
/// </summary>
public class BuildingLoader : MonoBehaviour
{
    public enum ModelSource
    {
        StreamingAssets,
        RemoteUrl,
    }

    public enum LoadState { Idle, Loading, Loaded, Failed }

    /// <summary>Reported by the HUD — a failed load looks identical to a bad placement.</summary>
    public LoadState State { get; private set; } = LoadState.Idle;
    public string LastMessage { get; private set; } = "";
    public int RendererCount { get; private set; }
    public Vector3 BoundsSize { get; private set; }

    public static BuildingLoader Instance;

    [SerializeField] ModelSource source = ModelSource.StreamingAssets;

    [Tooltip("File name inside Assets/StreamingAssets, e.g. synagogue.glb")]
    [SerializeField] string streamingAssetsFile = "synagogue.glb";

    [Tooltip("Must return binary GLB, not an HTML page. If loading fails, curl -L the URL " +
             "and check the first bytes before blaming glTFast.")]
    [SerializeField] string modelUrl;

    public enum SizeMode
    {
        /// <summary>Use modelScale verbatim.</summary>
        FixedScale,

        /// <summary>Scale so the model stands targetHeightMetres tall.</summary>
        TargetHeight,

        /// <summary>
        /// Scale so the model's facade matches the measured distance between the two
        /// footprint corners. The most trustworthy option by far: it is derived from
        /// coordinates you surveyed rather than a number anyone typed in.
        /// </summary>
        FootprintWidth,
    }

    [Header("Sizing")]
    [SerializeField] SizeMode sizeMode = SizeMode.FootprintWidth;

    [Tooltip("Used by TargetHeight. The real building's height in metres.")]
    [SerializeField] float targetHeightMetres = 10f;

    [Tooltip("Used by FixedScale.")]
    [SerializeField] float modelScale = 1f;

    public enum Axis { X, Z }

    [Tooltip("Used by FootprintWidth: which of the model's own horizontal axes runs along " +
             "the facade. X matches the convention in BuildingPlacement (A->B runs along " +
             "the model's local +X). Flip to Z if the building comes out square-on.")]
    [SerializeField] Axis footprintAxis = Axis.Z;

    [Tooltip("Optional. Supplies the surveyed facade length for FootprintWidth. Found " +
             "automatically if left empty.")]
    [SerializeField] BuildingPlacement placement;

    [Tooltip("Move the model so it sits ON the anchor: horizontally centred, base at ground " +
             "level. CAD exports routinely put the origin hundreds of metres from the " +
             "geometry, and then the building is placed correctly but drawn off in a field " +
             "somewhere. Turn off only when the model's origin is already meaningful.")]
    [SerializeField] bool recenterOnAnchor = true;

    /// <summary>What the sizing step actually did — the HUD reports it.</summary>
    public float AppliedScale { get; private set; } = 1f;

    /// <summary>
    /// Height in ALIGNMENT-ROOT metres — real building metres, independent of any preview
    /// shrinking applied above it. Preview placement needs this to pick a size that fits.
    /// </summary>
    public float ModelHeightMetres { get; private set; }

    void Awake() => Instance = this;

    /// <summary>Lets buildings.json name the model, so switching site switches asset too.</summary>
    public void ApplySite(SiteCatalog.Site site)
    {
        if (site == null || string.IsNullOrEmpty(site.model)) return;

        streamingAssetsFile = site.model;
        source = ModelSource.StreamingAssets;
        Debug.Log($"[Sites] model file: {streamingAssetsFile}");
    }

    /// <summary>
    /// StreamingAssets is not a real directory on Android — it is served from inside the APK
    /// as "jar:file://…/base.apk!/assets/…". That has to go through UnityWebRequest (which
    /// GltfImport.Load does), NOT GltfImport.LoadFile, which opens a FileStream and fails.
    /// </summary>
    string ResolveUrl()
    {
        if (source == ModelSource.RemoteUrl) return modelUrl;

        var path = $"{Application.streamingAssetsPath}/{streamingAssetsFile}";

        // Android already carries the jar: scheme; desktop/editor paths need file://.
        return path.Contains("://") ? path : $"file://{path}";
    }

    public async void LoadInto(Transform parent)
    {
        var url = ResolveUrl();

        State = LoadState.Loading;
        LastMessage = url;

        var gltf = new GltfImport();
        bool success = await gltf.Load(url);

        if (!success)
        {
            State = LoadState.Failed;
            LastMessage = $"load failed: {url}";
            Debug.LogError($"GLB load failed — check the source returns binary, not HTML: {url}");
            return;
        }

        // Switching placement mode mid-download destroys the hierarchy we were loading into.
        // Instantiating under a destroyed transform throws, so drop this load on the floor.
        if (parent == null)
        {
            State = LoadState.Idle;
            LastMessage = "load abandoned — placement changed";
            return;
        }

        await gltf.InstantiateMainSceneAsync(parent);

        if (parent == null)
        {
            State = LoadState.Idle;
            LastMessage = "load abandoned — placement changed";
            return;
        }

        // Models often come in with wrong scale/origin. Normalise here so the alignment
        // hierarchy above is the only thing deciding where the building sits.
        Transform glbRoot = null;
        Vector3 importedScale = Vector3.one;

        if (parent.childCount > 0)
        {
            glbRoot = parent.GetChild(0);

            // Origin only. The ROTATION is deliberately left alone: exporters bake the
            // Z-up -> Y-up conversion into the root node — Blender writes a +90 degree X
            // rotation — and clearing it lays the building on its back. The old placeholder
            // GLB happened to have no rotation there, which is why resetting it looked safe.
            glbRoot.localPosition = Vector3.zero;
            importedScale = glbRoot.localScale;
        }

        // Zero renderers means the GLB parsed but produced nothing visible — a very
        // different problem from "it placed somewhere I can't see".
        var renderers = parent.GetComponentsInChildren<Renderer>();
        RendererCount = renderers.Length;

        // Measured before we touch the scale, so this multiplies whatever the asset came
        // with rather than throwing an authored scale away.
        AppliedScale = ResolveScale(parent, renderers);
        if (glbRoot != null) glbRoot.localScale = importedScale * AppliedScale;

        if (glbRoot != null && recenterOnAnchor) RecenterOnAnchor(parent, glbRoot, renderers);

        // Don't assume the importer got this right: a glTF material flagged transparent or
        // double-sided can come in with casting off, and then the building silently throws
        // no shadow at all. It is the one thing that sells the model as really being there.
        foreach (var r in renderers)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        if (renderers.Length > 0)
        {
            BoundsSize = MeasureWorldBounds(renderers).size;

            float parentScaleY = Mathf.Abs(parent.lossyScale.y);
            ModelHeightMetres = parentScaleY > 1e-6f ? BoundsSize.y / parentScaleY : BoundsSize.y;
        }

        State = LoadState.Loaded;
        LastMessage = $"{RendererCount} renderers";
        Debug.Log($"[Loader] loaded {RendererCount} renderers, scale x{AppliedScale:F3}, " +
                  $"world bounds {BoundsSize}");
    }

    static Bounds MeasureWorldBounds(Renderer[] renderers)
    {
        var b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    /// <summary>
    /// Slides the model so its footprint is centred on the anchor with its base on the
    /// ground. Measured after scaling, so the correction is in real metres.
    /// </summary>
    void RecenterOnAnchor(Transform parent, Transform glbRoot, Renderer[] renderers)
    {
        if (renderers.Length == 0) return;

        var local = MeasureLocalBounds(parent, renderers);

        // Horizontal: centre on the anchor. Vertical: put the base on it, not the middle —
        // a building sits on the ground rather than being impaled by it.
        var offset = new Vector3(-local.center.x, -local.min.y, -local.center.z);

        if (offset.magnitude > 0.01f)
            Debug.Log($"[Loader] recentring model by {offset.x:F2}, {offset.y:F2}, " +
                      $"{offset.z:F2} m — its origin was off in the distance");

        glbRoot.localPosition += offset;
    }

    /// <summary>
    /// The model's own bounding box, measured in the ALIGNMENT ROOT's space where one unit
    /// is one real metre.
    ///
    /// Not renderer.bounds: that is a world-space AABB, so once the parent chain carries a
    /// heading rotation the "width" of the box is a mix of the model's width and depth. For
    /// height that error cancels — a yaw never tilts anything — which is why the height fit
    /// worked, but it would quietly corrupt a facade measurement.
    /// </summary>
    static Bounds MeasureLocalBounds(Transform space, Renderer[] renderers)
    {
        var bounds = new Bounds();
        bool started = false;

        foreach (var renderer in renderers)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) continue;

            Bounds local = mesh.bounds;
            Matrix4x4 toSpace = space.worldToLocalMatrix * renderer.transform.localToWorldMatrix;

            for (int corner = 0; corner < 8; corner++)
            {
                var point = toSpace.MultiplyPoint3x4(new Vector3(
                    (corner & 1) == 0 ? local.min.x : local.max.x,
                    (corner & 2) == 0 ? local.min.y : local.max.y,
                    (corner & 4) == 0 ? local.min.z : local.max.z));

                if (!started) { bounds = new Bounds(point, Vector3.zero); started = true; }
                else bounds.Encapsulate(point);
            }
        }

        return bounds;
    }

    /// <summary>
    /// Works out the uniform scale to apply. Measured in the ALIGNMENT ROOT's space, where
    /// one unit is one real metre — so a target size stays a real-world size even when the
    /// whole hierarchy is shrunk, as preview mode shrinks it.
    /// </summary>
    float ResolveScale(Transform parent, Renderer[] renderers)
    {
        if (renderers.Length == 0) return modelScale;

        // ModelHeightMetres is deliberately not set here — the caller recomputes it after the
        // scale is applied, and it must be the finished height, not the authored one.
        var local = MeasureLocalBounds(parent, renderers);

        Debug.Log($"[Loader] authored size {local.size.x:F2} x {local.size.y:F2} x " +
                  $"{local.size.z:F2} (model units)");

        if (sizeMode == SizeMode.FootprintWidth)
        {
            if (placement == null) placement = FindAnyObjectByType<BuildingPlacement>();

            double facade = placement != null ? placement.FootprintLengthMetres : 0.0;
            float width = footprintAxis == Axis.X ? local.size.x : local.size.z;

            if (facade <= 0.01)
            {
                Debug.LogWarning("[Loader] FootprintWidth needs footprint mode enabled on " +
                                 "BuildingPlacement with both corners set — falling back.");
            }
            else if (width <= 1e-6f)
            {
                Debug.LogWarning($"[Loader] model has no extent along {footprintAxis}.");
            }
            else
            {
                float fitted = (float)facade / width;
                Debug.Log($"[Loader] facade is {facade:F2} m surveyed, model is {width:F2} " +
                          $"along {footprintAxis}; scaling x{fitted:F4}");
                return fitted;
            }
        }

        if (sizeMode == SizeMode.TargetHeight && targetHeightMetres > 0f)
        {
            if (local.size.y <= 1e-6f)
            {
                Debug.LogWarning("[Loader] model has zero height — cannot scale to target.");
                return modelScale;
            }

            float scale = targetHeightMetres / local.size.y;
            Debug.Log($"[Loader] model is {local.size.y:F2} tall as authored; " +
                      $"scaling x{scale:F4} to reach {targetHeightMetres} m.");
            return scale;
        }

        return modelScale;
    }
}
