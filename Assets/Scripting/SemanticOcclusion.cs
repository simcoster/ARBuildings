using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Semantic occlusion: ARCore depth says which pixels are in front of the model;
/// an NPU segmenter says which of those pixels belong to a countable object.
/// The whole object then occludes, and floor / plaza / sky / building never do.
///
/// The model that actually ran on this phone's ENN (see docs/npu-model-matrix.md)
/// is DeepLab v3 257x257 PASCAL, <c>deeplabv3_257_mv_gpu.tflite</c>: 70/70 nodes
/// on <c>[enn]</c> with CPU disabled, ~57 ms. Unity Inference Engine is not used
/// — it cannot hit the Exynos NPU. ORT NNAPI still rejects the equivalent graphs.
///
/// Fail closed: if ENN rejects the graph, this stays off and the existing
/// <see cref="DepthOcclusion"/> switch is unchanged.
/// </summary>
public class SemanticOcclusion : MonoBehaviour
{
    public const string DefaultModelFile = "dis_isnet_1024.tflite";
    public const string CannyModel = "canny";
    public const string BenchModel = "bench";

    public enum SegBackend
    {
        Cpu,     // XNNPACK
        Gpu,     // NNAPI hybrid (GPU/CPU) — this is NOT the Mali GpuDelegate
        GpuDec,  // LiteRT CompiledModel GPU for DIS; classic GpuDelegate otherwise
        Npu      // ENN, CPU disabled
    }

    [SerializeField] bool enableOnStart;
    [SerializeField] string modelFile = DefaultModelFile;

    [Tooltip("Cycled by the HUD model button. Order: canny, bench, MODNet 256/512, " +
             "U2-Net, Depth Anything 3, IS-Net. Device files, not the APK. `segmodel FILE` " +
             "still loads anything.")]
    [SerializeField] string[] modelFiles =
    {
        CannyModel,
        BenchModel,
        "modnet_256.tflite",
        "modnet_512.tflite",
        "u2net_320_fp16.tflite",
        "depth_anything_3_small_fp16.tflite",
        DefaultModelFile,
    };

    [SerializeField] SegBackend backend = SegBackend.GpuDec;
    [SerializeField] int minVotePixels = 50;
    [SerializeField] float maxOcclusionDistance = 12f;
    [SerializeField] bool debugTint;
    [SerializeField] float inferIntervalSeconds = 0f;

    [Tooltip("Only queue inference every N camera frames. 1 = as soon as the worker is free.")]
    [SerializeField] int inferEveryNFrames = 1;

    [Tooltip("After a successful load, keep the interpreter this many seconds and then " +
             "unload. 0 (default) stays loaded until you tap seg off.")]
    [SerializeField] float holdSeconds = 0f;

    [Tooltip("Occlude the whole axis-aligned box of an accepted object instead of its " +
             "silhouette. A 257x257 mask upsampled to the screen leaves ragged holes in " +
             "anything thin; the box does not.")]
    [SerializeField] bool boundingBox;

    [Tooltip("Input normalisation, applied as (v - mean) / scale. DeepLab float32 wants " +
             "127.5 / 127.5. Wrong values return background everywhere rather than failing.")]
    [SerializeField] float inputMean = 127.5f;
    [SerializeField] float inputScale = 127.5f;

    [Tooltip("Rotate the camera image this many degrees clockwise before inference. The " +
             "CPU image arrives in SENSOR orientation — landscape — while the phone is held " +
             "portrait, so an upright bottle reaches the network lying down and PASCAL has " +
             "no such class. Labels are rotated back before they become a mask, so the " +
             "shader is untouched. A knob rather than a constant because the right value " +
             "is a device fact, and dialling it costs nothing while rebuilding costs minutes.")]
    [SerializeField] int rotationDegrees = 90;

    [Tooltip("Take the largest centred SQUARE of the camera image instead of squashing the " +
             "whole frame into one. Squashing compresses one axis by the frame aspect, which " +
             "costs wide objects — cars, buses — far more than tall ones.")]
    [SerializeField] bool centreCrop = true;

    [Tooltip("Blit ARCore's GPU camera texture (1080p-class) into the model input instead of " +
             "XRCpuImage (640x480 on this phone). LiteRT Java cannot bind a GL texture, so " +
             "this is still a readback — but the photons are the preview feed, not an " +
             "upscaled 480 crop. `segcam cpu` is the escape hatch.")]
    [SerializeField] bool gpuCameraInput = true;

    [Tooltip("RenderTextures are bottom-up; XRCpuImage is top-down. Flip the blit so a GPU " +
             "frame matches the orientation the CPU path used to feed the network.")]
    [SerializeField] bool gpuBlitFlipY = true;

    [Tooltip("Override DIS-ISNet spatial size after compile (LiteRT resize). 0 = baked " +
             "size from the file. Runtime resize of this graph to 512 already failed.")]
    [SerializeField] int inferSide = 0;

    [Tooltip("XNNPACK on the CPU backend. Off falls back to TFLite's built-in kernels, " +
             "which accept graphs XNNPACK refuses to load at all.")]
    [SerializeField] bool useXnnpack = true;

    [Tooltip("How to read a single-channel float output: auto | labels | alpha | depth. " +
             "A matte and a depth map have the same shape and dtype, so auto decides from " +
             "the observed range and says which way it went.")]
    [SerializeField] string outputKind = "auto";

    [Tooltip("Scalar view only. Codes below this stay unpainted, so an empty matte reads " +
             "as empty instead of washing the frame in ramp colour. Depth maps fill the " +
             "range by construction and are barely affected.")]
    [SerializeField] int scalarFloor = 8;

    ARCameraManager _camera;
    AROcclusionManager _occlusion;
    ARCameraBackground _background;
    Material _mat;
    Texture2D _maskTex;
    NpuSegmenterClient _npu = new NpuSegmenterClient();

    byte[] _rgb;
    byte[] _rgbRot;
    byte[] _rgbFit;
    int _fitW, _fitH;
    readonly List<string> _catalogue = new List<string>();
    byte[] _labels;
    byte[] _overlay;
    int[] _parent;
    int[] _votes;
    int[] _counts;
    float[] _minDepth;
    int[] _bx0, _by0, _bx1, _by1;
    bool[] _seen;
    readonly List<int> _roots = new List<int>();
    readonly int[] _hist = new int[256];
    float[] _depthM;
    byte[] _modelBytes;
    string _deviceModelPath;
    bool _reloading;
    bool _unloadAfterBind;
    bool _holdUnloaded;
    float _holdLeft = -1f;
    bool _normOverridden;
    int _depthW, _depthH;
    bool[] _isThing = BuildPascalThing();
    string[] _classNames = PascalNames;

    int _camW, _camH;
    // The mask texture covers the WHOLE GPU camera frame in UV, so the shader can keep
    // sampling it with the same coordinates as the camera texture. The CPU image is often
    // a different aspect (640x480 4:3 vs a 16:9 GPU feed); the square inference result is
    // inset into the GPU-sized canvas, not the CPU one, or the overlay looks squeezed.
    int _maskW, _maskH, _offX, _offY;
    int _gpuW, _gpuH;
    Texture _gpuCamTex;
    RenderTexture _inferRT;
    RenderTexture _camCopyRT;
    AsyncGPUReadbackRequest _gpuReadback;
    bool _gpuReadbackPending;
    bool _wantGpuCapture;
    bool _gpuInputFailed;
    bool _gpuDisplaySpace;
    bool _glFailed;
    string _ackedGlError;
    bool _glAwaitSubmit;
    int _glPackFrame = -1;
    int _eglWaitFrames;
    Material _disBlitMat;
    Material _maskPackMat;
    RenderTexture _inferFloatRT;
    RenderTexture _matteRT;
    RenderTexture _maskRT;
    static IntPtr _glEventFn;
    static bool _glEventFnMissingLogged;
    string _convertNote = "n/a";
    float _inferTimer;
    int _frameSkip;
    float _maskPeriodMs = -1f;
    float _lastMaskAt = -1f;
    const int StageHistCap = 32;
    readonly float[] _fillHist = new float[StageHistCap];
    readonly float[] _runHist = new float[StageHistCap];
    readonly float[] _decodeHist = new float[StageHistCap];
    readonly float[] _periodHist = new float[StageHistCap];
    const int StageN = 15;
    const int SWaitCam = 0, SBlit = 1, SCopy = 2, SUnpack = 3, SConvert = 4,
        SResize = 5, SRotate = 6, SSubmit = 7, SFill = 8, SRun = 9, SDecode = 10,
        SInferWait = 11, SPaint = 12, SUpload = 13, SE2E = 14;
    static readonly string[] StageName =
    {
        "wait-cam", "blit", "copy", "unpack", "convert", "resize", "rotate",
        "submit", "fill", "run", "decode", "infer-wait", "paint", "upload", "e2e"
    };
    readonly float[] _stageLast = new float[StageN];
    readonly float[] _stageHist = new float[StageN * StageHistCap];
    float _tWant = -1f, _tFrame = -1f, _tCopyStart = -1f, _tSubmit = -1f;
    int _stageCount;
    int _stageI;
    string _loadNote = "not loaded";
    int _lastThingPixels;
    int _lastStuffPixels;
    int _lastExpanded;
    int _lastComponents;
    float _mattePackedMetres;

    static readonly int IdMask = Shader.PropertyToID("_SemanticMask");
    static readonly int IdMax = Shader.PropertyToID("_MaxOcclusionDistance");
    static readonly int IdSeg = Shader.PropertyToID("_SegEnabled");
    static readonly int IdDbg = Shader.PropertyToID("_SegDebug");
    static readonly int IdDisplay = Shader.PropertyToID("_UnityDisplayTransform");
    const int GlEventCapture = 1, GlEventPack = 2, GlEventUnpack = 3;

#if UNITY_ANDROID && !UNITY_EDITOR
    [DllImport("npu_gl")]
    static extern IntPtr npu_gl_event_fn();
#endif

    public bool Enabled
    {
        get => enableOnStart;
        set
        {
            enableOnStart = value;
            debugTint = value;
            ApplyMaterialFlags();
            if (!value)
            {
                _holdLeft = -1f;
                if (_reloading) _unloadAfterBind = true;
                else StartCoroutine(UnloadModel());
                return;
            }
            _unloadAfterBind = false;
            _holdUnloaded = false;
            if (_npu.Ready)
            {
                ArmHoldTimer();
                SubmitFrame();
            }
            else if (!_reloading) StartCoroutine(LoadModel());
        }
    }

    public int MinVotePixels
    {
        get => minVotePixels;
        set => minVotePixels = Mathf.Max(1, value);
    }

    public float MaxOcclusionDistance
    {
        get => maxOcclusionDistance;
        set
        {
            maxOcclusionDistance = value;
            ApplyMaterialFlags();
        }
    }

    public bool DebugTint
    {
        get => debugTint;
        set
        {
            debugTint = value;
            ApplyMaterialFlags();
        }
    }

    public bool BoundingBox
    {
        get => boundingBox;
        set => boundingBox = value;
    }

    public int RotationDegrees
    {
        get => ((rotationDegrees % 360) + 360) % 360 / 90 * 90;
        set => rotationDegrees = ((Mathf.RoundToInt(value / 90f) * 90) % 360 + 360) % 360;
    }

    public bool CentreCrop
    {
        get => centreCrop;
        set
        {
            centreCrop = value;
            _maskW = _maskH = 0;  // force the mask geometry to be rebuilt
        }
    }

    public bool GpuCameraInput => gpuCameraInput && !_gpuInputFailed;
    public bool UseGlCameraPath => UseGlPath();

    public string SetGpuCameraInput(bool on)
    {
        gpuCameraInput = on;
        _gpuInputFailed = false;
        _glFailed = false;
        _ackedGlError = null;
        _wantGpuCapture = false;
        _glAwaitSubmit = false;
        _eglWaitFrames = 0;
        _maskW = _maskH = 0;
        return gpuCameraInput
            ? (UseGlPath()
                ? "segcam gpu — gl-zero-copy"
                : "segcam gpu — blit ARCore camera texture, async readback")
            : "segcam cpu — XRCpuImage";
    }

    public bool GpuBlitFlipY
    {
        get => gpuBlitFlipY;
        set => gpuBlitFlipY = value;
    }

    public int InferSide => inferSide;

    public string SetInferSide(int side)
    {
        if (side != 0 && (side < 64 || side > 2048))
            return $"ERROR segsize {side} — use 0 (baked) or 64..2048";
        inferSide = side;
        _npu.InferSide = IsDis ? inferSide : 0;
        _maskW = _maskH = 0;
        if (!enableOnStart)
            return inferSide == 0
                ? "segsize baked (from filename)"
                : $"segsize {inferSide} (applies on next load)";
        if (IsCanny || IsBench)
            return $"segsize {inferSide} (ignored by {modelFile})";
        if (!_reloading) StartCoroutine(LoadModel());
        return $"segsize {inferSide} — reloading";
    }

    public string SetNormalization(float mean, float scale)
    {
        inputMean = mean;
        inputScale = scale == 0f ? 1f : scale;
        _normOverridden = true;
        _npu.SetNormalization(inputMean, inputScale);
        return $"segnorm {_npu.Normalization}";
    }

    /// <summary>
    /// What each family was trained on. Wrong normalisation does not fail — it returns a
    /// flat or washed-out map, which is indistinguishable from a model that sees nothing,
    /// and that ambiguity already cost a session once. So the model carries its own value
    /// instead of inheriting whatever the last one used.
    /// </summary>
    void ApplyModelDefaults()
    {
        if (!_normOverridden)
        {
            string f = (modelFile ?? string.Empty).ToLowerInvariant();
            // A single scalar stands in for the per-channel mean and standard deviation.
            // That tilts the colour balance a little and changes no structure.
            if (f.Contains("modnet") || f.Contains("deeplab") || f.Contains("coral"))
            {
                inputMean = 127.5f;   // [-1, 1]
                inputScale = 127.5f;
            }
            else if (f.Contains("isnet") || f.Contains("dis"))
            {
                inputMean = 127.5f;   // [0, 1] centred on 0.5
                inputScale = 255f;
            }
            else if (f == CannyModel)
            {
                inputMean = 0f;
                inputScale = 1f;
                outputKind = "alpha";
            }
            else if (f == BenchModel)
            {
                inputMean = 0f;
                inputScale = 1f;
                outputKind = "alpha";
            }
            else if (IsToySeg(f))
            {
                inputMean = 123.7f;
                inputScale = 58.4f;
                outputKind = "alpha";
            }
            else if (f.Contains("depth") || f.Contains("midas") || f.Contains("da3"))
            {
                inputMean = 123.7f;
                inputScale = 58.4f;
                outputKind = "depth";
            }
            else
            {
                inputMean = 123.7f;   // ImageNet — u2net, cityscapes pidnet
                inputScale = 58.4f;
            }
        }
        _npu.SetNormalization(inputMean, inputScale);
        _npu.SetOutputKind(outputKind);
    }

    /// <summary>
    /// Codes below this stay unpainted. Doubles as a threshold: IS-Net's sigmoid comes back
    /// compressed into roughly 0.50..0.73, so at the default floor every pixel qualifies and
    /// the frame washes out even though the peak sits squarely on the object.
    /// </summary>
    public string SetScalarFloor(int floor)
    {
        scalarFloor = Mathf.Clamp(floor, 0, 255);
        return $"segfloor {scalarFloor} of 255";
    }

    /// <summary>auto | labels | alpha | depth — how to read a single-channel float output.</summary>
    public string SetOutputKind(string kind)
    {
        outputKind = string.IsNullOrWhiteSpace(kind) ? "auto" : kind.Trim().ToLowerInvariant();
        _npu.SetOutputKind(outputKind);
        return $"segkind {_npu.OutputKind}";
    }

    public bool NpuReady => _npu.Ready;
    public bool UsingNpu => backend == SegBackend.Npu && _npu.Ready;
    public float LastInferenceMs => _npu.LastInferenceMs;
    public float LastFillMs => _npu.LastFillMs;
    public float LastRunMs => _npu.LastRunMs;
    public float LastDecodeMs => _npu.LastDecodeMs;
    public float InferIntervalSeconds => inferIntervalSeconds;
    public int InferEveryNFrames => inferEveryNFrames;
    public float HoldSeconds => holdSeconds;
    public float HoldLeft => _holdLeft;
    public float MaskPeriodMs => _maskPeriodMs;
    public SegBackend Backend => backend;

    public string SetInferInterval(float seconds)
    {
        inferIntervalSeconds = Mathf.Max(0f, seconds);
        return $"segint {inferIntervalSeconds:F3} s" +
               (inferIntervalSeconds <= 0f ? " (submit as soon as the worker is free)" : "");
    }

    public string SetInferEveryNFrames(int n)
    {
        inferEveryNFrames = Mathf.Max(1, n);
        return $"segn every {inferEveryNFrames} frame(s)";
    }

    public string SetHoldSeconds(float seconds)
    {
        holdSeconds = Mathf.Max(0f, seconds);
        if (holdSeconds <= 0f)
        {
            _holdLeft = -1f;
            return "segtimer off (stays loaded until seg off)";
        }
        if (_npu.Ready && enableOnStart) _holdLeft = holdSeconds;
        return $"segtimer {holdSeconds:F0}s then unload";
    }

    bool IsCanny =>
        string.Equals(modelFile, CannyModel, StringComparison.OrdinalIgnoreCase);

    bool IsBench =>
        string.Equals(modelFile, BenchModel, StringComparison.OrdinalIgnoreCase);

    bool IsDis =>
        !string.IsNullOrEmpty(modelFile) &&
        (modelFile.IndexOf("isnet", StringComparison.OrdinalIgnoreCase) >= 0
         || modelFile.IndexOf("dis", StringComparison.OrdinalIgnoreCase) >= 0);

    public string SetBackend(SegBackend next)
    {
        backend = next;
        if (!IsCanny && !IsBench && _modelBytes == null && string.IsNullOrEmpty(_deviceModelPath))
            return $"seg backend {LabelOf(backend)} (model not loaded yet)";
        if (_reloading)
            return $"seg backend {LabelOf(backend)} (reload already running)";
        StartCoroutine(ReloadBackend());
        return $"seg backend {LabelOf(backend)} — reloading";
    }

    public string CycleBackend()
    {
        var next = backend == SegBackend.Cpu ? SegBackend.Gpu
                 : backend == SegBackend.Gpu ? SegBackend.Npu
                 : SegBackend.Cpu;
        return SetBackend(next);
    }

    public static string LabelOf(SegBackend b) =>
        b == SegBackend.Cpu ? "CPU"
        : b == SegBackend.Gpu ? "GPU"
        : b == SegBackend.GpuDec ? "LITERT"
        : "NPU";

    string BackendArg(SegBackend b)
    {
        if (b == SegBackend.Cpu)
        {
            // 1024² NCHW graphs native-crash inside XNNPACK on this phone (SIGSEGV in
            // libtensorflowlite_jni, tid NpuSegmenter) instead of throwing. Built-in
            // kernels are slower and they stay in Java.
            return useXnnpack && !NeedsBuiltinKernels(modelFile) ? "cpu" : "cpuref";
        }
        if (b == SegBackend.Gpu) return "gpu";
        if (b == SegBackend.GpuDec) return "gpudec";
        return "npu";
    }

    static bool NeedsBuiltinKernels(string file)
    {
        if (string.IsNullOrEmpty(file)) return false;
        string f = file.ToLowerInvariant();
        return f.Contains("1024") || f.Contains("isnet");
    }

    public bool UseXnnpack
    {
        get => useXnnpack;
        set
        {
            useXnnpack = value;
            if (!IsCanny && !IsBench && (_modelBytes != null || !string.IsNullOrEmpty(_deviceModelPath)) && !_reloading)
                StartCoroutine(ReloadBackend());
        }
    }

    /// <summary>
    /// Swaps the model at runtime. Comparing segmenters is otherwise a rebuild each, and
    /// the whole point of reading the device copy first is that a model is a file push.
    /// </summary>
    public string SetModel(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return $"segmodel {modelFile}";
        modelFile = file.Trim();
        backend = PreferredBackend(modelFile);
        // A segnorm override belongs to the model it was typed for, not to every model after.
        _normOverridden = false;
        _maskW = _maskH = 0;
        _glFailed = false;
        _ackedGlError = null;
        _glAwaitSubmit = false;
        _eglWaitFrames = 0;
        if (!enableOnStart)
        {
            _loadNote = $"idle {modelFile} — tap seg to load";
            return $"segmodel {modelFile} (not loaded until seg on)";
        }
        _loadNote = $"segmodel {modelFile} — loading";
        if (!_reloading) StartCoroutine(LoadModel());
        else
            _loadNote = $"segmodel {modelFile} — queued";
        return _loadNote;
    }

    /// <summary>
    /// HUD cycle. Built-ins first, then the mattes/depth/IS-Net already on the phone.
    /// Leftover toy nets in persistentDataPath stay off the button; `segmodel FILE` still
    /// loads them.
    /// </summary>
    List<string> Catalogue()
    {
        _catalogue.Clear();
        if (modelFiles != null)
            foreach (var m in modelFiles)
                if (!string.IsNullOrWhiteSpace(m) && !_catalogue.Contains(m))
                    _catalogue.Add(m.Trim());
        if (_catalogue.Count == 0)
        {
            _catalogue.Add(CannyModel);
            _catalogue.Add(BenchModel);
            _catalogue.Add("modnet_256.tflite");
            _catalogue.Add("modnet_512.tflite");
            _catalogue.Add("u2net_320_fp16.tflite");
            _catalogue.Add("depth_anything_3_small_fp16.tflite");
            _catalogue.Add(DefaultModelFile);
        }
        if (!string.IsNullOrEmpty(modelFile) && IndexInCatalogue(_catalogue, modelFile) < 0)
            _catalogue.Insert(0, modelFile);
        return _catalogue;
    }

    public string CycleModel()
    {
        var all = Catalogue();
        if (all.Count == 0) return "seg: no models";
        if (all.Count == 1) return $"segmodel {modelFile}";
        int i = IndexInCatalogue(all, modelFile);
        return SetModel(all[(i + 1) % all.Count]);
    }

    /// <summary>
    /// Fits a HUD button. Short name, not the .tflite path — otherwise canny is in the
    /// cycle and nobody can tell, because the file names never fitted anyway.
    /// </summary>
    public string ModelLabel
    {
        get
        {
            var all = Catalogue();
            int i = IndexInCatalogue(all, modelFile);
            string res = _npu.Ready ? $"{_npu.InputWidth}" : "--";
            return $"{i + 1}/{all.Count} {ShortModelName(modelFile)} {res}";
        }
    }

    static int IndexInCatalogue(List<string> all, string file)
    {
        for (int i = 0; i < all.Count; i++)
            if (string.Equals(all[i], file, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    static string ShortModelName(string file)
    {
        if (string.IsNullOrEmpty(file)) return "?";
        string f = file.ToLowerInvariant();
        if (f == CannyModel) return "canny";
        if (f == BenchModel) return "bench";
        if (f.Contains("modnet") && f.Contains("256")) return "m256";
        if (f.Contains("modnet")) return "m512";
        if (f.Contains("u2net")) return "u2net";
        if (f.Contains("depth") || f.Contains("da3") || f.Contains("midas") || f.Contains("dpt"))
            return "da3";
        if (f.Contains("coral") || f.Contains("deeplab")) return "deeplab";
        if (f.Contains("isnet") || f.Contains("dis")) return "isnet";
        string n = Path.GetFileNameWithoutExtension(file);
        return n.Length <= 12 ? n : n.Substring(0, 12);
    }

    public string ListModels()
    {
        var all = Catalogue();
        var r = new StringBuilder($"live: {modelFile} | {all.Count} available:");
        for (int i = 0; i < all.Count; i++)
            r.Append($" [{i + 1}]{all[i]}");
        return r.ToString();
    }

    /// <summary>
    /// The scalar equivalent of <see cref="TopClasses"/>. The raw range is the load-bearing
    /// number: it is the only thing that tells a 0..1 matte from a scaleless depth map, and
    /// therefore whether the ramp on screen is absolute or stretched.
    /// </summary>
    string ScalarSummary()
    {
        int n = 0;
        for (int c = 0; c < _hist.Length; c++) n += _hist[c];
        if (n == 0) return "scalar: nothing yet";

        int half = n / 2;
        int acc = 0, median = 0;
        for (int c = 0; c < _hist.Length; c++)
        {
            acc += _hist[c];
            if (acc >= half) { median = c; break; }
        }
        float painted = 100f * _lastThingPixels / n;
        string occlude = ScalarOccludes()
            ? (_mattePackedMetres > 0f ? $"occlude {_mattePackedMetres:F1} m" : "occlude (empty)")
            : "view only";
        return $"raw {_npu.ScalarRange} [{_npu.OutputKind}]\n" +
               $"  painted {painted:F1}% over {scalarFloor} | median code {median} | {occlude}";
    }

    public string HudReadout
    {
        get
        {
            string shape = "mask";
            if (_npu.ScalarOutput) shape = ScalarOccludes() ? "matte" : "VIEW";
            else if (_npu.OutputChannels == 19) shape = "VIEW";
            else if (boundingBox) shape = "BOX";
            return
                $"seg: {(enableOnStart ? "ON" : "OFF")} {LabelOf(backend)} " +
                $"{shape} overlay {(debugTint ? "ON" : "off")} " +
                $"{_npu.Ep}" +
                (holdSeconds <= 0f ? " hold off"
                    : _holdLeft >= 0f ? $" hold {_holdLeft:F0}s"
                    : "") + "\n" +
                $"  e2e {_stageLast[SE2E]:F0} p50 {StageP50Val(SE2E)}  " +
                $"copy {_stageLast[SCopy]:F0} unpack {_stageLast[SUnpack]:F0} " +
                $"paint {_stageLast[SPaint]:F0} upload {_stageLast[SUpload]:F0}\n" +
                $"  wait-cam {_stageLast[SWaitCam]:F0} blit {_stageLast[SBlit]:F1} " +
                $"rotate {_stageLast[SRotate]:F1} resize {_stageLast[SResize]:F1} " +
                $"convert {_stageLast[SConvert]:F0} submit {_stageLast[SSubmit]:F1}\n" +
                $"  fill {_npu.LastFillMs:F0} run {_npu.LastRunMs:F0} dec {_npu.LastDecodeMs:F0} " +
                $"infer-wait {_stageLast[SInferWait]:F0} " +
                $"per {(_maskPeriodMs < 0f ? "—" : $"{_maskPeriodMs:F0}ms")} " +
                $"{(UseGpuCameraInput() ? "gpu-cam" : "cpu-img")} " +
                $"every {inferEveryNFrames}f / {inferIntervalSeconds:F1}s\n" +
                $"  {modelFile}\n" +
                $"  {_loadNote}\n" +
                $"  {TopClasses(4)}";
        }
    }

    /// <summary>
    /// The pixel count per predicted class, biggest first. This is the whole answer to
    /// "does the model even see it" — a screenshot of the tint cannot distinguish
    /// "predicted nothing" from "predicted it and the mask never reached the shader".
    /// </summary>
    public string TopClasses(int take)
    {
        if (_npu.ScalarOutput) return ScalarSummary();

        var used = new List<int>();
        for (int c = 0; c < _hist.Length; c++)
            if (_hist[c] > 0) used.Add(c);
        if (used.Count == 0) return "classes: none yet";

        used.Sort((a, b) => _hist[b].CompareTo(_hist[a]));
        var r = new StringBuilder("classes:");
        for (int i = 0; i < used.Count && i < take; i++)
            r.Append($" {NameOf(used[i])}={_hist[used[i]]}");
        return r.ToString();
    }

    string NameOf(int cls) =>
        cls >= 0 && cls < _classNames.Length ? _classNames[cls] : $"#{cls}";

    /// <summary>
    /// How much the whole camera frame is compressed horizontally to reach a square
    /// tensor. Below 1 means wide objects reach the network narrower than they are, which
    /// costs cars and buses far more than it costs people.
    /// </summary>
    public float HorizontalSquash =>
        _camW > 0 && _camH > 0 && _npu.InputHeight > 0
            ? (float)_npu.InputWidth * _camH / (_npu.InputHeight * (float)_camW)
            : 0f;

    /// <summary>
    /// Writes the exact RGB the network was handed, plus the mask it produced, as PNGs.
    /// "The model does not see cars" and "the model is handed something a car cannot be
    /// recognised in" look identical from the label counts, and only the input itself
    /// tells squash, colour order, orientation and exposure apart.
    /// </summary>
    public string DumpInput()
    {
        if (_rgb == null || _npu.InputWidth <= 0) return "seg: no input captured yet";

        string dir = Path.Combine(Application.persistentDataPath, "captures");
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        int w = _npu.InputWidth, h = _npu.InputHeight;

        // Dump what the network was ACTUALLY handed, i.e. after rotation, not the raw crop.
        byte[] seen = RotationDegrees != 0 && _rgbRot != null ? _rgbRot : _rgb;

        // Unity textures start at the BOTTOM row, the converted image at the top. Writing
        // it straight through flips the dump vertically, which is a poor way to inspect a
        // picture you are trying to judge the orientation of.
        var flipped = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            Array.Copy(seen, y * w * 3, flipped, (h - 1 - y) * w * 3, w * 3);

        string note = Save(dir, $"seg_input_{stamp}.png", w, h, TextureFormat.RGB24, flipped);

        if (_overlay != null && _maskW > 0)
        {
            var flippedMask = new byte[_maskW * _maskH * 4];
            for (int y = 0; y < _maskH; y++)
                Array.Copy(_overlay, y * _maskW * 4, flippedMask,
                           (_maskH - 1 - y) * _maskW * 4, _maskW * 4);
            note += " | " + Save(dir, $"seg_mask_{stamp}.png", _maskW, _maskH,
                                 TextureFormat.RGBA32, flippedMask);
        }

        return $"{note} | camera {_camW}x{_camH} rot {RotationDegrees} " +
               $"{(centreCrop ? "square crop" : $"squash x{HorizontalSquash:F2}")} | {TopClasses(6)}";
    }

    static string Save(string dir, string name, int w, int h, TextureFormat fmt, byte[] data)
    {
        var tex = new Texture2D(w, h, fmt, false);
        try
        {
            tex.LoadRawTextureData(data);
            tex.Apply(false, false);
            File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
            return name;
        }
        catch (Exception e)
        {
            return $"{name} FAILED {e.Message}";
        }
        finally
        {
            Destroy(tex);
        }
    }

    public string StateReport
    {
        get
        {
            var r = new StringBuilder();
            r.AppendLine($"seg occlusion      : {(enableOnStart ? "ON" : "OFF")}");
            r.AppendLine($"seg backend        : {LabelOf(backend)} ({BackendArg(backend)})");
            r.AppendLine($"seg model          : {modelFile}");
            r.AppendLine($"seg EP             : {_npu.Ep}");
            r.AppendLine($"seg ready          : {_npu.Ready}");
            r.AppendLine($"seg load           : {_loadNote}");
            r.AppendLine($"seg last error     : {_npu.LastError}");
            r.AppendLine($"seg interval       : every {inferEveryNFrames} frames, min {inferIntervalSeconds:F3} s");
            r.AppendLine($"seg hold           : " +
                         (holdSeconds <= 0f ? "off"
                             : _holdLeft >= 0f ? $"{holdSeconds:F0}s ({_holdLeft:F0}s left)"
                             : $"{holdSeconds:F0}s"));
            r.AppendLine($"seg inference      : {_npu.LastInferenceMs:F2} ms (fill+run+decode)");
            r.AppendLine($"seg fill           : {_npu.LastFillMs:F2} ms" + StageP50("fill", _fillHist));
            r.AppendLine($"seg run            : {_npu.LastRunMs:F2} ms" + StageP50("run", _runHist));
            r.AppendLine($"seg decode         : {_npu.LastDecodeMs:F2} ms" + StageP50("decode", _decodeHist));
            for (int i = 0; i < StageN; i++)
                r.AppendLine($"seg {StageName[i],-12} : {_stageLast[i]:F2} ms" + StageP50Idx(i));
            r.AppendLine($"seg mask period    : " +
                         (_maskPeriodMs < 0f ? "n/a" : $"{_maskPeriodMs:F0} ms") +
                         StageP50("period", _periodHist));
            r.AppendLine($"seg input          : {_npu.InputWidth}x{_npu.InputHeight}" +
                         (inferSide > 0 ? $" (segsize {inferSide})" : " (baked)"));
            r.AppendLine($"seg convert        : {_convertNote}");
            r.AppendLine($"seg camera source  : " +
                         (!gpuCameraInput ? "cpu XRCpuImage"
                             : _gpuInputFailed ? "gpu FAILED, cpu fallback"
                             : _glFailed ? "gl FAILED, gpu-blit readback"
                             : IsCanny ? "cpu (canny)"
                             : UseGlPath() ? "gl-zero-copy"
                             : "gpu blit + async readback") +
                         (gpuBlitFlipY ? ", flipY" : ""));
            r.AppendLine($"seg egl            : " +
                         (_npu.EglReady ? "share context ready"
                             : UseGlPath() && !IsBench
                                 ? $"waiting ({_eglWaitFrames} frames) {_npu.LastError}"
                                 : "n/a"));
            r.AppendLine($"seg camera image   : last {_camW}x{_camH}, gpu tex {_gpuW}x{_gpuH} " +
                         (centreCrop ? "-> centred square, no squash"
                                     : $"-> whole frame squashed x{HorizontalSquash:F2} horizontally"));
            r.AppendLine($"seg rotation       : {RotationDegrees} deg clockwise before inference");
            r.AppendLine($"seg mask texture   : {_maskW}x{_maskH} covering the frame, inset +{_offX},+{_offY}");
            r.AppendLine($"seg labels         : {_npu.OutputWidth}x{_npu.OutputHeight} c={_npu.OutputChannels}");
            r.AppendLine($"seg output tensor  : {_npu.OutputSpec}");
            r.AppendLine($"seg output kind    : {_npu.OutputKind}" +
                         (_npu.ScalarOutput
                             ? (ScalarOccludes()
                                 ? $" — matte occludes. raw {_npu.ScalarRange}"
                                 : $" — VIEW ONLY, no occlusion. raw {_npu.ScalarRange}")
                             : _npu.OutputChannels == 19
                                 ? " — VIEW ONLY (Cityscapes, no occlusion)"
                                 : ""));
            r.AppendLine($"seg normalisation  : {_npu.Normalization}");
            r.AppendLine($"segmin             : {minVotePixels} px");
            r.AppendLine($"seg max distance   : {maxOcclusionDistance:F1} m");
            r.AppendLine($"seg occluder shape : {(boundingBox ? "BOUNDING BOX" : "silhouette mask")}");
            r.AppendLine($"seg debug          : {(debugTint ? "ON" : "OFF")}");
            r.AppendLine($"seg {TopClasses(8)}");
            r.AppendLine($"seg thing/stuff    : {_lastThingPixels} / {_lastStuffPixels} px");
            r.AppendLine($"seg components     : {_lastComponents} expanded {_lastExpanded}");
            r.AppendLine($"seg mask           : {(_maskTex != null ? $"{_maskTex.width}x{_maskTex.height}" : "none")}");
            return r.ToString();
        }
    }

    void Awake()
    {
        if (_camera == null) _camera = FindAnyObjectByType<ARCameraManager>();
        if (_occlusion == null) _occlusion = FindAnyObjectByType<AROcclusionManager>();
        if (_background == null) _background = FindAnyObjectByType<ARCameraBackground>();
        // Scene-serialized modelFiles / modelFile win over C# defaults.
        modelFiles = new[]
        {
            CannyModel,
            BenchModel,
            "modnet_256.tflite",
            "modnet_512.tflite",
            "u2net_320_fp16.tflite",
            "depth_anything_3_small_fp16.tflite",
            DefaultModelFile,
        };
        if (IsRetired(modelFile) || IndexInCatalogue(new List<string>(modelFiles), modelFile) < 0)
            modelFile = BenchModel;
        backend = PreferredBackend(modelFile);
        enableOnStart = false;
        holdSeconds = 0f;
        _holdLeft = -1f;
        inferEveryNFrames = 1;
        inferIntervalSeconds = 0f;
        inferSide = 0;
        _loadNote = $"idle {modelFile} — tap seg to load";
    }

    void Start()
    {
        var src = Resources.Load<Material>("ARCoreBackgroundMasked");
        if (src == null)
        {
            _loadNote = "ARCoreBackgroundMasked.mat missing from Resources — shader would strip";
            Debug.LogError("[Seg] " + _loadNote);
            return;
        }

        _mat = Instantiate(src);
        var dis = Resources.Load<Shader>("SegDisBlit");
        var pack = Resources.Load<Shader>("SegMaskPack");
        if (dis != null) _disBlitMat = new Material(dis);
        if (pack != null) _maskPackMat = new Material(pack);
        if (_disBlitMat == null || _maskPackMat == null)
            Debug.LogWarning("[Seg] SegDisBlit/SegMaskPack missing from Resources — GL path off");
        if (_background != null)
        {
            _background.customMaterial = _mat;
            _background.useCustomMaterial = true;
        }

        ApplyMaterialFlags();
        if (_camera != null)
            _camera.frameReceived += OnCameraFrame;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        // Do not LoadModel here. DIS-ISNet is 176 MB; GpuDelegate OpenCL compile of it
        // hung the S24 (black screen, no HUD, system UI crawling). Tap seg to load.
    }

    void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        if (IsBench || _npu.EglReady || !_npu.GlPathReady) return;
        IssueGlEvent(GlEventCapture);
    }

    void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        if (_camera != null)
            _camera.frameReceived -= OnCameraFrame;
        _wantGpuCapture = false;
        _gpuReadbackPending = false;
        ReleaseInferRT();
        ReleaseGlRTs();
        _npu.Dispose();
        if (_maskTex != null) Destroy(_maskTex);
        if (_mat != null) Destroy(_mat);
        if (_disBlitMat != null) Destroy(_disBlitMat);
        if (_maskPackMat != null) Destroy(_maskPackMat);
    }

    void Update()
    {
        if (_holdLeft >= 0f && !_reloading && _npu.Ready)
        {
            _holdLeft -= Time.deltaTime;
            if (_holdLeft <= 0f)
            {
                _holdLeft = -1f;
                _holdUnloaded = true;
                enableOnStart = false;
                debugTint = false;
                ApplyMaterialFlags();
                StartCoroutine(UnloadModel());
            }
        }

        if (!enableOnStart || !_npu.Ready || _reloading) return;

        CollectGlResult();
        CollectResult();
        TryFinishGpuReadback();
        TrySubmitGl();
        WatchGlFailure();

        _frameSkip++;
        if (_frameSkip < inferEveryNFrames) return;
        _inferTimer += Time.deltaTime;
        if (_inferTimer < inferIntervalSeconds) return;
        if (_npu.Busy || _gpuReadbackPending || _wantGpuCapture || _glAwaitSubmit) return;
        _frameSkip = 0;
        _inferTimer = 0f;
        SubmitFrame();
    }

    IEnumerator LoadModel()
    {
        _reloading = true;
        // Closing the interpreter while the worker is inside interpreter.run is a native
        // SIGSEGV. Wait until the current job finishes, then tear it down. HUD cycle
        // used to start a second coroutine; now it only retargets modelFile and this
        // loop picks it up, so OpenCL is never destroyed under a live Run.
        yield return null;
        while (true)
        {
            string want = modelFile;
            _loadNote = $"loading {want} off-thread";
            while (_npu.Busy) yield return null;
            if (modelFile != want) continue;

            string devicePath = Path.Combine(Application.persistentDataPath, want);
            _deviceModelPath = null;
            _modelBytes = null;

            if (IsCanny)
            {
                _loadNote = "canny 480² CPU (no tflite)";
                yield return BindInterpreter();
            }
            else if (IsBench)
            {
                _loadNote = "bench luma 1024² (1-layer stand-in, no tflite)";
                yield return BindInterpreter();
            }
            else if (File.Exists(devicePath))
            {
                _deviceModelPath = devicePath;
                _loadNote = $"device {want} {new FileInfo(devicePath).Length} bytes (mmap)";
                yield return BindInterpreter();
            }
            else
            {
                string path = $"{Application.streamingAssetsPath}/{want}";
                string url = path.Contains("://") ? path : $"file://{path}";
                using var req = UnityWebRequest.Get(url);
                yield return req.SendWebRequest();
                if (modelFile != want) continue;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _loadNote = $"could not read {url}: {req.error}";
                    Debug.LogWarning("[Seg] " + _loadNote);
                    _reloading = false;
                    yield break;
                }
                _modelBytes = req.downloadHandler.data;
                _loadNote = $"apk {want} {_modelBytes.Length} bytes";
                yield return BindInterpreter();
            }

            if (modelFile != want)
            {
                _reloading = true;
                continue;
            }
            yield break;
        }
    }

    IEnumerator ReloadBackend()
    {
        _reloading = true;
        while (_npu.Busy) yield return null;
        yield return BindInterpreter();
        if (enableOnStart && _npu.Ready && !_unloadAfterBind) SubmitFrame();
    }

    bool TryLoad(string backendArg)
    {
        _npu.InferSide = IsDis ? inferSide : 0;
        if (IsCanny) return _npu.LoadCanny();
        if (IsBench) return _npu.LoadBench();
        if (!string.IsNullOrEmpty(_deviceModelPath))
            return _npu.LoadFile(_deviceModelPath, backendArg);
        return _npu.Load(_modelBytes, backendArg);
    }

    IEnumerator BindInterpreter()
    {
        _reloading = true;
        bool ok = false;
        string refused = null;
        bool tryFallback = backend != SegBackend.Cpu;
        string firstArg = BackendArg(backend);
        SegBackend used = backend;

        yield return RunOffThread(() =>
        {
            ok = TryLoad(firstArg);
            if (!ok && tryFallback)
            {
                refused = _npu.LastError;
                used = SegBackend.Cpu;
                ok = TryLoad(BackendArg(SegBackend.Cpu));
            }
        });

        if (ok && refused != null)
            backend = SegBackend.Cpu;

        if (ok && (IsCanny || IsBench))
        {
            FinishBind($"CPU {_npu.Ep} {_npu.InputWidth}x{_npu.InputHeight} (no tflite)");
            yield break;
        }

        if (ok && refused != null)
        {
            FinishBind($"CPU {_npu.Ep} {_npu.InputWidth}x{_npu.InputHeight} " +
                       $"(fell back, NPU/GPU refused: {refused})");
            yield break;
        }

        if (!ok)
        {
            _loadNote = $"{LabelOf(backend)} REJECT {_npu.LastError}";
            ApplyMaterialFlags();
            Debug.LogWarning($"[Seg] {LabelOf(backend)} refused the graph. {_npu.LastError}");
            _reloading = false;
            yield break;
        }

        FinishBind($"{LabelOf(used)} {_npu.Ep} {_npu.InputWidth}x{_npu.InputHeight} -> {_npu.OutputWidth}x{_npu.OutputHeight}");
        yield return null;
    }

    IEnumerator UnloadModel()
    {
        _reloading = true;
        enableOnStart = false;
        debugTint = false;
        ApplyMaterialFlags();
        while (_npu.Busy) yield return null;
        _wantGpuCapture = false;
        _gpuReadbackPending = false;
        yield return RunOffThread(() => _npu.Dispose());
        _holdLeft = -1f;
        _reloading = false;
        _loadNote = _holdUnloaded
            ? $"unloaded after {holdSeconds:F0}s — tap seg to load"
            : $"idle {modelFile} — tap seg to load";
        _holdUnloaded = false;
        ApplyMaterialFlags();
    }

    IEnumerator RunOffThread(Action work)
    {
        var done = new ManualResetEventSlim(false);
        Exception err = null;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AndroidJNI.AttachCurrentThread();
#endif
                work();
            }
            catch (Exception e)
            {
                err = e;
            }
            finally
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try { AndroidJNI.DetachCurrentThread(); } catch { /* already detached */ }
#endif
                done.Set();
            }
        });
        while (!done.IsSet) yield return null;
        done.Dispose();
        if (err != null)
            Debug.LogWarning("[Seg] off-thread: " + err.Message);
    }

    void ArmHoldTimer()
    {
        _holdLeft = holdSeconds > 0f ? holdSeconds : -1f;
    }

    void FinishBind(string note)
    {
        ApplyModelDefaults();
        Allocate(_npu.OutputWidth, _npu.OutputHeight);
        ConfigureThingTable(_npu.OutputChannels);
        _loadNote = note;
        if (_npu.GlPathReady)
            _loadNote += " gl-zero-copy";
        Debug.Log($"[Seg] loaded: {_loadNote}");
        ApplyMaterialFlags();
        _reloading = false;
        _stageCount = 0;
        _stageI = 0;
        _maskPeriodMs = -1f;
        _lastMaskAt = -1f;
        _eglWaitFrames = 0;
        _glAwaitSubmit = false;
        if (_unloadAfterBind || !enableOnStart)
        {
            _unloadAfterBind = false;
            StartCoroutine(UnloadModel());
            return;
        }
        if (_npu.GlPathReady && !IsBench)
            StartCoroutine(PumpEglCapture());
        ArmHoldTimer();
        if (enableOnStart && _npu.Ready) SubmitFrame();
    }

    void RecordStages()
    {
        _stageLast[SFill] = _npu.LastFillMs;
        _stageLast[SRun] = _npu.LastRunMs;
        _stageLast[SDecode] = _npu.LastDecodeMs;
        _fillHist[_stageI] = _npu.LastFillMs;
        _runHist[_stageI] = _npu.LastRunMs;
        _decodeHist[_stageI] = _npu.LastDecodeMs;
        _periodHist[_stageI] = _maskPeriodMs;
        for (int s = 0; s < StageN; s++)
            _stageHist[s * StageHistCap + _stageI] = _stageLast[s];
        _stageI = (_stageI + 1) % StageHistCap;
        if (_stageCount < StageHistCap) _stageCount++;
    }

    string StageP50(string _, float[] hist)
    {
        if (_stageCount < 3) return "";
        return $"  p50 {Percentile(hist, _stageCount, 0.5f):F1} ms n={_stageCount}";
    }

    string StageP50Idx(int id)
    {
        if (_stageCount < 3) return "";
        var tmp = new float[_stageCount];
        int off = id * StageHistCap;
        for (int i = 0; i < _stageCount; i++) tmp[i] = _stageHist[off + i];
        Array.Sort(tmp);
        int i50 = Mathf.Clamp(Mathf.RoundToInt((_stageCount - 1) * 0.5f), 0, _stageCount - 1);
        return $"  p50 {tmp[i50]:F1} ms n={_stageCount}";
    }

    string StageP50Val(int id)
    {
        if (_stageCount < 3) return "—";
        var tmp = new float[_stageCount];
        int off = id * StageHistCap;
        for (int i = 0; i < _stageCount; i++) tmp[i] = _stageHist[off + i];
        Array.Sort(tmp);
        int i50 = Mathf.Clamp(Mathf.RoundToInt((_stageCount - 1) * 0.5f), 0, _stageCount - 1);
        return tmp[i50].ToString("F0");
    }

    static float Now() => Time.realtimeSinceStartup;

    static float MsSince(float t0) => t0 < 0f ? 0f : (Now() - t0) * 1000f;

    void ClearJobStages()
    {
        for (int i = 0; i < StageN; i++) _stageLast[i] = 0f;
    }

    static float Percentile(float[] src, int n, float p)
    {
        var tmp = new float[n];
        Array.Copy(src, tmp, n);
        Array.Sort(tmp);
        int i = Mathf.Clamp(Mathf.RoundToInt((n - 1) * p), 0, n - 1);
        return tmp[i];
    }

    void Allocate(int w, int h)
    {
        _labels = new byte[w * h];
        _parent = new int[w * h];
        _votes = new int[w * h];
        _counts = new int[w * h];
        _minDepth = new float[w * h];
        _bx0 = new int[w * h];
        _by0 = new int[w * h];
        _bx1 = new int[w * h];
        _by1 = new int[w * h];
        _seen = new bool[w * h];
        _rgb = new byte[_npu.InputWidth * _npu.InputHeight * 3];
        _maskW = _maskH = 0;
    }

    /// <summary>
    /// The mask texture spans the GPU camera frame so its UV matches the camera texture's
    /// and the shader needs no remapping. The square inference result is inset into that
    /// canvas at a 1:1 pixel ratio with the CPU crop.
    /// </summary>
    void EnsureMaskGeometry()
    {
        int outW = _npu.OutputWidth, outH = _npu.OutputHeight;
        int mw = outW, mh = outH;
        int offX = 0, offY = 0;

        if (centreCrop && _camW > 0 && _camH > 0)
        {
            int texW = _gpuW > 16 ? _gpuW : _camW;
            int texH = _gpuH > 16 ? _gpuH : _camH;
            int side = Mathf.Min(_camW, _camH);
            float scale = (float)outW / side;

            CenteredAspectCrop(texW, texH, _camW, _camH,
                out int cropX, out int cropY, out int cropW, out int cropH);

            float maskPerGpu = (float)(_camW * scale) / Mathf.Max(1, cropW);
            mw = Mathf.Max(outW, Mathf.RoundToInt(texW * maskPerGpu));
            mh = Mathf.Max(outH, Mathf.RoundToInt(texH * maskPerGpu));
            int cpuMw = Mathf.RoundToInt(_camW * scale);
            int cpuMh = Mathf.RoundToInt(_camH * scale);
            offX = Mathf.RoundToInt(cropX * maskPerGpu) + (cpuMw - outW) / 2;
            offY = Mathf.RoundToInt(cropY * maskPerGpu) + (cpuMh - outH) / 2;
        }

        if (mw == _maskW && mh == _maskH && _maskTex != null) return;

        _maskW = mw;
        _maskH = mh;
        _offX = offX;
        _offY = offY;
        _overlay = new byte[mw * mh * 4];
        if (_maskTex != null) Destroy(_maskTex);
        _maskTex = new Texture2D(mw, mh, TextureFormat.RGBA32, false, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            // Canny is a 1-wide ridge; bilinear of that against black vanishes. Mattes
            // want bilinear so the silhouette isn't a stair-step.
            filterMode = IsCanny ? FilterMode.Point : FilterMode.Bilinear
        };
        if (_mat != null) _mat.SetTexture(IdMask, _maskTex);
    }

    /// <summary>
    /// Largest centred rectangle of <paramref name="innerW"/>:<paramref name="innerH"/>
    /// aspect inside <paramref name="outerW"/>x<paramref name="outerH"/>. This is the GPU
    /// texel box that a CPU image of that aspect occupies when it is a centre-crop of the
    /// GPU feed (4:3 CPU on a 16:9 camera is the usual case).
    /// </summary>
    static void CenteredAspectCrop(
        int outerW, int outerH, int innerW, int innerH,
        out int x, out int y, out int w, out int h)
    {
        float outerA = (float)outerW / outerH;
        float innerA = (float)innerW / innerH;
        if (innerA > outerA)
        {
            w = outerW;
            h = Mathf.Max(1, Mathf.RoundToInt(outerW / innerA));
            x = 0;
            y = (outerH - h) / 2;
        }
        else
        {
            h = outerH;
            w = Mathf.Max(1, Mathf.RoundToInt(outerH * innerA));
            y = 0;
            x = (outerW - w) / 2;
        }
    }

    void OnCameraFrame(ARCameraFrameEventArgs args)
    {
        if (args.textures == null || args.textures.Count == 0) return;
        var tex = args.textures[0];
        if (tex == null || tex.width < 16 || tex.height < 16) return;
        _gpuCamTex = tex;
        if (tex.width != _gpuW || tex.height != _gpuH)
        {
            _gpuW = tex.width;
            _gpuH = tex.height;
            _maskW = _maskH = 0;
        }

        if (!_wantGpuCapture) return;
        _wantGpuCapture = false;
        if (!TryQueueGpuBlit(tex))
        {
            _gpuInputFailed = true;
            _loadNote = "gpu blit failed — falling back to XRCpuImage";
            Debug.LogWarning("[Seg] " + _loadNote);
            SubmitCpuImageFrame();
        }
    }

    bool UseGpuCameraInput()
    {
        return gpuCameraInput
            && !_gpuInputFailed
            && !IsCanny
            && _npu.Ready
            && _gpuCamTex != null
            && _npu.InputWidth > 0
            && _npu.InputHeight > 0
            && (UseGlPath() || SystemInfo.supportsAsyncGPUReadback);
    }

    bool UseGlPath()
    {
        if (_glFailed || !gpuCameraInput || _gpuInputFailed) return false;
        if (IsCanny) return false;
        if (_disBlitMat == null || _maskPackMat == null) return false;
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3) return false;
        if (IsBench) return true;
        return _npu.GlPathReady;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    static IntPtr GlEventFn()
    {
        if (_glEventFn == IntPtr.Zero)
        {
            try { _glEventFn = npu_gl_event_fn(); }
            catch { _glEventFn = IntPtr.Zero; }
        }
        return _glEventFn;
    }

    static void IssueGlEvent(int id)
    {
        var fn = GlEventFn();
        if (fn != IntPtr.Zero)
        {
            GL.IssuePluginEvent(fn, id);
            return;
        }
        if (_glEventFnMissingLogged) return;
        _glEventFnMissingLogged = true;
        Debug.LogWarning("[Seg] npu_gl_event_fn missing — libnpu_gl.so not loaded");
    }
#else
    static void IssueGlEvent(int id) { }
#endif

    /// <summary>
    /// Grabs a camera frame and queues it. Returns without blocking, so frame time no
    /// longer includes inference.
    /// </summary>
    void SubmitFrame()
    {
        if (_glFailed) return;
        if (UseGpuCameraInput())
        {
            ClearJobStages();
            _tWant = Now();
            _tFrame = -1f;
            _wantGpuCapture = true;
            return;
        }
        SubmitCpuImageFrame();
    }

    void SubmitCpuImageFrame()
    {
        _gpuDisplaySpace = false;
        ClearJobStages();
        _tFrame = Now();
        if (_camera == null || !_camera.TryAcquireLatestCpuImage(out var image))
            return;

        int inW = _npu.InputWidth, inH = _npu.InputHeight;

        using (image)
        {
            _camW = image.width;
            _camH = image.height;
            int side = Mathf.Min(_camW, _camH);
            int srcW = centreCrop ? side : _camW;
            int srcH = centreCrop ? side : _camH;

            // Convert DOWNSAMPLES only: asking for dimensions larger than the source
            // throws "Converted image height must be less than or equal to native image
            // height", which is how every 513x513 model silently produced no frames at
            // all against this 640x480 camera image. Convert to the largest size that
            // fits and scale up here instead.
            _fitW = Mathf.Min(inW, srcW);
            _fitH = Mathf.Min(inH, srcH);

            var conv = new XRCpuImage.ConversionParams
            {
                inputRect = centreCrop
                    ? new RectInt((_camW - side) / 2, (_camH - side) / 2, side, side)
                    : new RectInt(0, 0, _camW, _camH),
                outputDimensions = new Vector2Int(_fitW, _fitH),
                outputFormat = TextureFormat.RGB24,
                transformation = XRCpuImage.Transformation.None
            };
            int size = image.GetConvertedDataSize(conv);
            if (_rgbFit == null || _rgbFit.Length < size) _rgbFit = new byte[size];
            var handle = new NativeArray<byte>(size, Allocator.Temp);
            float tConv = Now();
            try
            {
                image.Convert(conv, handle);
                NativeArray<byte>.Copy(handle, _rgbFit, size);
            }
            catch (Exception e)
            {
                _loadNote = $"convert {_fitW}x{_fitH} failed: {e.Message}";
                _convertNote = _loadNote;
                return;
            }
            finally
            {
                handle.Dispose();
            }
            _stageLast[SConvert] = MsSince(tConv);
        }

        int need = inW * inH * 3;
        if (_rgb == null || _rgb.Length < need) _rgb = new byte[need];
        float tResize = Now();
        if (_fitW == inW && _fitH == inH)
            Array.Copy(_rgbFit, _rgb, need);
        else
            ResizeRgb(_rgbFit, _fitW, _fitH, _rgb, inW, inH);
        _stageLast[SResize] = MsSince(tResize);

        _convertNote = $"{_fitW}x{_fitH} cpu" +
                       (_fitW == inW && _fitH == inH
                           ? " native, no rescale"
                           : $" then UPSCALED to {inW}x{inH} (CPU image cannot supply the tensor size)");

        EnsureMaskGeometry();

        float tRot = Now();
        byte[] input = Upright(_rgb);
        _stageLast[SRotate] = MsSince(tRot);
        float tSub = Now();
        if (!_npu.Submit(input) && !string.IsNullOrEmpty(_npu.LastError))
            _loadNote = $"submit failed: {_npu.LastError}";
        _stageLast[SSubmit] = MsSince(tSub);
        _tSubmit = Now();
    }

    bool TryQueueGpuBlit(Texture src)
    {
        int inW = _npu.InputWidth, inH = _npu.InputHeight;
        if (src == null || inW < 8 || inH < 8) return false;
        if (_mat == null) return false;

        if (UseGlPath())
            return TryQueueGlBlit(src, inW, inH);

        if (!EnsureInferRT(inW, inH)) return false;
        if (!EnsureCamCopyRT(src.width, src.height)) return false;

        _stageLast[SWaitCam] = MsSince(_tWant);
        _tFrame = Now();
        try
        {
            // Default Graphics.Blit samples the ARCore Vulkan camera as RGB. That is
            // YUV-as-RGB: a green field. IS-Net then returns a flat ~0.50 matte and
            // Ramp paints the whole frame green. The background material already
            // knows how to sample this texture.
            float segWas = _mat.GetFloat(IdSeg);
            _mat.SetFloat(IdSeg, 0f);
            try
            {
                Graphics.Blit(src, _camCopyRT, _mat);
            }
            finally
            {
                _mat.SetFloat(IdSeg, segWas);
            }

            SquareCropScaleOffset(_camCopyRT.width, _camCopyRT.height, inW, inH,
                centreCrop, gpuBlitFlipY, out Vector2 scale, out Vector2 offset);
            Graphics.Blit(_camCopyRT, _inferRT, scale, offset);
        }
        catch (Exception e)
        {
            _loadNote = $"gpu blit {src.width}x{src.height}: {e.Message}";
            return false;
        }
        _stageLast[SBlit] = MsSince(_tFrame);

        _gpuDisplaySpace = true;
        _camW = src.width;
        _camH = src.height;
        _fitW = inW;
        _fitH = inH;
        _convertNote = $"{inW}x{inH} gpu-blit (ARCore sample) from {src.width}x{src.height}" +
                       (centreCrop ? ", centred square" : ", squashed") +
                       (gpuBlitFlipY ? ", flipY" : "");
        EnsureMaskGeometry();

        _gpuReadback = AsyncGPUReadback.Request(_inferRT, 0, TextureFormat.RGBA32);
        _gpuReadbackPending = true;
        _tCopyStart = Now();
        return true;
    }

    bool EnsureCamCopyRT(int w, int h)
    {
        if (_camCopyRT != null && _camCopyRT.IsCreated() &&
            _camCopyRT.width == w && _camCopyRT.height == h)
            return true;
        ReleaseCamCopyRT();
        _camCopyRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
            name = "SegCamCopyRT"
        };
        if (!_camCopyRT.Create())
        {
            ReleaseCamCopyRT();
            return false;
        }
        return true;
    }

    bool EnsureInferRT(int w, int h)
    {
        if (_inferRT != null && _inferRT.IsCreated() && _inferRT.width == w && _inferRT.height == h)
            return true;
        ReleaseInferRT();
        _inferRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
            name = "SegInferRT"
        };
        if (!_inferRT.Create())
        {
            ReleaseInferRT();
            return false;
        }
        return true;
    }

    void ReleaseInferRT()
    {
        if (_inferRT != null)
        {
            _inferRT.Release();
            Destroy(_inferRT);
            _inferRT = null;
        }
        ReleaseCamCopyRT();
    }

    void ReleaseCamCopyRT()
    {
        if (_camCopyRT == null) return;
        _camCopyRT.Release();
        Destroy(_camCopyRT);
        _camCopyRT = null;
    }

    static void SquareCropScaleOffset(
        int srcW, int srcH, int dstW, int dstH, bool crop, bool flipY,
        out Vector2 scale, out Vector2 offset)
    {
        if (!crop || srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
        {
            scale = Vector2.one;
            offset = Vector2.zero;
        }
        else
        {
            float srcA = (float)srcW / srcH;
            float dstA = (float)dstW / dstH;
            if (Mathf.Abs(srcA - dstA) < 1e-4f)
            {
                scale = Vector2.one;
                offset = Vector2.zero;
            }
            else if (dstA > srcA)
            {
                float y = 1f - srcA / dstA;
                scale = new Vector2(1f, srcA / dstA);
                offset = new Vector2(0f, y * 0.5f);
            }
            else
            {
                float x = 1f - dstA / srcA;
                scale = new Vector2(dstA / srcA, 1f);
                offset = new Vector2(x * 0.5f, 0f);
            }
        }
        if (flipY)
        {
            offset.y += scale.y;
            scale.y = -scale.y;
        }
    }

    bool TryQueueGlBlit(Texture src, int inW, int inH)
    {
        if (!EnsureFloatRT(inW, inH) || !EnsureMatteRT(inW, inH)) return false;

        _stageLast[SWaitCam] = MsSince(_tWant);
        _tFrame = Now();

        if (!IsBench && !_npu.EglReady)
        {
            IssueGlEvent(GlEventCapture);
            _eglWaitFrames++;
            _convertNote = $"gl waiting for EGL share ({_eglWaitFrames} frames)";
            return true;
        }

        SquareCropScaleOffset(src.width, src.height, inW, inH, centreCrop, gpuBlitFlipY,
            out Vector2 cropScale, out Vector2 cropOffset);
        _disBlitMat.SetVector("_CropScale", cropScale);
        _disBlitMat.SetVector("_CropOffset", cropOffset);
        _disBlitMat.SetFloat("_Mean", inputMean);
        _disBlitMat.SetFloat("_Scale", inputScale == 0f ? 1f : inputScale);
        if (_mat != null && _mat.HasProperty(IdDisplay))
            _disBlitMat.SetMatrix(IdDisplay, _mat.GetMatrix(IdDisplay));

        try
        {
            if (IsBench)
            {
                _disBlitMat.SetFloat("_Normalize", 0f);
                _disBlitMat.SetFloat("_Luma", 1f);
                Graphics.Blit(src, _matteRT, _disBlitMat);
            }
            else
            {
                _disBlitMat.SetFloat("_Normalize", 1f);
                _disBlitMat.SetFloat("_Luma", 0f);
                Graphics.Blit(src, _inferFloatRT, _disBlitMat);
                int rgbId = (int)_inferFloatRT.GetNativeTexturePtr();
                int matteId = (int)_matteRT.GetNativeTexturePtr();
                _npu.SetGlTextures(rgbId, matteId, inW, inH);
                IssueGlEvent(GlEventPack);
                _glAwaitSubmit = true;
                _glPackFrame = Time.frameCount;
            }
        }
        catch (Exception e)
        {
            _loadNote = $"gl blit {src.width}x{src.height}: {e.Message}";
            _glFailed = true;
            return false;
        }

        _stageLast[SBlit] = MsSince(_tFrame);
        _gpuDisplaySpace = true;
        _camW = src.width;
        _camH = src.height;
        _fitW = inW;
        _fitH = inH;
        _convertNote = IsBench
            ? $"{inW}x{inH} gl-zero-copy luma from {src.width}x{src.height}"
            : $"{inW}x{inH} gl-zero-copy from {src.width}x{src.height}";
        if (centreCrop) _convertNote += ", centred square";
        if (gpuBlitFlipY) _convertNote += ", flipY";
        EnsureMaskGeometry();

        if (IsBench)
        {
            float tPaint = Now();
            PackMaskGpu();
            _stageLast[SPaint] = MsSince(tPaint);
            _stageLast[SCopy] = _stageLast[SUnpack] = _stageLast[SSubmit] = 0f;
            _stageLast[SFill] = _stageLast[SDecode] = _stageLast[SUpload] = 0f;
            _stageLast[SRun] = 0f;
            _stageLast[SE2E] = MsSince(_tFrame);
            _tSubmit = Now();
            StampMaskPeriod();
            RecordStages();
        }

        return true;
    }

    void TrySubmitGl()
    {
        if (!_glAwaitSubmit || _npu.Busy) return;
        if (!_npu.EglReady)
        {
            IssueGlEvent(GlEventCapture);
            return;
        }
        if (Time.frameCount <= _glPackFrame) return;
        _glAwaitSubmit = false;
        float tSub = Now();
        if (!_npu.SubmitGl())
        {
            string e = _npu.LastError ?? "";
            if (e.IndexOf("EGL not captured", System.StringComparison.Ordinal) >= 0)
            {
                _glAwaitSubmit = true;
                IssueGlEvent(GlEventCapture);
                return;
            }
            if (!string.IsNullOrEmpty(e))
            {
                _loadNote = $"submitGl failed: {e}";
                FailGl(e);
            }
        }
        _stageLast[SSubmit] = MsSince(tSub);
        _stageLast[SCopy] = _stageLast[SUnpack] = 0f;
        _tSubmit = Now();
    }

    void CollectGlResult()
    {
        if (!_npu.GlPathReady) return;
        if (!_npu.PollGl()) return;

        _stageLast[SInferWait] = MsSince(_tSubmit);
        _stageLast[SFill] = _npu.LastFillMs;
        _stageLast[SRun] = _npu.LastRunMs;
        _stageLast[SDecode] = _npu.LastDecodeMs;
        IssueGlEvent(GlEventUnpack);
        float tPaint = Now();
        PackMaskGpu();
        _stageLast[SPaint] = MsSince(tPaint);
        _stageLast[SUpload] = 0f;
        _stageLast[SE2E] = MsSince(_tFrame);
        StampMaskPeriod();
        RecordStages();
        if (!string.IsNullOrEmpty(_npu.LastError))
            _loadNote = $"gl infer: {_npu.LastError}";
    }

    void WatchGlFailure()
    {
        if (_glFailed || !_npu.GlPathReady || _npu.Busy || _glAwaitSubmit) return;
        string e = _npu.LastError;
        if (string.IsNullOrEmpty(e)) return;
        if (e.IndexOf("EGL not captured", System.StringComparison.Ordinal) >= 0)
            return;
        if (e.IndexOf("no current EGL", System.StringComparison.Ordinal) >= 0)
            return;
        if (e.IndexOf("REJECT gl", System.StringComparison.Ordinal) >= 0
            || e.IndexOf("CreateFromGl", System.StringComparison.Ordinal) >= 0
            || e.IndexOf("LiteRt", System.StringComparison.Ordinal) >= 0
            || e.IndexOf("eglMakeCurrent", System.StringComparison.Ordinal) >= 0
            || e.StartsWith("gl run", System.StringComparison.Ordinal)
            || e.StartsWith("gl path", System.StringComparison.Ordinal))
        {
            if (e == _ackedGlError) return;
            _ackedGlError = e;
            FailGl(e);
        }
    }

    IEnumerator PumpEglCapture()
    {
        int n = 0;
        while (_npu.GlPathReady && !_npu.EglReady && n < 90 && !_glFailed)
        {
            IssueGlEvent(GlEventCapture);
            yield return new WaitForEndOfFrame();
            if (_npu.TryCaptureEgl())
                break;
            string err = _npu.LastError ?? "";
            if (err.IndexOf("compile", System.StringComparison.Ordinal) >= 0
                || err.IndexOf("link", System.StringComparison.Ordinal) >= 0)
            {
                FailGl(err);
                yield break;
            }
            n++;
            _eglWaitFrames = n;
        }
        if (_npu.EglReady)
        {
            _eglWaitFrames = 0;
            Debug.Log("[Seg] EGL share context ready");
        }
        else if (!_glFailed && _npu.GlPathReady)
            FailGl("EGL not captured after 90 frames — " + _npu.LastError);
    }

    void FailGl(string why)
    {
        if (_glFailed) return;
        _glFailed = true;
        _glAwaitSubmit = false;
        _loadNote = "gl path failed (Java fallback OFF): " + why;
        Debug.LogWarning("[Seg] " + _loadNote);
    }

    void StampMaskPeriod()
    {
        float now = Now();
        if (_lastMaskAt >= 0f)
            _maskPeriodMs = (now - _lastMaskAt) * 1000f;
        _lastMaskAt = now;
    }

    void PackMaskGpu()
    {
        if (_maskPackMat == null || _matteRT == null) return;
        EnsureMaskGeometry();
        if (!EnsureMaskRT(_maskW, _maskH)) return;
        _maskPackMat.SetTexture("_Matte", _matteRT);
        _maskPackMat.SetFloat("_ScalarFloor", scalarFloor);
        _maskPackMat.SetFloat("_PackedMetres", MatteFallbackMetres());
        _maskPackMat.SetFloat("_MaxDist", maxOcclusionDistance);
        _maskPackMat.SetVector("_Inset", new Vector4(_offX, _offY, 0f, 0f));
        _maskPackMat.SetVector("_MatteSize", new Vector4(_npu.OutputWidth, _npu.OutputHeight, 0f, 0f));
        _maskPackMat.SetVector("_MaskSize", new Vector4(_maskW, _maskH, 0f, 0f));
        Graphics.Blit(_matteRT, _maskRT, _maskPackMat);
        if (_mat != null) _mat.SetTexture(IdMask, _maskRT);
        _mattePackedMetres = MatteFallbackMetres();
        _lastThingPixels = 1;
        _lastComponents = 1;
        _lastExpanded = 1;
    }

    bool EnsureFloatRT(int w, int h)
    {
        if (_inferFloatRT != null && _inferFloatRT.IsCreated() &&
            _inferFloatRT.width == w && _inferFloatRT.height == h)
            return true;
        ReleaseRT(ref _inferFloatRT);
        _inferFloatRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
            name = "SegInferFloatRT"
        };
        return _inferFloatRT.Create();
    }

    bool EnsureMatteRT(int w, int h)
    {
        if (_matteRT != null && _matteRT.IsCreated() &&
            _matteRT.width == w && _matteRT.height == h)
            return true;
        ReleaseRT(ref _matteRT);
        _matteRT = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
            enableRandomWrite = true,
            name = "SegMatteRT"
        };
        return _matteRT.Create();
    }

    bool EnsureMaskRT(int w, int h)
    {
        if (w < 8 || h < 8) return false;
        if (_maskRT != null && _maskRT.IsCreated() &&
            _maskRT.width == w && _maskRT.height == h)
            return true;
        ReleaseRT(ref _maskRT);
        _maskRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
            name = "SegMaskRT"
        };
        return _maskRT.Create();
    }

    void ReleaseGlRTs()
    {
        ReleaseRT(ref _inferFloatRT);
        ReleaseRT(ref _matteRT);
        ReleaseRT(ref _maskRT);
    }

    static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        Destroy(rt);
        rt = null;
    }

    void TryFinishGpuReadback()
    {
        if (!_gpuReadbackPending) return;
        if (!_gpuReadback.done) return;
        _gpuReadbackPending = false;
        if (_gpuReadback.hasError)
        {
            _gpuInputFailed = true;
            _loadNote = "gpu readback failed — falling back to XRCpuImage";
            Debug.LogWarning("[Seg] " + _loadNote);
            return;
        }
        if (!_npu.Ready || _reloading) return;

        _stageLast[SCopy] = MsSince(_tCopyStart);

        NativeArray<byte> data;
        try
        {
            data = _gpuReadback.GetData<byte>();
        }
        catch (Exception e)
        {
            _gpuInputFailed = true;
            _loadNote = $"gpu readback GetData: {e.Message}";
            Debug.LogWarning("[Seg] " + _loadNote);
            return;
        }

        int inW = _npu.InputWidth, inH = _npu.InputHeight;
        int px = inW * inH;
        int need = px * 3;
        if (_rgb == null || _rgb.Length < need) _rgb = new byte[need];
        int srcPx = Mathf.Min(px, data.Length / 4);
        float tUnpack = Now();
        for (int i = 0; i < srcPx; i++)
        {
            int s = i * 4;
            int d = i * 3;
            _rgb[d] = data[s];
            _rgb[d + 1] = data[s + 1];
            _rgb[d + 2] = data[s + 2];
        }
        _stageLast[SUnpack] = MsSince(tUnpack);

        if (LooksLikeBadCameraInput(_rgb, srcPx))
        {
            _gpuInputFailed = true;
            _loadNote = "gpu blit was not a camera image — falling back to XRCpuImage";
            Debug.LogWarning("[Seg] " + _loadNote);
            return;
        }

        float tRot = Now();
        byte[] input = _gpuDisplaySpace ? _rgb : Upright(_rgb);
        _stageLast[SRotate] = MsSince(tRot);
        float tSub = Now();
        if (!_npu.Submit(input) && !string.IsNullOrEmpty(_npu.LastError))
            _loadNote = $"submit failed: {_npu.LastError}";
        _stageLast[SSubmit] = MsSince(tSub);
        _tSubmit = Now();
    }

    /// <summary>
    /// Y sampled as RGB is G-dominant; an unwritten RT is near-black. Either one made
    /// IS-Net return a flat 0.50 matte and Ramp wash the frame green.
    /// </summary>
    static bool LooksLikeBadCameraInput(byte[] rgb, int px)
    {
        if (px < 64) return false;
        long r = 0, g = 0, b = 0;
        int step = Mathf.Max(1, px / 2048);
        int n = 0;
        for (int i = 0; i < px; i += step)
        {
            r += rgb[i * 3];
            g += rgb[i * 3 + 1];
            b += rgb[i * 3 + 2];
            n++;
        }
        if (n == 0) return false;
        if ((r + g + b) < n * 24) return true;
        return g > r * 3 / 2 && g > b * 3 / 2;
    }

    /// <summary>Takes a finished label map, if one is waiting, and rebuilds the mask.</summary>
    void CollectResult()
    {
        var labels = _npu.PollLabels();
        if (labels == null)
        {
            if (!string.IsNullOrEmpty(_npu.LastError) && !_npu.Busy)
                _loadNote = $"infer failed: {_npu.LastError}";
            return;
        }

        _labels = labels;
        float now = Now();
        if (_lastMaskAt >= 0f)
            _maskPeriodMs = (now - _lastMaskAt) * 1000f;
        _lastMaskAt = now;
        _stageLast[SInferWait] = MsSince(_tSubmit);
        // Coral keeps the thing/stuff vote. Mattes (MODNet and friends) now pack a
        // depth into alpha so the silhouette occludes; inverse-depth maps stay tint-only
        // because their range is not metres.
        float tPaint = Now();
        if (_npu.ScalarOutput || IsCanny || IsBench) PaintScalarView();
        else if (_npu.OutputChannels == 19) PaintClassView();
        else
        {
            GrabDepth();
            VoteAndExpand();
        }
        _stageLast[SPaint] = MsSince(tPaint);
        if (_maskTex == null)
        {
            RecordStages();
            return;
        }
        float tUp = Now();
        _maskTex.LoadRawTextureData(_overlay);
        _maskTex.Apply(false, false);
        _stageLast[SUpload] = MsSince(tUp);
        _stageLast[SE2E] = MsSince(_tFrame);
        RecordStages();
    }

    /// <summary>Bilinear RGB24 rescale, for models whose input exceeds the camera image.</summary>
    static void ResizeRgb(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        float xr = (float)sw / dw, yr = (float)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            float sy = (y + 0.5f) * yr - 0.5f;
            int y0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, sh - 1);
            int y1 = Mathf.Min(y0 + 1, sh - 1);
            float fy = Mathf.Clamp01(sy - y0);

            for (int x = 0; x < dw; x++)
            {
                float sx = (x + 0.5f) * xr - 0.5f;
                int x0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, sw - 1);
                int x1 = Mathf.Min(x0 + 1, sw - 1);
                float fx = Mathf.Clamp01(sx - x0);

                int i00 = (y0 * sw + x0) * 3, i01 = (y0 * sw + x1) * 3;
                int i10 = (y1 * sw + x0) * 3, i11 = (y1 * sw + x1) * 3;
                int d = (y * dw + x) * 3;

                for (int c = 0; c < 3; c++)
                {
                    float top = src[i00 + c] + (src[i01 + c] - src[i00 + c]) * fx;
                    float bot = src[i10 + c] + (src[i11 + c] - src[i10 + c]) * fx;
                    dst[d + c] = (byte)(top + (bot - top) * fy + 0.5f);
                }
            }
        }
    }

    /// <summary>
    /// Turns the sensor-oriented crop into the orientation the network was trained on.
    /// Returns the source untouched at 0 degrees, or when a quarter turn would need a
    /// square and the tensor is not one.
    /// </summary>
    /// <summary>
    /// The rotation that can actually be applied. A quarter turn of a non-square tensor
    /// swaps its dimensions, which no longer fits the model, so it has to be skipped — and
    /// skipped in BOTH directions. Un-rotating a label map that was never rotated walks the
    /// mask index off the end of its own row, which on a 504x896 depth model is an
    /// out-of-range throw rather than a wrong picture.
    /// </summary>
    int EffectiveRotation()
    {
        // ARCore-material blit is already in display UV, same space the shader samples
        // the mask with. Rotating again would lie the photo down and un-rotate the labels
        // off the overlay.
        if (_gpuDisplaySpace) return 0;
        int rot = RotationDegrees;
        if (rot != 90 && rot != 270) return rot;
        if (_npu.InputWidth != _npu.InputHeight) return 0;
        if (_npu.OutputWidth != _npu.OutputHeight) return 0;
        return rot;
    }

    byte[] Upright(byte[] src)
    {
        int rot = EffectiveRotation();
        int w = _npu.InputWidth, h = _npu.InputHeight;
        if (rot == 0) return src;

        int need = w * h * 3;
        if (_rgbRot == null || _rgbRot.Length < need) _rgbRot = new byte[need];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SourceOf(x, y, w, h, rot, out int sx, out int sy);
                int d = (y * w + x) * 3;
                int s = (sy * w + sx) * 3;
                _rgbRot[d] = src[s];
                _rgbRot[d + 1] = src[s + 1];
                _rgbRot[d + 2] = src[s + 2];
            }
        }
        return _rgbRot;
    }

    /// <summary>
    /// Where a pixel of the ROTATED image came from in the un-rotated crop. Used both to
    /// build the rotated input and to put labels back where they belong, so the mask stays
    /// in camera orientation and the shader keeps its existing coordinates.
    /// </summary>
    static void SourceOf(int x, int y, int w, int h, int rot, out int sx, out int sy)
    {
        switch (rot)
        {
            case 90:  sx = y;             sy = h - 1 - x; break;
            case 180: sx = w - 1 - x;     sy = h - 1 - y; break;
            case 270: sx = w - 1 - y;     sy = x;         break;
            default:  sx = x;             sy = y;         break;
        }
    }

    /// <summary>Index into <see cref="_overlay"/> for a pixel of the rotated label map.</summary>
    int MaskIndex(int rx, int ry, int w, int h)
    {
        SourceOf(rx, ry, w, h, EffectiveRotation(), out int cx, out int cy);
        return (cy + _offY) * _maskW + (cx + _offX);
    }

    void GrabDepth()
    {
        _depthW = _depthH = 0;
        if (_occlusion == null || !_occlusion.enabled) return;
        if (!_occlusion.TryAcquireEnvironmentDepthCpuImage(out var image)) return;

        using (image)
        {
            var plane = image.GetPlane(0);
            _depthW = image.width;
            _depthH = image.height;
            int n = _depthW * _depthH;
            if (_depthM == null || _depthM.Length < n) _depthM = new float[n];
            for (int y = 0; y < _depthH; y++)
                for (int x = 0; x < _depthW; x++)
                    _depthM[y * _depthW + x] = DepthOcclusion.ReadMetres(
                        plane.data, plane, image.format, x, y);
        }
    }

    static bool IsRetired(string file)
    {
        if (string.IsNullOrEmpty(file)) return true;
        string f = file.ToLowerInvariant();
        return f == "deeplabv3_257_mv_gpu.tflite"
            || f == "deeplabv3_mnv2_pascal_8bit.tflite"
            || f == "coral_deeplabv3_mnv2_pascal_quant.tflite"
            || f.StartsWith("mediapipe_selfie");
    }

    static bool IsToySeg(string file)
    {
        if (string.IsNullOrEmpty(file)) return false;
        string f = file.ToLowerInvariant();
        return f.Contains("fast_scnn") || f.Contains("bisenet")
            || f.Contains("mobilenetv4") || f.Contains("pidnet_s")
            || f.Contains("cnn_s");
    }

    static SegBackend PreferredBackend(string file)
    {
        if (string.Equals(file, CannyModel, StringComparison.OrdinalIgnoreCase))
            return SegBackend.Cpu;
        if (string.Equals(file, BenchModel, StringComparison.OrdinalIgnoreCase))
            return SegBackend.Cpu;
        if (IsToySeg(file)) return SegBackend.GpuDec;
        string f = (file ?? string.Empty).ToLowerInvariant();
        if (f.Contains("isnet") || f.Contains("dis_")
            || f.Contains("modnet") || f.Contains("u2net")
            || f.Contains("depth_anything") || f.Contains("da3"))
            return SegBackend.GpuDec;
        return SegBackend.Cpu;
    }

    /// <summary>
    /// Cityscapes overlay: every class, including road / building / sky, and no occlusion.
    /// The thing/stuff split is the wrong question while we are still asking what the
    /// network even sees — hiding class 0 (road) and class 2 (building) would paint a
    /// picture of poles and people and leave the facade blank.
    /// </summary>
    void PaintClassView()
    {
        int w = _npu.OutputWidth;
        int h = _npu.OutputHeight;
        Array.Clear(_overlay, 0, _overlay.Length);
        Array.Clear(_hist, 0, _hist.Length);
        _lastThingPixels = _lastStuffPixels = _lastExpanded = _lastComponents = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int cls = _labels[y * w + x];
                _hist[cls]++;
                if (IsThing(cls)) _lastThingPixels++;
                else _lastStuffPixels++;
                Color32 c = ColorForClass(cls);
                int o = MaskIndex(x, y, w, h) * 4;
                _overlay[o] = c.r;
                _overlay[o + 1] = c.g;
                _overlay[o + 2] = c.b;
            }
        }
    }

    /// <summary>
    /// True for a 0..1 matte (MODNet, IS-Net, …). Inverse-depth maps share the same tensor
    /// shape; they stay overlay-only because stretching their range into metres is a lie.
    /// </summary>
    bool ScalarOccludes()
    {
        if (!_npu.ScalarOutput) return false;
        string k = _npu.OutputKind;
        return !string.IsNullOrEmpty(k) &&
               k.IndexOf("alpha", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Colour-ramp overlay, and for an alpha matte the occlusion the shader already knows
    /// how to apply: mask alpha is a packed distance, 0 means "do not occlude". Depth maps
    /// keep alpha at 0. Class tables and connected components are skipped — a class id is
    /// not what came back.
    ///
    /// ARCore depth is used when it has a reading inside <see cref="maxOcclusionDistance"/>
    /// so a person at 3 m occludes at 3 m. When it does not (far field, depth off), the
    /// matte still punches a hole at <see cref="MatteFallbackMetres"/> — otherwise a
    /// correct silhouette would tint and never hide the building, which is how this path
    /// shipped as view-only.
    /// </summary>
    void PaintScalarView()
    {
        int w = _npu.OutputWidth;
        int h = _npu.OutputHeight;
        Array.Clear(_overlay, 0, _overlay.Length);
        Array.Clear(_hist, 0, _hist.Length);
        _lastThingPixels = _lastStuffPixels = _lastExpanded = _lastComponents = 0;

        bool occlude = ScalarOccludes();
        float packedMetres = 0f;
        _mattePackedMetres = 0f;
        if (occlude)
        {
            GrabDepth();
            packedMetres = MatteFallbackMetres();
            if (_depthW > 0)
            {
                float nearest = 0f;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int v = _labels[y * w + x];
                        if (v < scalarFloor) continue;
                        float metres = DepthAt(x, y, w, h, out _);
                        if (metres <= 0f) continue;
                        if (maxOcclusionDistance > 0f && metres >= maxOcclusionDistance) continue;
                        if (nearest <= 0f || metres < nearest) nearest = metres;
                    }
                }
                if (nearest > 0f) packedMetres = nearest;
            }
            _mattePackedMetres = packedMetres;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int v = _labels[y * w + x];
                _hist[v]++;
                if (v < scalarFloor) continue;
                _lastThingPixels++;
                // Canny is a line drawing, not a matte. Ramp(1) is dark red (0.5,0,0) and
                // a 1-px ridge of that mixed 55% into the camera is invisible. Cyan matches
                // the desk overlay.
                Color32 c = IsCanny ? new Color32(0, 220, 255, 0) : Ramp(v * (1f / 255f));
                int o = MaskIndex(x, y, w, h) * 4;
                _overlay[o] = c.r;
                _overlay[o + 1] = c.g;
                _overlay[o + 2] = c.b;
                if (occlude && packedMetres > 0f)
                {
                    float scale = maxOcclusionDistance > 0f ? maxOcclusionDistance : 16f;
                    _overlay[o + 3] = (byte)Mathf.Clamp(
                        Mathf.RoundToInt(packedMetres / scale * 255f), 1, 255);
                }
            }
        }
        if (occlude && _lastThingPixels > 0)
        {
            _lastComponents = 1;
            _lastExpanded = 1;
        }
    }

    /// <summary>
    /// Near enough to sit in front of the building, far enough not to fight a real
    /// ARCore reading of a person at arm's length when one exists.
    /// </summary>
    float MatteFallbackMetres()
    {
        float cap = maxOcclusionDistance > 0f ? maxOcclusionDistance : 16f;
        return Mathf.Min(2f, cap * 0.5f);
    }

    /// <summary>
    /// Blue through green to red. High is NEAR for a depth model, because MiDaS and friends
    /// return inverse depth, and "present" for a matte.
    /// </summary>
    static Color32 Ramp(float t)
    {
        t = Mathf.Clamp01(t);
        float r = Mathf.Clamp01(1.5f - Mathf.Abs(4f * t - 3f));
        float g = Mathf.Clamp01(1.5f - Mathf.Abs(4f * t - 2f));
        float b = Mathf.Clamp01(1.5f - Mathf.Abs(4f * t - 1f));
        return new Color32((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f), 0);
    }

    void VoteAndExpand()
    {
        int w = _npu.OutputWidth;
        int h = _npu.OutputHeight;
        int n = w * h;
        Array.Clear(_overlay, 0, _overlay.Length);
        Array.Clear(_votes, 0, n);
        Array.Clear(_counts, 0, n);
        Array.Clear(_minDepth, 0, n);
        Array.Clear(_seen, 0, n);
        Array.Clear(_hist, 0, _hist.Length);
        _roots.Clear();
        _lastThingPixels = _lastStuffPixels = _lastExpanded = _lastComponents = 0;

        for (int i = 0; i < n; i++)
        {
            _parent[i] = i;
            int cls = _labels[i];
            _hist[cls]++;
            if (IsThing(cls)) _lastThingPixels++;
            else _lastStuffPixels++;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!IsThing(_labels[i])) continue;
                if (x + 1 < w && _labels[i] == _labels[i + 1] && IsThing(_labels[i + 1]))
                    Union(i, i + 1);
                if (y + 1 < h && _labels[i] == _labels[i + w] && IsThing(_labels[i + w]))
                    Union(i, i + w);
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!IsThing(_labels[i])) continue;
                int root = Find(i);
                if (_counts[root] == 0)
                {
                    _bx0[root] = _bx1[root] = x;
                    _by0[root] = _by1[root] = y;
                }
                else
                {
                    if (x < _bx0[root]) _bx0[root] = x;
                    if (x > _bx1[root]) _bx1[root] = x;
                    if (y < _by0[root]) _by0[root] = y;
                    if (y > _by1[root]) _by1[root] = y;
                }
                _counts[root]++;
                float metres = DepthAt(x, y, w, h, out _);
                if (metres > 0f &&
                    (maxOcclusionDistance <= 0f || metres < maxOcclusionDistance))
                {
                    _votes[root]++;
                    if (_minDepth[root] <= 0f || metres < _minDepth[root])
                        _minDepth[root] = metres;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (!IsThing(_labels[i])) continue;
            int root = Find(i);
            if (_seen[root]) continue;
            _seen[root] = true;
            _lastComponents++;
            if (_votes[root] >= minVotePixels && _minDepth[root] > 0f)
            {
                _lastExpanded++;
                _roots.Add(root);
            }
        }

        // Silhouette. The tint is written for every thing pixel whether or not it was
        // accepted, so `segdebug` shows what the model predicted rather than only what
        // survived the depth vote.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!IsThing(_labels[i])) continue;
                int root = Find(i);
                bool accepted = _votes[root] >= minVotePixels && _minDepth[root] > 0f;
                Paint(MaskIndex(x, y, w, h), _labels[i], debugTint,
                      !boundingBox && accepted ? _minDepth[root] : 0f);
            }
        }

        if (!boundingBox) return;

        foreach (int root in _roots)
        {
            for (int y = _by0[root]; y <= _by1[root]; y++)
                for (int x = _bx0[root]; x <= _bx1[root]; x++)
                    Paint(MaskIndex(x, y, w, h), _labels[root], debugTint, _minDepth[root]);
        }
    }

    /// <summary>
    /// Alpha carries the object's nearest voting depth, packed against
    /// <see cref="maxOcclusionDistance"/>; 0 means "do not occlude". RGB is the tint and
    /// is only written when the overlay is wanted, so occlusion can be judged without
    /// pink paint over the thing being judged — the shader tints on non-black RGB.
    /// </summary>
    void Paint(int i, int cls, bool tint, float metres)
    {
        int o = i * 4;
        if (tint)
        {
            Color32 paint = ColorForClass(cls);
            _overlay[o] = paint.r;
            _overlay[o + 1] = paint.g;
            _overlay[o + 2] = paint.b;
        }
        if (metres <= 0f) return;
        float scale = maxOcclusionDistance > 0f ? maxOcclusionDistance : 16f;
        _overlay[o + 3] = (byte)Mathf.Clamp(
            Mathf.RoundToInt(metres / scale * 255f), 1, 255);
    }

    /// <summary>
    /// Depth for a pixel of the ROTATED label map. The depth image is in camera orientation
    /// and covers the whole frame, so the label pixel has to be carried back through both
    /// the rotation and the crop inset before it can be looked up.
    /// </summary>
    float DepthAt(int x, int y, int w, int h, out int maskIndex)
    {
        SourceOf(x, y, w, h, EffectiveRotation(), out int cx, out int cy);
        int mx = cx + _offX;
        int my = cy + _offY;
        maskIndex = my * _maskW + mx;
        if (_depthW <= 0) return 0f;
        float u = (mx + 0.5f) / _maskW;
        float v = (my + 0.5f) / _maskH;
        int dx = Mathf.Clamp((int)(u * _depthW), 0, _depthW - 1);
        int dy = Mathf.Clamp((int)(v * _depthH), 0, _depthH - 1);
        return _depthM[dy * _depthW + dx];
    }

    bool IsThing(int cls) => cls >= 0 && cls < _isThing.Length && _isThing[cls];

    int Find(int i)
    {
        int p = i;
        while (_parent[p] != p) p = _parent[p];
        while (_parent[i] != p)
        {
            int n = _parent[i];
            _parent[i] = p;
            i = n;
        }
        return p;
    }

    void Union(int a, int b)
    {
        a = Find(a);
        b = Find(b);
        if (a != b) _parent[b] = a;
    }

    void ApplyMaterialFlags()
    {
        if (_mat == null) return;
        bool live = enableOnStart && _npu.Ready;
        _mat.SetFloat(IdSeg, live ? 1f : 0f);
        _mat.SetFloat(IdDbg, live ? 1f : 0f);
        _mat.SetFloat(IdMax, maxOcclusionDistance);
        if (_maskTex != null) _mat.SetTexture(IdMask, _maskTex);
    }

    void ConfigureThingTable(int channels)
    {
        // 19-class Cityscapes trainIds. Everything else (PASCAL 21-class logits, or an
        // already-argmax'd label map) uses the PASCAL thing table. Class 0 is background
        // / plaza / sky / building and never occludes.
        bool cityscapes = channels == 19;
        _isThing = cityscapes ? BuildCityscapesThing() : BuildPascalThing();
        _classNames = cityscapes ? CityscapesNames : PascalNames;
    }

    /// <summary>
    /// PASCAL VOC 21, in label order. `bottle` is class 5 — which is why a bottle is a
    /// fair test of this model and a cardboard box is not.
    /// </summary>
    static readonly string[] PascalNames =
    {
        "background", "aeroplane", "bicycle", "bird", "boat", "bottle", "bus", "car",
        "cat", "chair", "cow", "diningtable", "dog", "horse", "motorbike", "person",
        "pottedplant", "sheep", "sofa", "train", "tv"
    };

    static readonly string[] CityscapesNames =
    {
        "road", "sidewalk", "building", "wall", "fence", "pole", "trafficlight",
        "trafficsign", "vegetation", "terrain", "sky", "person", "rider", "car",
        "truck", "bus", "train", "motorcycle", "bicycle"
    };

    /// <summary>PASCAL VOC: every labelled object. Class 0 is plaza/road/building/sky.</summary>
    static bool[] BuildPascalThing()
    {
        var t = new bool[256];
        for (int id = 1; id <= 20; id++)
            t[id] = true;
        return t;
    }

    Color32 ColorForClass(int cls)
    {
        // Dispatch on the live table rather than the class id: Cityscapes person is 11,
        // PASCAL person is 15, and using the PASCAL palette on PIDNet painted the road
        // orange and the facade the same colour as a train.
        return _classNames == CityscapesNames ? ColorForCityscapes(cls) : ColorForPascal(cls);
    }

    static Color32 ColorForPascal(int cls)
    {
        // PASCAL VOC thing colours. Floor / plaza / sky stay unpainted (class 0).
        switch (cls)
        {
            case 15: return new Color32(255, 50, 180, 255);  // person
            case 5:  return new Color32(120, 255, 40, 255);  // bottle
            case 9:  return new Color32(200, 60, 255, 255);  // chair
            case 16: return new Color32(0, 255, 120, 255);   // pottedplant
            case 7:  return new Color32(0, 220, 255, 255);   // car
            case 6:  return new Color32(40, 90, 255, 255);   // bus
            case 14: return new Color32(255, 220, 0, 255);   // motorbike
            case 2:  return new Color32(80, 255, 80, 255);   // bicycle
            case 19: return new Color32(255, 140, 0, 255);   // train
            case 1:  return new Color32(255, 60, 60, 255);   // aeroplane
            case 4:  return new Color32(0, 200, 160, 255);   // boat
            default: return new Color32(255, 140, 0, 255);
        }
    }

    /// <summary>
    /// Brighter than the official Cityscapes palette, which is too dark (building is
    /// 70,70,70) to read as an overlay on a camera feed.
    /// </summary>
    static Color32 ColorForCityscapes(int cls)
    {
        switch (cls)
        {
            case 0:  return new Color32(180, 70, 180, 255);  // road
            case 1:  return new Color32(255, 80, 200, 255);  // sidewalk
            case 2:  return new Color32(40, 200, 220, 255);  // building
            case 3:  return new Color32(140, 140, 220, 255);  // wall
            case 4:  return new Color32(230, 170, 170, 255);  // fence
            case 5:  return new Color32(220, 220, 80, 255);  // pole
            case 6:  return new Color32(255, 180, 40, 255);  // traffic light
            case 7:  return new Color32(255, 255, 80, 255);  // traffic sign
            case 8:  return new Color32(80, 220, 60, 255);   // vegetation
            case 9:  return new Color32(160, 255, 160, 255);  // terrain
            case 10: return new Color32(80, 170, 255, 255);  // sky
            case 11: return new Color32(255, 50, 180, 255);  // person
            case 12: return new Color32(255, 80, 80, 255);   // rider
            case 13: return new Color32(40, 90, 255, 255);   // car
            case 14: return new Color32(40, 40, 200, 255);   // truck
            case 15: return new Color32(40, 120, 255, 255);  // bus
            case 16: return new Color32(255, 140, 0, 255);   // train
            case 17: return new Color32(80, 80, 255, 255);   // motorcycle
            case 18: return new Color32(80, 255, 80, 255);   // bicycle
            default: return new Color32(255, 140, 0, 255);
        }
    }

    /// <summary>Cityscapes 19-class trainIds. Road/sidewalk/terrain/sky/building never occlude.</summary>
    static bool[] BuildCityscapesThing()
    {
        var t = new bool[256];
        foreach (int id in new[] { 5, 6, 7, 11, 12, 13, 14, 15, 16, 17, 18 })
            t[id] = true;
        return t;
    }
}
