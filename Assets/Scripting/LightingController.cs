using Google.XR.ARCoreExtensions;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class LightingController : MonoBehaviour
{
    [Header("Scene references")]
    [SerializeField] Light sunLight;
    [SerializeField] ARCameraManager cameraManager;
    [SerializeField] Volume postVolume;

    [Header("Site (must match buildings.json)")]
    [SerializeField] double latitude = 32.081234;
    [SerializeField] double longitude = 34.812345;

    [Header("Sun")]
    [Tooltip("Sun moves ~0.25 deg/min. No need to recompute every frame.")]
    [SerializeField] float sunUpdateIntervalSeconds = 60f;
    [Tooltip("Lumens -> Unity intensity. Calibrate on device; this is a starting point.")]
    [SerializeField] float intensityDivisor = 1000f;

    [Tooltip("Pin the sun at a fixed mid-morning angle and intensity, ignoring the real " +
             "solar position and light estimation. For previewing indoors or after dark, " +
             "where the real sun is below the horizon and the model would render black.")]
    [SerializeField] bool forceDaylight = false;

    [Tooltip("Elevation and azimuth of the forced sun, degrees.")]
    [SerializeField] float forcedSunElevation = 45f;
    [SerializeField] float forcedSunAzimuth = 135f;
    [SerializeField] float forcedSunIntensity = 1.4f;

    [Tooltip("While forced daylight is on, point the sun where ARCore says the light " +
             "actually comes from, instead of the fixed azimuth above.\n\n" +
             "Forced daylight exists for previewing INDOORS, which is precisely where the " +
             "solar position is meaningless and the room's own lighting is the thing the " +
             "shadow has to agree with — a ceiling lamp overhead and a 45 deg sun from the " +
             "south-east put the shadow in visibly different places. Colour and intensity " +
             "still come from forced daylight, or a dim room would undo it.")]
    [SerializeField] bool matchEstimatedLight = true;

    [Tooltip("Estimated direction is per-frame and jitters. Lower = steadier shadow.")]
    [SerializeField] float estimatedLightSmoothing = 3f;

    [Header("North alignment")]
    [Tooltip("Optional. Found automatically. Needed to know which way Unity's +Z actually " +
             "points, without which the sun — and every shadow — is aimed arbitrarily.")]
    [SerializeField] AREarthManager earthManager;

    [Tooltip("Used only until ARCore knows where north is: indoors, in preview, or before " +
             "VPS localizes. Spin it until the shadows match the room you're standing in.")]
    [SerializeField] float fallbackNorthOffsetDeg = 0f;

    [Header("Exposure matching")]
    [SerializeField] bool driveExposure = true;
    [Tooltip("Lower = smoother. Too fast and the model visibly pulses.")]
    [SerializeField] float exposureSmoothing = 1.5f;
    [SerializeField] float middleGrey = 0.18f;

    // ARCore's averageBrightness units are not documented as normalised, and post-processing
    // in AR Foundation covers the camera feed as well as the model — so an out-of-range
    // reading blows out the ENTIRE frame, not just the building. Always clamp.
    [Tooltip("Hard limits on computed exposure, in EV. Widen only once calibrated on device.")]
    [SerializeField] float minExposureEV = -2f;
    [SerializeField] float maxExposureEV = 2f;

    [Tooltip("Log the first few brightness readings so the curve can be calibrated.")]
    [SerializeField] bool logExposureSamples = true;

    [Header("Aerial perspective")]
    [SerializeField] bool driveFogColour = true;
    [SerializeField] int skySampleIntervalFrames = 30;
    [Range(0, 0.5f)]
    [SerializeField] float skySampleTopFraction = 0.2f;

    ColorAdjustments colorAdjustments;
    float smoothedExposure;
    int _exposureLogCount;
    float sunTimer;
    int frameCounter;
    bool loggedEstimationMode;

    // Wire this to a debug Text element � you will want it on site.
    public string DebugReadout { get; private set; } = "";

    // ------------------------------------------------- what ARCore actually reports

    /// <summary>
    /// Direction the estimated main light TRAVELS (light -> scene), matching Unity's
    /// convention for Light.transform.forward. Negate it to point at the source.
    /// Null until an estimated frame arrives.
    /// </summary>
    public Vector3? EstimatedLightTravel { get; private set; }

    public float EstimatedLumens { get; private set; }
    public Color EstimatedColour { get; private set; } = Color.white;

    /// <summary>Solar position we compute ourselves, for comparison against the estimate.</summary>
    public float SolarAzimuthDeg { get; private set; }
    public float SolarElevationDeg { get; private set; }

    /// <summary>Where the ambient probe says most of the light energy comes from.</summary>
    public Vector3 AmbientPeakDirection { get; private set; } = Vector3.up;

    /// <summary>
    /// Distinct bright lobes in the ambient probe. See <see cref="AnalyseAmbient"/> for why
    /// this can never resolve individual lamps.
    /// </summary>
    public int AmbientLobeCount { get; private set; }

    /// <summary>Peak-to-average ratio of the ambient probe. ~1 = flat, higher = directional.</summary>
    public float AmbientDirectionality { get; private set; } = 1f;

    // A fixed direction set, sampled once, so the probe analysis allocates nothing per frame.
    static Vector3[] _sampleDirections;
    Color[] _sampleColours;
    float[] _sampleLuma;
    float _ambientTimer;

    void Start()
    {
        if (postVolume != null && !postVolume.profile.TryGet(out colorAdjustments))
            Debug.LogWarning("LightingController: Volume has no Color Adjustments override � " +
                             "exposure matching will do nothing.");

        // Flags enum. NOT LightEstimation.EnvironmentalHDR � that member lives on the
        // deprecated LightEstimationMode enum and will not compile here.
        if (cameraManager != null)
            cameraManager.requestedLightEstimation =
                LightEstimation.AmbientSphericalHarmonics |
                LightEstimation.MainLightDirection |
                LightEstimation.MainLightIntensity;

        UpdateSun();
    }

    void OnEnable()
    {
        if (cameraManager != null) cameraManager.frameReceived += OnFrame;
    }

    void OnDisable()
    {
        if (cameraManager != null) cameraManager.frameReceived -= OnFrame;
    }

    void Update()
    {
        sunTimer += Time.deltaTime;
        if (sunTimer >= sunUpdateIntervalSeconds)
        {
            sunTimer = 0f;
            UpdateSun();
        }

        // Twice a second is ample — the probe itself is heavily smoothed by ARCore.
        _ambientTimer += Time.deltaTime;
        if (_ambientTimer >= 0.5f)
        {
            _ambientTimer = 0f;
            AnalyseAmbient();
        }
    }

    /// <summary>
    /// Degrees to add to a true bearing to get the equivalent Unity yaw. Zero would mean
    /// Unity's +Z happens to point at true north, which it only does by coincidence.
    /// </summary>
    public float NorthOffsetDeg { get; private set; }

    /// <summary>True once ARCore has told us where north is; false indoors and in preview.</summary>
    public bool NorthKnown { get; private set; }

    /// <summary>
    /// Derives Unity's heading error from the camera: ARCore reports the camera's true
    /// bearing, and Unity knows its yaw, so the difference is the rotation between frames.
    /// </summary>
    void UpdateNorthAlignment()
    {
        if (earthManager == null) earthManager = FindAnyObjectByType<AREarthManager>();

        if (earthManager == null ||
            earthManager.EarthTrackingState != TrackingState.Tracking)
        {
            if (!NorthKnown) NorthOffsetDeg = fallbackNorthOffsetDeg;
            return;   // a previously measured offset is better than falling back to a guess
        }

        var camera = Camera.main;
        if (camera == null) return;

        var pose = earthManager.CameraGeospatialPose;

        NorthOffsetDeg = Mathf.DeltaAngle(0f, camera.transform.eulerAngles.y - (float)pose.Heading);
        NorthKnown = true;
    }

    /// <summary>Lighting state for the capture button — the answer to "is the sun real?".</summary>
    public string StateReport =>
        $"sun computed az/el : {SolarAzimuthDeg:F1} / {SolarElevationDeg:F1} deg\n" +
        $"north offset       : {NorthOffsetDeg:F1} deg {(NorthKnown ? "(MEASURED from VPS)" : "(GUESSED - shadows meaningless)")}\n" +
        $"forced daylight    : {forceDaylight}\n" +
        $"sun light          : {(sunLight != null ? $"enabled={sunLight.enabled} intensity={sunLight.intensity:F2} colour={sunLight.color}" : "none")}\n" +
        $"sun world forward  : {(sunLight != null ? sunLight.transform.forward.ToString() : "n/a")}\n" +
        $"sun direction from : {(forceDaylight ? (matchEstimatedLight && EstimatedLightTravel.HasValue ? "ROOM LIGHT (ARCore estimate)" : $"forced az/el {forcedSunAzimuth:F0}/{forcedSunElevation:F0}") : "computed solar position")}\n" +
        $"estimation mode    : {(cameraManager != null ? cameraManager.currentLightEstimation.ToString() : "no camera manager")}\n" +
        $"estimated dir      : {(EstimatedLightTravel.HasValue ? EstimatedLightTravel.Value.ToString() : "none")}\n" +
        $"estimated lumens   : {EstimatedLumens:F0}\n" +
        $"estimated colour   : {EstimatedColour}\n" +
        $"ambient lobes      : {AmbientLobeCount} (directionality {AmbientDirectionality:F2}x)\n" +
        $"exposure           : {smoothedExposure:F2} EV (driven {driveExposure})\n" +
        $"intensity divisor  : {intensityDivisor}\n";

    // ------------------------------------------------------- ambient probe analysis

    /// <summary>
    /// Directions the probe is sampled in, spread evenly over the sphere by the Fibonacci
    /// spiral. Shared and built once — the visualiser reads the same set.
    /// </summary>
    public static Vector3[] SampleDirections
    {
        get
        {
            if (_sampleDirections != null) return _sampleDirections;

            const int count = 64;
            _sampleDirections = new Vector3[count];
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));

            for (int i = 0; i < count; i++)
            {
                float y = 1f - i / (count - 1f) * 2f;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = golden * i;
                _sampleDirections[i] =
                    new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
            }

            return _sampleDirections;
        }
    }

    /// <summary>Per-direction irradiance from the last analysis, for the debug view.</summary>
    public Color[] SampleColours => _sampleColours;

    /// <summary>
    /// Reconstructs irradiance from the ambient spherical harmonics and looks for bright
    /// lobes.
    ///
    /// This CANNOT resolve individual light sources. ARCore reports order-2 spherical
    /// harmonics — 9 coefficients per channel — which is a deliberately very low-frequency
    /// description of the environment. Two lamps less than about 60 degrees apart blur into
    /// one lobe no matter how distinct they really are. Treat the count as "is the lighting
    /// directional or wrapped-around", not as a light inventory.
    /// </summary>
    void AnalyseAmbient()
    {
        var directions = SampleDirections;

        _sampleColours ??= new Color[directions.Length];
        _sampleLuma ??= new float[directions.Length];

        RenderSettings.ambientProbe.Evaluate(directions, _sampleColours);

        float peak = 0f, sum = 0f;
        int peakIndex = 0;

        for (int i = 0; i < directions.Length; i++)
        {
            var c = _sampleColours[i];
            float luma = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
            _sampleLuma[i] = luma;
            sum += luma;

            if (luma > peak) { peak = luma; peakIndex = i; }
        }

        float average = sum / directions.Length;
        AmbientPeakDirection = directions[peakIndex];
        AmbientDirectionality = average > 1e-6f ? peak / average : 1f;

        // A lobe is a sample brighter than 75% of peak whose neighbours within 50 degrees
        // are all dimmer — i.e. a local maximum, not a point on the shoulder of one.
        int lobes = 0;
        float threshold = peak * 0.75f;

        for (int i = 0; i < directions.Length; i++)
        {
            if (_sampleLuma[i] < threshold) continue;

            bool isLocalMax = true;
            for (int k = 0; k < directions.Length && isLocalMax; k++)
            {
                if (k == i) continue;
                if (Vector3.Dot(directions[i], directions[k]) < 0.64f) continue;   // >50 deg
                if (_sampleLuma[k] > _sampleLuma[i]) isLocalMax = false;
            }

            if (isLocalMax) lobes++;
        }

        AmbientLobeCount = lobes;
    }

    // ---------------------------------------------------------------- sun

    /// <summary>
    /// Fake daylight, for previewing indoors or at night. Without it the sun is switched
    /// off below the horizon and the model renders as a black silhouette — which looks
    /// exactly like a broken placement.
    /// </summary>
    public bool ForceDaylight
    {
        get => forceDaylight;
        set { forceDaylight = value; UpdateSun(); }
    }

    void UpdateSun()
    {
        if (sunLight == null) return;

        float az, el;

        if (forceDaylight)
        {
            az = forcedSunAzimuth;
            el = forcedSunElevation;
            sunLight.color = Color.white;
            sunLight.intensity = forcedSunIntensity;
        }
        else
        {
            SolarPosition.Compute(latitude, longitude, System.DateTime.UtcNow, out az, out el);
        }

        SolarAzimuthDeg = az;
        SolarElevationDeg = el;

        // BEFORE the horizon test below: that path returns early, so at night north was
        // never measured at all and stayed GUESSED until the next sunrise.
        UpdateNorthAlignment();

        if (el <= 0f)                       // below the horizon
        {
            sunLight.enabled = false;
            return;
        }
        sunLight.enabled = true;

        // Azimuth is a TRUE bearing; the light lives in Unity's world, and the two are not
        // the same frame. Unity's origin and heading are wherever the AR session happened to
        // start — that is exactly why AREarthManager.Convert() exists. Rotate the bearing
        // into Unity's frame before using it, or the sun (and every cast shadow) points in a
        // direction that changes with whichever way the phone was facing at launch.
        float yawRad = (az + NorthOffsetDeg) * Mathf.Deg2Rad;
        float elRad = el * Mathf.Deg2Rad;

        // Unity yaw: 0 faces +Z and increases clockwise, so a bearing is (sin, cos) in XZ.
        // This is the direction TOWARD the sun.
        Vector3 toSun = new Vector3(
            Mathf.Cos(elRad) * Mathf.Sin(yawRad),
            Mathf.Sin(elRad),
            Mathf.Cos(elRad) * Mathf.Cos(yawRad));

        sunLight.transform.rotation = Quaternion.LookRotation(-toSun);
    }

    // ---------------------------------------------------- per-frame estimation

    void OnFrame(ARCameraFrameEventArgs args)
    {
        if (!loggedEstimationMode)
        {
            loggedEstimationMode = true;
            Debug.Log($"Light estimation granted: {cameraManager.currentLightEstimation}");
        }

        var le = args.lightEstimation;

        // Recorded but NOT applied: the sun's direction stays computed from the site's real
        // solar position, which is far more reliable than a single-frame estimate. Keeping
        // the estimate lets the debug view show how far apart the two are.
        if (le.mainLightDirection.HasValue) EstimatedLightTravel = le.mainLightDirection.Value;
        if (le.mainLightIntensityLumens.HasValue) EstimatedLumens = le.mainLightIntensityLumens.Value;
        if (le.mainLightColor.HasValue) EstimatedColour = le.mainLightColor.Value;

        // Indoors, the estimate is the only thing that knows where the light IS. Applied
        // only under forced daylight: outdoors the computed solar position is far steadier
        // than a single frame, which is why the estimate is otherwise recorded and ignored.
        if (sunLight != null && forceDaylight && matchEstimatedLight &&
            EstimatedLightTravel.HasValue &&
            EstimatedLightTravel.Value.sqrMagnitude > 1e-4f)
        {
            var target = Quaternion.LookRotation(EstimatedLightTravel.Value.normalized);

            sunLight.transform.rotation = Quaternion.Slerp(
                sunLight.transform.rotation, target,
                Time.deltaTime * estimatedLightSmoothing);

            // UpdateSun() switches the light off below the computed horizon; the room's
            // light has no horizon and must survive that.
            sunLight.enabled = true;
        }

        // Sun COLOUR and INTENSITY from estimation � direction stays computed.
        // Forced daylight owns both, or a dim room would immediately undo it.
        if (sunLight != null && !forceDaylight)
        {
            if (le.mainLightColor.HasValue)
                sunLight.color = le.mainLightColor.Value;

            if (le.mainLightIntensityLumens.HasValue)
                sunLight.intensity = le.mainLightIntensityLumens.Value / intensityDivisor;
        }

        // Ambient � the L2 spherical harmonics are the useful part.
        if (le.ambientSphericalHarmonics.HasValue)
            RenderSettings.ambientProbe = le.ambientSphericalHarmonics.Value;

        if (driveExposure && le.averageBrightness.HasValue)
            DriveExposure(le.averageBrightness.Value);

        if (driveFogColour && ++frameCounter % skySampleIntervalFrames == 0)
            SampleSkyColour();

        DebugReadout =
            $"est: {cameraManager.currentLightEstimation}\n" +
            $"exposure: {smoothedExposure:F2} EV\n" +
            $"sun: {(sunLight != null && sunLight.enabled ? "up" : "down")}";
    }

    // ------------------------------------------------------------ exposure

    void DriveExposure(float averageBrightness)
    {
        float rawTarget = Mathf.Log(Mathf.Max(averageBrightness, 0.001f) / middleGrey, 2f);
        float target = Mathf.Clamp(rawTarget, minExposureEV, maxExposureEV);

        // Heavy smoothing: match the camera's SETTLED exposure. Chasing it per frame pumps.
        smoothedExposure = Mathf.Lerp(smoothedExposure, target,
                                      Time.deltaTime * exposureSmoothing);

        if (colorAdjustments != null)
            colorAdjustments.postExposure.value = smoothedExposure;

        if (logExposureSamples && _exposureLogCount < 5)
        {
            _exposureLogCount++;
            Debug.Log($"[Exposure] avgBrightness={averageBrightness:F4} " +
                      $"rawEV={rawTarget:F2} clampedEV={target:F2} " +
                      $"smoothed={smoothedExposure:F2}");
        }
    }

    // ------------------------------------------------------ aerial perspective

    void SampleSkyColour()
    {
        if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return;

        using (image)
        {
            const int N = 32;                       // downsample hard � this runs on the CPU
            var conversion = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(N, N),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.None
            };

            int size = image.GetConvertedDataSize(conversion);
            var buffer = new NativeArray<byte>(size, Allocator.Temp);
            try
            {
                image.Convert(conversion, buffer);

                int rows = Mathf.Max(1, Mathf.RoundToInt(N * skySampleTopFraction));
                long r = 0, g = 0, b = 0;
                int n = 0;

                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < N; x++)
                    {
                        int i = (y * N + x) * 4;
                        r += buffer[i]; g += buffer[i + 1]; b += buffer[i + 2];
                        n++;
                    }

                var sky = new Color(r / (n * 255f), g / (n * 255f), b / (n * 255f), 1f);
                RenderSettings.fogColor = Color.Lerp(RenderSettings.fogColor, sky, 0.25f);
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }
}