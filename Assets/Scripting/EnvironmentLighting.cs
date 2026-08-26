using System.Text;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Makes the model reflect the ROOM instead of a default skybox.
///
/// <para>
/// The scene ships with `m_DefaultReflectionMode: 0` (Skybox) and not one reflection probe in
/// it, so every reflective surface on the building — the blue glass panel over the entrance
/// most obviously — mirrors Unity's stock procedural sky. Indoors that is a blue gradient in a
/// grey room, and it is one of the reasons the model reads as pasted onto the photograph
/// rather than standing in it.
/// </para>
///
/// <para>
/// ARCore can supply the real thing. Its Environmental HDR mode produces an HDR cubemap of the
/// surroundings, and AR Foundation 6.5 exposes it on Android through
/// <see cref="AREnvironmentProbeManager"/> — `ARCoreEnvironmentProbeSubsystem` advertises
/// `supportsAutomaticPlacement` and `supportsEnvironmentTextureHDR`, and its `Start()` is
/// literally documented as "enabling the HDR Environmental Light Estimation". Nothing in this
/// project had ever asked for it.
/// </para>
///
/// <para>
/// Each probe ARCore places carries a <see cref="ReflectionProbe"/> with the cubemap as its
/// custom baked texture. Rather than rely on probe volumes reaching a building 28 m away, the
/// newest cubemap is also published as the SCENE's reflection source
/// (<see cref="RenderSettings.customReflectionTexture"/>), which every renderer falls back to
/// regardless of where it stands.
/// </para>
///
/// <para>
/// Added from code, not to the scene, for the same reason as <see cref="DepthOcclusion"/>:
/// the Editor holds the scene file and this had to be testable the same day.
/// </para>
/// </summary>
public class EnvironmentLighting : MonoBehaviour
{
    [Tooltip("Whether ARCore's HDR environment cubemap drives scene reflections. Off falls " +
             "back to the skybox, which is what shipped before.")]
    [SerializeField] bool useRoomReflections = true;

    [Tooltip("How strongly the room shows up in reflections. 1 is physically neutral; the " +
             "estimate is noisy indoors, so this is worth turning down rather than off.")]
    [Range(0f, 1f)]
    [SerializeField] float reflectionIntensity = 1f;

    AREnvironmentProbeManager _probes;
    ReflectionProbe _newest;
    Texture _cubemap;

    // What the scene had before this component touched anything, so `probe off` restores the
    // original look rather than an approximation of it.
    DefaultReflectionMode _originalMode;
    Texture _originalTexture;
    float _originalIntensity;
    bool _captured;

    string _status = "not started";

    public bool Enabled
    {
        get => useRoomReflections;
        set { useRoomReflections = value; Apply(); }
    }

    public float Intensity
    {
        get => reflectionIntensity;
        set
        {
            reflectionIntensity = Mathf.Clamp01(value);
            if (useRoomReflections) RenderSettings.reflectionIntensity = reflectionIntensity;
        }
    }

    void Awake()
    {
        CaptureOriginalSettings();

        // ARTrackableManager reads its XROrigin off its OWN GameObject, so the manager has to
        // live on the origin — not on this component's object, and not on the camera.
        var origin = FindAnyObjectByType<XROrigin>();
        if (origin == null)
        {
            _status = "no XROrigin — cannot place probes";
            Debug.LogWarning("[Env] no XROrigin in the scene; room reflections unavailable");
            return;
        }

        _probes = origin.GetComponent<AREnvironmentProbeManager>();
        if (_probes == null) _probes = origin.gameObject.AddComponent<AREnvironmentProbeManager>();

        _probes.automaticPlacementRequested = true;
        _probes.environmentTextureHDRRequested = true;

        _probes.trackablesChanged.AddListener(OnProbesChanged);

        Debug.Log("[Env] environment probe manager attached to " + origin.name);
    }

    void OnDestroy()
    {
        if (_probes != null) _probes.trackablesChanged.RemoveListener(OnProbesChanged);
    }

    void Start() => Apply();

    void CaptureOriginalSettings()
    {
        if (_captured) return;
        _captured = true;
        _originalMode = RenderSettings.defaultReflectionMode;
        _originalTexture = RenderSettings.customReflectionTexture;
        _originalIntensity = RenderSettings.reflectionIntensity;
    }

    void OnProbesChanged(ARTrackablesChangedEventArgs<AREnvironmentProbe> args)
    {
        foreach (var probe in args.added) Adopt(probe);
        foreach (var probe in args.updated) Adopt(probe);
    }

    /// <summary>
    /// Takes the cubemap off whichever probe reported last. ARCore places probes
    /// automatically and only ever has a handful; the newest one is the best guess at the
    /// room as it is now, and picking by recency avoids having to reason about which probe
    /// volume a building 28 m away falls inside.
    /// </summary>
    void Adopt(AREnvironmentProbe probe)
    {
        if (probe == null) return;

        var reflection = probe.GetComponent<ReflectionProbe>();
        if (reflection == null) return;

        _newest = reflection;

        // customBakedTexture is what AREnvironmentProbe writes ARCore's cubemap into.
        if (reflection.customBakedTexture != null)
            _cubemap = reflection.customBakedTexture;

        Apply();
    }

    void Apply()
    {
        CaptureOriginalSettings();

        if (!useRoomReflections)
        {
            RenderSettings.defaultReflectionMode = _originalMode;
            RenderSettings.customReflectionTexture = _originalTexture;
            RenderSettings.reflectionIntensity = _originalIntensity;
            _status = "off — skybox reflections restored";
            return;
        }

        RenderSettings.reflectionIntensity = reflectionIntensity;

        if (_cubemap == null)
        {
            _status = "waiting for ARCore's first HDR cubemap";
            return;
        }

        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = _cubemap;
        _status = $"room cubemap live ({_cubemap.width}px)";
    }

    /// <summary>
    /// Distinguishes the three states that look identical on screen: not asked for, asked for
    /// but ARCore has not produced a cubemap yet, and live.
    /// </summary>
    public string StateReport
    {
        get
        {
            var report = new StringBuilder();
            report.AppendLine($"room reflections   : {(useRoomReflections ? "ON" : "OFF")} " +
                              $"intensity {reflectionIntensity:F2}");
            report.AppendLine($"reflection status  : {_status}");
            report.AppendLine($"probe manager      : " +
                              (_probes == null ? "ABSENT"
                                               : $"enabled={_probes.enabled}, " +
                                                 $"HDR={_probes.environmentTextureHDRRequested}, " +
                                                 $"auto={_probes.automaticPlacementRequested}, " +
                                                 $"probes={_probes.trackables.count}"));
            report.AppendLine($"reflection source  : {RenderSettings.defaultReflectionMode}" +
                              (RenderSettings.customReflectionTexture != null
                                   ? $" ({RenderSettings.customReflectionTexture.width}px cubemap)"
                                   : " (no custom texture)"));
            return report.ToString();
        }
    }

    public string HudReadout => $"reflect: {_status}";
}
