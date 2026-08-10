using GLTFast;
using UnityEngine;

public class BuildingLoader : MonoBehaviour
{
    public static BuildingLoader Instance;

    [SerializeField] string modelUrl;

    void Awake() => Instance = this;

    public async void LoadInto(Transform parent)
    {
        var gltf = new GltfImport();
        bool success = await gltf.Load(modelUrl);

        if (!success)
        {
            Debug.LogError("GLB load failed — check the URL returns binary, not HTML");
            return;
        }

        await gltf.InstantiateMainSceneAsync(parent);
    }
}