using System.Text;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// The master switch for ENVIRONMENT DEPTH occlusion — the second, invisible occluder in this
/// app, and the one that survived every measurement made against the first.
///
/// <para>
/// The scene carries an <see cref="AROcclusionManager"/> with environment depth set to
/// <c>Fastest</c> and temporal smoothing on. Nothing in this project ever asked for it and no
/// custom shader samples it, which is exactly why it went unnoticed: it does not need one.
/// <c>ARCoreBackground.shader</c> draws the camera feed with <c>ZWrite On</c> and writes
/// <c>gl_FragDepth</c> straight from <c>_EnvironmentDepth</c>, so ARCore's depth map lands in
/// the depth buffer before any of our geometry is drawn, and EVERY opaque object in the scene
/// is then depth-tested against it. The model does not have to opt in to be occluded by it.
/// </para>
///
/// <para>
/// Two reasons that is wrong here, one general and one specific to this device:
/// </para>
///
/// <list type="bullet">
/// <item>ARCore's depth is useful from roughly 0.5 m to 5 m and valid to about 8 m. The
/// building is <b>28 m</b> away. There is no reading at that range that can be trusted to
/// decide whether the model is in front of the world or behind it.</item>
/// <item>On the A35 the depth pipeline is failing outright, roughly ten times a second:
/// <c>spherical_rectifier.cc:159 RET_CHECK failure … Only kUnrectifiedOriginal is supported
/// for ComputeDisparity</c>. Motion stereo is erroring per frame while its output is still
/// being written into the depth buffer every frame.</item>
/// </list>
///
/// <para>
/// That is the shape of the flicker recorded on 2026-08-23 — whole model absent in one frame
/// and back in the next, jagged bites out of it at the edges, worse when tilting, worse with a
/// table in view. None of the diagnostics added that day could see it: <c>renderers
/// drawing : 0 of 40</c> counts STREETSCAPE renderers, and the camera background is not one;
/// culling bounds are correct because culling was never involved; the anchor reads
/// <c>Tracking, active=True</c> because the anchor is fine.
/// </para>
///
/// <para>
/// Off by default, therefore. It is a toggle rather than a deletion because on an iPad the
/// same switch is worth turning ON: LiDAR gives a real depth image at real range, and there
/// this is the mechanism that puts people and cars in front of the model — the one thing
/// streetscape geometry can never do.
/// </para>
/// </summary>
public class DepthOcclusion : MonoBehaviour
{
    [Tooltip("Whether real-world depth is allowed to hide the model. OFF on Android: ARCore " +
             "depth cannot reach the building and is broken on this device. Worth turning ON " +
             "for LiDAR hardware.")]
    [SerializeField] bool enableOnStart = false;

    [Tooltip("Depth mode requested when this is switched on. Fastest is the right trade for " +
             "occlusion — the extra quality only sharpens edges.")]
    [SerializeField] EnvironmentDepthMode modeWhenOn = EnvironmentDepthMode.Fastest;

    [Tooltip("How often to check that the subsystem still agrees with the request. The " +
             "manager only forwards a request once its subsystem exists, and the subsystem " +
             "does not exist yet at Start.")]
    [SerializeField] float watchdogSeconds = 1f;

    AROcclusionManager _manager;
    float _timer;

    /// <summary>
    /// Whether real-world depth may occlude the model.
    ///
    /// <para>
    /// The lever is <c>AROcclusionManager.enabled</c>, and which lever is used matters more
    /// than it looks. The background material's <c>ARCORE_ENVIRONMENT_DEPTH_ENABLED</c>
    /// keyword is only ever pushed by <c>ARCameraBackground.OnOcclusionFrameReceived</c>, i.e.
    /// on a frame event — so anything that stops depth frames arriving leaves the keyword
    /// stuck ON with a stale texture, rather than turning it off. Requesting
    /// <c>EnvironmentDepthMode.Disabled</c> on its own does exactly that.
    /// </para>
    ///
    /// <para>
    /// Disabling the manager does not, and it is the path the package explicitly supports.
    /// <c>AROcclusionManager.OnDisable</c> stops the subsystem, destroys the textures and then
    /// fires one last frame event on purpose — its own comment reads "We are firing a
    /// frameReceived event when occlusion manager is disabled because ARCameraBackground needs
    /// it to set the shader keywords." With the subsystem stopped that event carries the
    /// keyword in the DISABLED list, so the background material is cleaned up properly.
    /// </para>
    ///
    /// <para>
    /// The depth mode is still set to <c>Disabled</c> first, while the manager is enabled and
    /// the setter can still reach the subsystem, so that ARCore is told to stop computing a
    /// depth image at all and the failing motion-stereo pipeline goes quiet too.
    /// </para>
    /// </summary>
    public bool Enabled
    {
        get => enableOnStart;
        set
        {
            enableOnStart = value;
            Apply();
        }
    }

    void Awake()
    {
        if (_manager == null) _manager = FindAnyObjectByType<AROcclusionManager>();
    }

    void Start() => Apply();

    void Update()
    {
        if (watchdogSeconds <= 0f) return;

        _timer += Time.deltaTime;
        if (_timer < watchdogSeconds) return;
        _timer = 0f;

        // The AROcclusionManager setters are a no-op until its subsystem exists, and the
        // subsystem is created with the AR session — after Start. Without this the request
        // made at startup is silently dropped and the scene's serialized values win, which
        // looks exactly like the switch not working.
        if (_manager != null && _manager.enabled != enableOnStart)
            Apply();
    }

    void Apply()
    {
        if (_manager == null) _manager = FindAnyObjectByType<AROcclusionManager>();
        if (_manager == null)
        {
            Debug.Log("[Depth] no AROcclusionManager in the scene — nothing to switch");
            return;
        }

        if (enableOnStart)
        {
            // Enable first: the mode setters only reach the subsystem while the manager is
            // enabled, and OnEnable is what creates it.
            _manager.enabled = true;
            _manager.requestedEnvironmentDepthMode = modeWhenOn;
            _manager.requestedOcclusionPreferenceMode =
                OcclusionPreferenceMode.PreferEnvironmentOcclusion;
        }
        else
        {
            // Order matters, both ways round. These two are set while the manager is still
            // enabled so they actually reach the subsystem — the depth mode is what tells
            // ARCore to stop producing depth at all.
            _manager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
            _manager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Disabled;

            // …and disabling comes last, because that is what makes the package fire the
            // frame event that clears the background material's depth keyword.
            _manager.enabled = false;
        }

        Debug.Log($"[Depth] environment depth occlusion {(enableOnStart ? "ON" : "OFF")} " +
                  $"(manager enabled={_manager.enabled}, " +
                  $"requested {_manager.requestedEnvironmentDepthMode}, " +
                  $"preference {_manager.requestedOcclusionPreferenceMode})");
    }

    /// <summary>
    /// Enough to tell "the switch is off" from "the switch did not take", which are the two
    /// states that matter and are indistinguishable from a screenshot. <c>current</c> is what
    /// the subsystem is actually doing; <c>requested</c> is only what we asked for.
    /// </summary>
    public string StateReport
    {
        get
        {
            var report = new StringBuilder();
            report.AppendLine($"depth occlusion    : {(enableOnStart ? "ON" : "OFF")}");

            if (_manager == null)
            {
                report.AppendLine("depth manager      : ABSENT (nothing can occlude via depth)");
                return report.ToString();
            }

            report.AppendLine($"depth manager      : enabled={_manager.enabled} " +
                              "(the lever — a disabled manager clears the background keyword)");
            report.AppendLine($"depth requested    : {_manager.requestedEnvironmentDepthMode}, " +
                              $"preference {_manager.requestedOcclusionPreferenceMode}");
            report.AppendLine($"depth current      : {_manager.currentEnvironmentDepthMode}, " +
                              $"preference {_manager.currentOcclusionPreferenceMode}");

            report.AppendLine("depth texture      : " +
                              (_manager.TryGetEnvironmentDepthTexture(out var texture) && texture != null
                                   ? $"{texture.width}x{texture.height} — a depth image IS being " +
                                     "produced, and the background pass writes it into the buffer"
                                   : "none"));
            return report.ToString();
        }
    }
}
