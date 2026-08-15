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

        if (el <= 0f)                       // below the horizon
        {
            sunLight.enabled = false;
            return;
        }
        sunLight.enabled = true;

        float azRad = az * Mathf.Deg2Rad;
        float elRad = el * Mathf.Deg2Rad;

        // ARCore geospatial world frame is EUS: +X East, +Y Up, +Z South => North is -Z.
        // This is the direction TOWARD the sun.
        Vector3 toSun = new Vector3(
            Mathf.Cos(elRad) * Mathf.Sin(azRad),
            Mathf.Sin(elRad),
           -Mathf.Cos(elRad) * Mathf.Cos(azRad));

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