using System;
using System.Globalization;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Task Manager-style CPU / GPU / NPU utilisation for the HUD.
///
/// The first version died silently on the phone: <c>Process.GetCurrentProcess()</c>
/// throws (or is stripped) under IL2CPP, Awake aborted, and every meter stayed n/a.
/// This sampler never uses that type. Each rail has a chain that ends on a number
/// Unity can always produce, so the bars move even when sysfs is sealed off.
///
/// CPU prefers <c>/proc/stat</c> when the counters actually increment (many
/// Androids expose the file but freeze it). Then this process via
/// <c>android.os.Process.getElapsedCpuTime</c> / <c>/proc/self/stat</c>. Then
/// main-thread time vs the frame budget.
/// GPU prefers Mali/Adreno sysfs, then XR GPU time (seconds), then
/// FrameTimingManager (needs Frame Timing Stats in Player Settings).
/// NPU prefers sysfs; otherwise the segmenter's duty cycle, or 0% when idle.
/// </summary>
public class PerfMeters : MonoBehaviour
{
    const float SampleSeconds = 0.25f;
    const float Smooth = 0.35f;
    const long ClkTck = 100; // Android CLOCKS_PER_SEC

    static readonly string[] GpuPaths =
    {
        "/sys/kernel/gpu/gpu_busy",
        "/sys/kernel/gpu/gpu_busy_percentage",
        "/sys/class/misc/mali0/device/utilization",
        "/sys/class/misc/mali0/device/gpu_busy",
        "/sys/devices/platform/mali.0/utilization",
        "/sys/class/kgsl/kgsl-3d0/gpu_busy_percentage",
        "/sys/class/kgsl/kgsl-3d0/devfreq/gpu_load",
        "/proc/mali/utilization",
    };

    static readonly string[] NpuPaths =
    {
        "/sys/kernel/npu/utilization",
        "/sys/class/npu/npu_utilization",
        "/sys/class/npu_exynos/npu_utilization",
        "/sys/class/devfreq/npu/load",
    };

    SemanticOcclusion _seg;
    float _sysfsTimer;
    bool _probed;

    long _cpuIdle, _cpuTotal;
    long _selfTicks;
    float _selfWall;
    long _elapsedCpuMs;
    float _elapsedCpuWall;

    string _gpuPath;
    string _npuPath;

    readonly FrameTiming[] _timings = new FrameTiming[1];
    ProfilerRecorder _mainThread;
    ProfilerRecorder _gpuThread;

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaClass _androidProcess;
#endif

    public float CpuPct { get; private set; } = -1f;
    public float GpuPct { get; private set; } = -1f;
    public float NpuPct { get; private set; } = -1f;

    public bool CpuValid => CpuPct >= 0f;
    public bool GpuValid => GpuPct >= 0f;
    public bool NpuValid => NpuPct >= 0f;

    public string CpuSource { get; private set; } = "n/a";
    public string GpuSource { get; private set; } = "n/a";
    public string NpuSource { get; private set; } = "n/a";

    public string StateReport =>
        $"cpu                : {(CpuValid ? $"{CpuPct:F0}%" : "n/a")} ({CpuSource})\n" +
        $"gpu                : {(GpuValid ? $"{GpuPct:F0}%" : "n/a")} ({GpuSource})\n" +
        $"npu                : {(NpuValid ? $"{NpuPct:F0}%" : "n/a")} ({NpuSource})";

    void OnEnable()
    {
        _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
        _gpuThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GPU Main Thread", 15);
#if UNITY_ANDROID && !UNITY_EDITOR
        try { _androidProcess = new AndroidJavaClass("android.os.Process"); }
        catch { _androidProcess = null; }
#endif
    }

    void OnDisable()
    {
        if (_mainThread.Valid) _mainThread.Dispose();
        if (_gpuThread.Valid) _gpuThread.Dispose();
#if UNITY_ANDROID && !UNITY_EDITOR
        _androidProcess?.Dispose();
        _androidProcess = null;
#endif
    }

    void Awake()
    {
        if (_seg == null) _seg = FindAnyObjectByType<SemanticOcclusion>();
    }

    void Update()
    {
        // Frame timings every tick — cheap, and the only GPU number ARCore will give us.
        SampleFrameGpu();
        SampleFrameCpu();

        _sysfsTimer += Time.unscaledDeltaTime;
        if (_sysfsTimer < SampleSeconds) return;
        _sysfsTimer = 0f;

        if (!_probed)
        {
            ProbeSysfs();
            _probed = true;
        }

        SampleSysfsCpu();
        SampleSysfsGpu();
        SampleNpu();
    }

    void ProbeSysfs()
    {
        try
        {
            _gpuPath = FirstReadable(GpuPaths);
            _npuPath = FirstReadable(NpuPaths);
            if (_gpuPath == null)
                _gpuPath = FindDevfreq("mali", "gpu", "kgsl", "g3d");
            if (_npuPath == null)
                _npuPath = FindDevfreq("npu", "vnpu", "dsp", "eden");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"[Perf] sysfs probe failed: {e.Message}");
        }

        UnityEngine.Debug.Log(
            $"[Perf] gpu={_gpuPath ?? "frame"} npu={_npuPath ?? "duty"}");
    }

    void SampleSysfsCpu()
    {
        if (TryReadProcStat(out long idle, out long total))
        {
            if (_cpuTotal > 0)
            {
                long dIdle = idle - _cpuIdle;
                long dTotal = total - _cpuTotal;
                // Android 10+ often serves a readable /proc/stat whose counters never
                // move. Treating that as success left CPU at n/a forever.
                if (dTotal > 0)
                {
                    _cpuIdle = idle;
                    _cpuTotal = total;
                    SetCpu(100f * (1f - (float)dIdle / dTotal), "proc/stat");
                    return;
                }
            }
            _cpuIdle = idle;
            _cpuTotal = total;
        }

        if (TryReadSelfStat(out long ticks))
        {
            float now = Time.realtimeSinceStartup;
            if (_selfTicks > 0)
            {
                long dTicks = ticks - _selfTicks;
                float dWall = now - _selfWall;
                int cores = Mathf.Max(1, SystemInfo.processorCount);
                if (dTicks > 0 && dWall > 0.001f)
                {
                    _selfTicks = ticks;
                    _selfWall = now;
                    SetCpu(100f * dTicks / (ClkTck * dWall * cores), "proc/self");
                    return;
                }
            }
            _selfTicks = ticks;
            _selfWall = now;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        if (_androidProcess != null)
        {
            try
            {
                long cpuMs = _androidProcess.CallStatic<long>("getElapsedCpuTime");
                float now = Time.realtimeSinceStartup;
                if (_elapsedCpuMs > 0)
                {
                    float dCpu = (cpuMs - _elapsedCpuMs) / 1000f;
                    float dWall = now - _elapsedCpuWall;
                    int cores = Mathf.Max(1, SystemInfo.processorCount);
                    if (dWall > 0.001f)
                    {
                        _elapsedCpuMs = cpuMs;
                        _elapsedCpuWall = now;
                        SetCpu(100f * dCpu / (dWall * cores), "elapsed");
                        return;
                    }
                }
                _elapsedCpuMs = cpuMs;
                _elapsedCpuWall = now;
            }
            catch { /* fall through to frame CPU */ }
        }
#endif
    }

    void SampleFrameCpu()
    {
        if (CpuSource == "proc/stat" || CpuSource == "proc/self" || CpuSource == "elapsed")
            return;

        FrameTimingManager.CaptureFrameTimings();
        uint n = FrameTimingManager.GetLatestTimings(1, _timings);
        if (n > 0 && _timings[0].cpuMainThreadFrameTime > 0f)
        {
            SetCpu(PctOfBudget((float)_timings[0].cpuMainThreadFrameTime), "frame cpu");
            return;
        }

        if (_mainThread.Valid && _mainThread.LastValue > 0)
        {
            float ms = _mainThread.LastValue / 1_000_000f;
            SetCpu(PctOfBudget(ms), "main thread");
            return;
        }

        SetCpu(PctOfBudget(Time.unscaledDeltaTime * 1000f), "delta");
    }

    void SampleSysfsGpu()
    {
        if (_gpuPath != null && TryReadPercent(_gpuPath, out float sys))
            SetGpu(sys, Path.GetFileName(_gpuPath));
    }

    void SampleFrameGpu()
    {
        if (_gpuPath != null) return;

        var xr = XRDisplaySubsystem.activeSubsystem;
        if (xr != null && xr.TryGetAppGPUTimeLastFrame(out float gpuSec) && gpuSec > 0f)
        {
            // XR reports seconds, not milliseconds.
            SetGpu(PctOfBudget(gpuSec * 1000f), "xr gpu");
            return;
        }

        FrameTimingManager.CaptureFrameTimings();
        uint n = FrameTimingManager.GetLatestTimings(1, _timings);
        if (n > 0 && _timings[0].gpuFrameTime > 0f)
        {
            SetGpu(PctOfBudget((float)_timings[0].gpuFrameTime), "gpu frame");
            return;
        }

        if (_gpuThread.Valid && _gpuThread.LastValue > 0)
        {
            SetGpu(PctOfBudget(_gpuThread.LastValue / 1_000_000f), "gpu thread");
            return;
        }

        SetGpu(PctOfBudget(Time.unscaledDeltaTime * 1000f), "frame");
    }

    void SampleNpu()
    {
        if (_npuPath != null && TryReadPercent(_npuPath, out float sys))
        {
            SetNpu(sys, Path.GetFileName(_npuPath));
            return;
        }

        if (_seg == null) _seg = FindAnyObjectByType<SemanticOcclusion>();
        if (_seg == null || !_seg.NpuReady || !_seg.Enabled)
        {
            SetNpu(0f, _seg == null ? "no seg" : !_seg.NpuReady ? "not loaded" : "seg off");
            return;
        }

        float infer = _seg.LastInferenceMs;
        if (infer < 0f)
        {
            SetNpu(0f, "waiting");
            return;
        }

        float interval = Mathf.Max(0.001f, _seg.InferIntervalSeconds) * 1000f;
        SetNpu(100f * infer / interval, "duty");
    }

    static float PctOfBudget(float ms)
    {
        float fps = Application.targetFrameRate > 0 ? Application.targetFrameRate : 30f;
        return 100f * ms / (1000f / fps);
    }

    void SetCpu(float raw, string src)
    {
        CpuSource = src;
        CpuPct = SmoothToward(CpuPct, raw);
    }

    void SetGpu(float raw, string src)
    {
        GpuSource = src;
        GpuPct = SmoothToward(GpuPct, raw);
    }

    void SetNpu(float raw, string src)
    {
        NpuSource = src;
        NpuPct = SmoothToward(NpuPct, raw);
    }

    static float SmoothToward(float ema, float raw)
    {
        raw = Mathf.Clamp(raw, 0f, 100f);
        return ema < 0f ? raw : Mathf.Lerp(ema, raw, Smooth);
    }

    static bool TryReadProcStat(out long idle, out long total)
    {
        idle = 0;
        total = 0;
        string text;
        try { text = File.ReadAllText("/proc/stat"); }
        catch { return false; }

        int nl = text.IndexOf('\n');
        string line = nl >= 0 ? text.Substring(0, nl) : text;
        if (!line.StartsWith("cpu", StringComparison.Ordinal)) return false;

        var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;

        long user = ParseLong(parts, 1);
        long nice = ParseLong(parts, 2);
        long system = ParseLong(parts, 3);
        long idleJ = ParseLong(parts, 4);
        long iowait = ParseLong(parts, 5);
        long irq = ParseLong(parts, 6);
        long softirq = ParseLong(parts, 7);
        long steal = ParseLong(parts, 8);

        idle = idleJ + iowait;
        total = user + nice + system + idle + irq + softirq + steal;
        return total > 0;
    }

    static bool TryReadSelfStat(out long ticks)
    {
        ticks = 0;
        string text;
        try { text = File.ReadAllText("/proc/self/stat"); }
        catch { return false; }

        int close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length) return false;

        var parts = text.Substring(close + 1).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        // After comm: state(0) ppid(1) ... utime(11) stime(12)
        if (parts.Length < 13) return false;
        ticks = ParseLong(parts, 11) + ParseLong(parts, 12);
        return ticks > 0;
    }

    static long ParseLong(string[] parts, int i)
    {
        if (i >= parts.Length) return 0;
        return long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
            ? v : 0;
    }

    static string FirstReadable(string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if (TryReadPercent(paths[i], out _))
                return paths[i];
        }
        return null;
    }

    static string FindDevfreq(params string[] needles)
    {
        const string root = "/sys/class/devfreq";
        if (!Directory.Exists(root)) return null;

        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch { return null; }

        for (int i = 0; i < dirs.Length; i++)
        {
            string name = Path.GetFileName(dirs[i]) ?? "";
            bool hit = false;
            for (int n = 0; n < needles.Length; n++)
            {
                if (name.IndexOf(needles[n], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) continue;

            string load = Path.Combine(dirs[i], "load");
            if (TryReadPercent(load, out _)) return load;
            string busy = Path.Combine(dirs[i], "gpu_load");
            if (TryReadPercent(busy, out _)) return busy;
        }
        return null;
    }

    static bool TryReadPercent(string path, out float pct)
    {
        pct = 0f;
        string text;
        try { text = File.ReadAllText(path); }
        catch { return false; }

        if (string.IsNullOrEmpty(text)) return false;

        int i = 0;
        while (i < text.Length && !char.IsDigit(text[i])) i++;
        int j = i;
        while (j < text.Length && (char.IsDigit(text[j]) || text[j] == '.')) j++;
        if (j == i) return false;

        if (!float.TryParse(text.Substring(i, j - i), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float v))
            return false;

        if (v > 100f && v <= 256f) v = v * (100f / 256f);
        if (v < 0f || v > 100f) return false;
        pct = v;
        return true;
    }
}
