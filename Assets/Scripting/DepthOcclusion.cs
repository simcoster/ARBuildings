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
/// <b>This is now the only occluder in the app</b>, and it is ON by default. Streetscape
/// geometry — the occluder passes, the cutout, the ghosting and the master switch — was
/// removed on 2026-08-25: Google has no reconstruction of this building, so all it could ever
/// contribute was the terrain slab taking bites out of the model. Depth is the only mechanism
/// that can do the thing actually wanted here, which is to let a car or a pedestrian pass in
/// front of the building and hide it.
/// </para>
///
/// <para>
/// Whether it CAN do that at this site is an open measurement, not an assumption. See
/// <see cref="SampleDepth"/>: if ARCore clamps its far field to ~8 m then a building at 28 m
/// loses the depth test everywhere and the model vanishes, and the fix is a maximum occlusion
/// distance — which needs a custom camera-background material. `depth off` is the escape
/// hatch in the meantime.
/// </para>
/// </summary>
public class DepthOcclusion : MonoBehaviour
{
    [Tooltip("Whether real-world depth is allowed to hide the model. This is now the ONLY " +
             "occluder in the app, so ON is the intended state — a car or a pedestrian in " +
             "front of the building should hide it.")]
    [SerializeField] bool enableOnStart = true;

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

        SampleDepth();
    }

    // --- what ARCore actually thinks the distances are -------------------------------------

    string _depthReadout = "not sampled yet";

    /// <summary>
    /// Reads the depth image on the CPU once a second and reports the metres at the centre of
    /// frame, plus the range across the whole image.
    ///
    /// <para>
    /// This one line decides whether depth occlusion can work at this site at all. ARCore's
    /// depth is useful from about 0.5 m to 5 m and valid to about 8 m; the building is 28 m
    /// away. The question is what ARCore returns beyond its range, and the two answers demand
    /// opposite fixes:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>0, or something larger than the building's distance</b> — far field reads as
    /// "nothing there", the model draws, and near-field cars and pedestrians still occlude it
    /// correctly. Depth occlusion works as asked and there is nothing more to do.</item>
    /// <item><b>Clamped to roughly 8 m</b> — the far field claims the whole world is 8 m away,
    /// the building at 28 m loses the depth test everywhere, and the model vanishes. That is
    /// the 2026-08-23 flicker, and the fix is a maximum occlusion distance, which needs a
    /// custom camera-background material.</item>
    /// </list>
    ///
    /// <para>
    /// Point the phone at the facade from a known distance and read `depth at centre`. Both
    /// answers look identical from a screenshot, which is the whole reason this exists.
    /// </para>
    /// </summary>
    void SampleDepth()
    {
        if (_manager == null || !_manager.enabled)
        {
            _depthReadout = "not sampled (occlusion off)";
            return;
        }

        if (!_manager.TryAcquireEnvironmentDepthCpuImage(out var image))
        {
            _depthReadout = "no CPU depth image available";
            return;
        }

        using (image)
        {
            var plane = image.GetPlane(0);
            var data = plane.data;

            float centre = ReadMetres(data, plane, image.format, image.width / 2, image.height / 2);

            // Coarse grid rather than every pixel: this runs once a second on the main thread
            // and the shape of the range is what matters, not the exact extremes.
            float min = float.MaxValue, max = 0f;
            int valid = 0, total = 0;

            for (int y = 0; y < image.height; y += 8)
            {
                for (int x = 0; x < image.width; x += 8)
                {
                    total++;
                    float m = ReadMetres(data, plane, image.format, x, y);
                    if (m <= 0f) continue;      // 0 is ARCore's "no reading here"
                    valid++;
                    if (m < min) min = m;
                    if (m > max) max = m;
                }
            }

            string range = valid > 0
                ? $"{min:F2}-{max:F2} m over {valid}/{total} samples"
                : $"NO valid samples in {total}";

            _depthReadout = $"{centre:F2} m at centre | {range} | " +
                            $"{image.width}x{image.height} {image.format}";
        }
    }

    /// <summary>
    /// One depth pixel in metres. ARCore hands back either 16-bit millimetres or 32-bit
    /// metres depending on the device and the smoothing mode, and getting the two confused
    /// reads as a factor of a thousand — so both are handled explicitly rather than assumed.
    /// </summary>
    static float ReadMetres(Unity.Collections.NativeArray<byte> data, XRCpuImage.Plane plane,
                            XRCpuImage.Format format, int x, int y)
    {
        int i = y * plane.rowStride + x * plane.pixelStride;
        if (i < 0 || i + 1 >= data.Length) return 0f;

        switch (format)
        {
            case XRCpuImage.Format.DepthUint16:
                return (ushort)(data[i] | (data[i + 1] << 8)) * 0.001f;

            case XRCpuImage.Format.DepthFloat32:
                if (i + 3 >= data.Length) return 0f;
                return System.BitConverter.ToSingle(
                    new[] { data[i], data[i + 1], data[i + 2], data[i + 3] }, 0);

            default:
                return 0f;
        }
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
    /// Two lines for the HUD: whether the only occluder in the app is on, and what ARCore
    /// thinks the distances in front of the camera are. The second is the one that says
    /// whether depth can reach the building or is about to eat it.
    /// </summary>
    public string HudReadout =>
        $"occlusion: depth {(enableOnStart ? "ON" : "OFF")}\n  {_depthReadout}";

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
            report.AppendLine($"depth at centre    : {_depthReadout}");
            return report.ToString();
        }
    }
}
