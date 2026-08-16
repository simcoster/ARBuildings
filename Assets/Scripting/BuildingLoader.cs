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

    [Tooltip("File name inside Assets/StreamingAssets, e.g. abandoned-house.glb")]
    [SerializeField] string streamingAssetsFile = "abandoned-house.glb";

    [Tooltip("Must return binary GLB, not an HTML page. If loading fails, curl -L the URL " +
             "and check the first bytes before blaming glTFast.")]
    [SerializeField] string modelUrl;

    [Header("Sizing")]
    [Tooltip("Scale the model so its bounding box is this many metres tall, ignoring " +
             "modelScale. Set this when you don't know the asset's units — AI-generated " +
             "and asset-store models are usually authored at unit scale, not metres. " +
             "0 = off.")]
    [SerializeField] float targetHeightMetres = 10f;

    [Tooltip("Uniform scale, used only when targetHeightMetres is 0.")]
    [SerializeField] float modelScale = 1f;

    /// <summary>What the sizing step actually did — the HUD reports it.</summary>
    public float AppliedScale { get; private set; } = 1f;

    /// <summary>
    /// Height in ALIGNMENT-ROOT metres — real building metres, independent of any preview
    /// shrinking applied above it. Preview placement needs this to pick a size that fits.
    /// </summary>
    public float ModelHeightMetres { get; private set; }

    void Awake() => Instance = this;

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
        if (parent.childCount > 0)
        {
            glbRoot = parent.GetChild(0);
            glbRoot.localPosition = Vector3.zero;
            glbRoot.localRotation = Quaternion.identity;
            glbRoot.localScale = Vector3.one;
        }

        // Zero renderers means the GLB parsed but produced nothing visible — a very
        // different problem from "it placed somewhere I can't see".
        var renderers = parent.GetComponentsInChildren<Renderer>();
        RendererCount = renderers.Length;

        AppliedScale = ResolveScale(parent, renderers);
        if (glbRoot != null) glbRoot.localScale = Vector3.one * AppliedScale;

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
    /// Works out the uniform scale to apply. Measured in the ALIGNMENT ROOT's space, where
    /// one unit is one real metre — so a target height stays a real-world height even when
    /// the whole hierarchy is shrunk, as preview mode shrinks it.
    /// </summary>
    float ResolveScale(Transform parent, Renderer[] renderers)
    {
        if (targetHeightMetres <= 0f) return modelScale;

        if (renderers.Length == 0)
        {
            Debug.LogWarning("[Loader] targetHeightMetres set but the model has no renderers.");
            return modelScale;
        }

        float parentScaleY = Mathf.Abs(parent.lossyScale.y);
        float worldHeight = MeasureWorldBounds(renderers).size.y;
        float localHeight = parentScaleY > 1e-6f ? worldHeight / parentScaleY : worldHeight;

        if (localHeight <= 1e-6f)
        {
            Debug.LogWarning("[Loader] model has zero height — cannot scale to target.");
            return modelScale;
        }

        float scale = targetHeightMetres / localHeight;
        Debug.Log($"[Loader] model is {localHeight:F2} m tall as authored; " +
                  $"scaling x{scale:F2} to reach {targetHeightMetres} m.");
        return scale;
    }
}
