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

        var gltf = new GltfImport();
        bool success = await gltf.Load(url);

        if (!success)
        {
            Debug.LogError($"GLB load failed — check the source returns binary, not HTML: {url}");
            return;
        }

        await gltf.InstantiateMainSceneAsync(parent);

        // Models often come in with wrong scale/origin. Normalise here so the alignment
        // hierarchy above is the only thing deciding where the building sits.
        if (parent.childCount > 0)
        {
            var glbRoot = parent.GetChild(0);
            glbRoot.localPosition = Vector3.zero;
            glbRoot.localRotation = Quaternion.identity;
        }
    }
}
