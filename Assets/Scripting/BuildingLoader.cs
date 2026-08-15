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

    [Tooltip("File name inside Assets/StreamingAssets, e.g. placeholder-building.glb")]
    [SerializeField] string streamingAssetsFile = "placeholder-building.glb";

    [Tooltip("Must return binary GLB, not an HTML page. If loading fails, curl -L the URL " +
             "and check the first bytes before blaming glTFast.")]
    [SerializeField] string modelUrl;

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
        if (parent.childCount > 0)
        {
            var glbRoot = parent.GetChild(0);
            glbRoot.localPosition = Vector3.zero;
            glbRoot.localRotation = Quaternion.identity;
        }

        // Zero renderers means the GLB parsed but produced nothing visible — a very
        // different problem from "it placed somewhere I can't see".
        var renderers = parent.GetComponentsInChildren<Renderer>();
        RendererCount = renderers.Length;

        if (renderers.Length > 0)
        {
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            BoundsSize = b.size;
        }

        State = LoadState.Loaded;
        LastMessage = $"{RendererCount} renderers";
        Debug.Log($"[Loader] loaded {RendererCount} renderers, bounds {BoundsSize}");
    }
}
