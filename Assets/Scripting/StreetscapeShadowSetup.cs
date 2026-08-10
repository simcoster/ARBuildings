using UnityEngine;
using UnityEngine.Rendering;
using Google.XR.ARCoreExtensions;

public class StreetscapeShadowSetup : MonoBehaviour
{
    [SerializeField] ARStreetscapeGeometryManager streetscapeManager;
    [SerializeField] Material shadowCatcher;   // AR/ShadowCatcher from above

    void OnEnable() => streetscapeManager.StreetscapeGeometriesChanged += OnChanged;
    void OnDisable() => streetscapeManager.StreetscapeGeometriesChanged -= OnChanged;

    void OnChanged(ARStreetscapeGeometriesChangedEventArgs args)
    {
        foreach (var geometry in args.Added)
        {
            var r = geometry.GetComponentInChildren<MeshRenderer>();
            if (r == null) continue;

            // Ghost only the mesh whose bounds contain the site; occlude everything else.
            bool isTarget = r.bounds.Contains(targetAnchor.position);
            r.material = isTarget ? ghostMaterial : occluderMaterial;

            r.shadowCastingMode = isTarget
                ? ShadowCastingMode.Off      // the replaced building shouldn't cast a real shadow
                : ShadowCastingMode.On;
            r.receiveShadows = !isTarget;
        }
    }

    void Configure(ARStreetscapeGeometry geometry)
    {
        var renderer = geometry.GetComponentInChildren<MeshRenderer>();
        if (renderer == null) return;

        renderer.material = shadowCatcher;

        // Receive your model's shadow AND cast onto it.
        // ShadowsOnly = contributes to the shadow map but writes no colour,
        // which is what you want for geometry standing in for the real world.
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
    }
}