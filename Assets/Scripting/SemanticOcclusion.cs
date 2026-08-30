using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
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
    public const string DefaultModelFile = "coral_deeplabv3_mnv2_pascal_quant.tflite";
    public const string CannyModel = "canny";

    public enum SegBackend
    {
        Cpu,     // XNNPACK
        Gpu,     // NNAPI hybrid (GPU/CPU) — this is NOT the Mali GpuDelegate
        GpuDec,  // tensorflow-lite-gpu GpuDelegate on Mali
        Npu      // ENN, CPU disabled
    }

    [SerializeField] bool enableOnStart;
    [SerializeField] string modelFile = DefaultModelFile;

    [Tooltip("Cycled by the HUD's model button, in this order. Anything pushed to the " +
             "device as a .tflite joins the cycle too, without a rebuild. `canny` is CPU " +
             "edges, not a file.")]
    // Only the one that actually found the bottle survives in the APK. The four DeepLab /
    // MediaPipe also-rans were dropped on 2026-08-26. Everything being compared now is a
    // matting or depth model an order of magnitude too big to ship, so it arrives by push
    // and Catalogue() picks it up. `canny` is built in (no .tflite).
    [SerializeField] string[] modelFiles =
    {
        "coral_deeplabv3_mnv2_pascal_quant.tflite",  // 513 uint8 PASCAL — the one that found the bottle
        CannyModel,
    };

    [SerializeField] SegBackend backend = SegBackend.Cpu;
    [SerializeField] int minVotePixels = 50;
    [SerializeField] float maxOcclusionDistance = 12f;
    [SerializeField] bool debugTint;
    [SerializeField] float inferIntervalSeconds = 0.2f;

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
    bool _normOverridden;
    int _depthW, _depthH;
    bool[] _isThing = BuildPascalThing();
    string[] _classNames = PascalNames;

    int _camW, _camH;
    // The mask texture covers the WHOLE camera frame in UV, so the shader can keep sampling
    // it with the same coordinates as the camera texture. The square inference result lands
    // inside it at a 1:1 pixel offset, which is why the mask is frame-shaped and not square.
    int _maskW, _maskH, _offX, _offY;
    float _inferTimer;
    float _maskPeriodMs = -1f;
    float _lastMaskAt = -1f;
    const int StageHistCap = 32;
    readonly float[] _fillHist = new float[StageHistCap];
    readonly float[] _runHist = new float[StageHistCap];
    readonly float[] _decodeHist = new float[StageHistCap];
    readonly float[] _periodHist = new float[StageHistCap];
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

    public bool Enabled
    {
        get => enableOnStart;
        set
        {
            enableOnStart = value;
            debugTint = value;
            ApplyMaterialFlags();
            if (value && _npu.Ready) SubmitFrame();
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
            else
            {
                inputMean = 123.7f;   // ImageNet — u2net, pidnet, midas, depth anything
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
    public float MaskPeriodMs => _maskPeriodMs;
    public SegBackend Backend => backend;

    public string SetInferInterval(float seconds)
    {
        inferIntervalSeconds = Mathf.Max(0f, seconds);
        return $"segint {inferIntervalSeconds:F3} s" +
               (inferIntervalSeconds <= 0f ? " (submit as soon as the worker is free)" : "");
    }

    bool IsCanny =>
        string.Equals(modelFile, CannyModel, StringComparison.OrdinalIgnoreCase);

    public string SetBackend(SegBackend next)
    {
        backend = next;
        if (!IsCanny && _modelBytes == null && string.IsNullOrEmpty(_deviceModelPath))
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
        : b == SegBackend.GpuDec ? "GPUDEC"
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
        return f.Contains("1024") || f.Contains("isnet") || f.Contains("pidnet");
    }

    public bool UseXnnpack
    {
        get => useXnnpack;
        set
        {
            useXnnpack = value;
            if (!IsCanny && (_modelBytes != null || !string.IsNullOrEmpty(_deviceModelPath)) && !_reloading)
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
        // A segnorm override belongs to the model it was typed for, not to every model after.
        _normOverridden = false;
        _maskW = _maskH = 0;
        _loadNote = $"loading {modelFile}";
        if (!_reloading) StartCoroutine(LoadModel());
        return $"segmodel {modelFile} — loading";
    }

    /// <summary>
    /// Everything that can be cycled: the models shipped in the APK, plus any .tflite
    /// pushed to the device since. A pushed file joins the cycle without a rebuild.
    /// </summary>
    List<string> Catalogue()
    {
        _catalogue.Clear();
        if (modelFiles != null)
            foreach (var m in modelFiles)
                if (!string.IsNullOrWhiteSpace(m) && !_catalogue.Contains(m))
                    _catalogue.Add(m.Trim());
        if (!_catalogue.Contains(CannyModel))
            _catalogue.Add(CannyModel);

        try
        {
            foreach (var f in Directory.GetFiles(Application.persistentDataPath, "*.tflite"))
            {
                string n = Path.GetFileName(f);
                if (IsRetired(n)) continue;
                // IS-Net is 176 MB and 80 s/frame. Keep it off the cycle even if the
                // file is still sitting on the device from the one-shot look.
                if (new FileInfo(f).Length > 100L * 1024 * 1024) continue;
                if (!_catalogue.Contains(n)) _catalogue.Add(n);
            }
        }
        catch (Exception)
        {
            // No device copies is normal, not an error.
        }

        if (!string.IsNullOrEmpty(modelFile) && IndexInCatalogue(_catalogue, modelFile) < 0)
            _catalogue.Insert(0, modelFile);
        return _catalogue;
    }

    public string CycleModel()
    {
        var all = Catalogue();
        if (all.Count == 0) return "seg: no models";
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
        if (f.Contains("u2net")) return "u2net";
        if (f.Contains("modnet")) return "modnet";
        if (f.Contains("coral") || f.Contains("deeplab")) return "deeplab";
        if (f.Contains("isnet") || f.Contains("dis")) return "isnet";
        if (f.Contains("pidnet")) return "pidnet";
        if (f.Contains("midas") || f.Contains("dpt") || f.Contains("depth")) return "depth";
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
                $"{_npu.Ep}\n" +
                $"  fill {_npu.LastFillMs:F0} run {_npu.LastRunMs:F0} dec {_npu.LastDecodeMs:F0} " +
                $"per {(_maskPeriodMs < 0f ? "—" : $"{_maskPeriodMs:F0}ms")} int {inferIntervalSeconds:F2}s\n" +
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
            r.AppendLine($"seg interval       : {inferIntervalSeconds:F3} s");
            r.AppendLine($"seg inference      : {_npu.LastInferenceMs:F2} ms (fill+run+decode)");
            r.AppendLine($"seg fill           : {_npu.LastFillMs:F2} ms" + StageP50("fill", _fillHist));
            r.AppendLine($"seg run            : {_npu.LastRunMs:F2} ms" + StageP50("run", _runHist));
            r.AppendLine($"seg decode         : {_npu.LastDecodeMs:F2} ms" + StageP50("decode", _decodeHist));
            r.AppendLine($"seg mask period    : " +
                         (_maskPeriodMs < 0f ? "n/a" : $"{_maskPeriodMs:F0} ms") +
                         StageP50("period", _periodHist));
            r.AppendLine($"seg input          : {_npu.InputWidth}x{_npu.InputHeight}");
            r.AppendLine($"seg convert        : {_fitW}x{_fitH}" +
                         (_fitW == _npu.InputWidth && _fitH == _npu.InputHeight
                             ? " native, no rescale"
                             : $" then UPSCALED to {_npu.InputWidth}x{_npu.InputHeight} " +
                               "(camera cannot supply the tensor size)"));
            r.AppendLine($"seg camera image   : {_camW}x{_camH} " +
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
        // The scene still stores the DeepLab 257 filename from before that model was
        // dropped. A [SerializeField] default does not overwrite a value the scene has
        // already saved, so without this the load looks for a file that is no longer
        // in the APK and the HUD sits on "could not read".
        if (IsRetired(modelFile)) modelFile = DefaultModelFile;
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
        if (_background != null)
        {
            _background.customMaterial = _mat;
            _background.useCustomMaterial = true;
        }

        ApplyMaterialFlags();
        StartCoroutine(LoadModel());
    }

    void OnDestroy()
    {
        _npu.Dispose();
        if (_maskTex != null) Destroy(_maskTex);
        if (_mat != null) Destroy(_mat);
    }

    void Update()
    {
        if (!enableOnStart || !_npu.Ready || _reloading) return;

        CollectResult();

        _inferTimer += Time.deltaTime;
        if (_inferTimer < inferIntervalSeconds) return;
        // Only reset the timer once a frame is actually accepted, so a model slower than
        // the interval submits again the moment the worker frees up instead of waiting.
        if (_npu.Busy) return;
        _inferTimer = 0f;
        SubmitFrame();
    }

    IEnumerator LoadModel()
    {
        _reloading = true;
        // Closing the interpreter while the worker is inside interpreter.run is a native
        // SIGSEGV. Wait until the current job finishes, then tear it down.
        while (_npu.Busy) yield return null;

        string want = modelFile;
        string devicePath = Path.Combine(Application.persistentDataPath, want);
        _deviceModelPath = null;
        _modelBytes = null;

        if (IsCanny)
        {
            _loadNote = "canny 480² CPU (no tflite)";
            yield return BindInterpreter();
            yield break;
        }

        if (File.Exists(devicePath))
        {
            _deviceModelPath = devicePath;
            _loadNote = $"device {want} {new FileInfo(devicePath).Length} bytes (mmap)";
        }
        else
        {
            string path = $"{Application.streamingAssetsPath}/{want}";
            string url = path.Contains("://") ? path : $"file://{path}";
            using var req = UnityWebRequest.Get(url);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                _loadNote = $"could not read {url}: {req.error}";
                Debug.LogWarning("[Seg] " + _loadNote);
                _reloading = false;
                yield break;
            }
            _modelBytes = req.downloadHandler.data;
            _loadNote = $"apk {want} {_modelBytes.Length} bytes";
        }

        yield return BindInterpreter();
    }

    IEnumerator ReloadBackend()
    {
        _reloading = true;
        while (_npu.Busy) yield return null;
        yield return BindInterpreter();
        if (enableOnStart && _npu.Ready) SubmitFrame();
    }

    bool TryLoad(string backendArg)
    {
        if (IsCanny) return _npu.LoadCanny();
        if (!string.IsNullOrEmpty(_deviceModelPath))
            return _npu.LoadFile(_deviceModelPath, backendArg);
        return _npu.Load(_modelBytes, backendArg);
    }

    IEnumerator BindInterpreter()
    {
        _reloading = true;
        bool ok = TryLoad(BackendArg(backend));
        if (ok && IsCanny)
        {
            FinishBind($"CPU {_npu.Ep} {_npu.InputWidth}x{_npu.InputHeight} (no tflite)");
            yield break;
        }

        // ENN and the GPU delegate each refuse graphs the CPU takes without complaint, and
        // the HUD no longer has a backend button to escape with — so a rejected model would
        // otherwise be a dead end. Fall back, and say so rather than hiding it.
        if (!ok && backend != SegBackend.Cpu)
        {
            string refused = _npu.LastError;
            Debug.LogWarning($"[Seg] {LabelOf(backend)} refused {modelFile}. {refused}");
            backend = SegBackend.Cpu;
            ok = TryLoad(BackendArg(backend));
            if (ok)
            {
                FinishBind($"CPU {_npu.Ep} {_npu.InputWidth}x{_npu.InputHeight} " +
                           $"(fell back, NPU/GPU refused: {refused})");
                yield break;
            }
        }

        if (!ok)
        {
            _loadNote = $"{LabelOf(backend)} REJECT {_npu.LastError}";
            ApplyMaterialFlags();
            Debug.LogWarning($"[Seg] {LabelOf(backend)} refused the graph. {_npu.LastError}");
            _reloading = false;
            yield break;
        }

        FinishBind($"{LabelOf(backend)} {_npu.Ep} {_npu.InputWidth}x{_npu.InputHeight} -> {_npu.OutputWidth}x{_npu.OutputHeight}");
        yield return null;
    }

    void FinishBind(string note)
    {
        ApplyModelDefaults();
        Allocate(_npu.OutputWidth, _npu.OutputHeight);
        ConfigureThingTable(_npu.OutputChannels);
        _loadNote = note;
        Debug.Log($"[Seg] loaded: {_loadNote}");
        ApplyMaterialFlags();
        _reloading = false;
        _stageCount = 0;
        _stageI = 0;
        _maskPeriodMs = -1f;
        _lastMaskAt = -1f;
    }

    void RecordStages()
    {
        _fillHist[_stageI] = _npu.LastFillMs;
        _runHist[_stageI] = _npu.LastRunMs;
        _decodeHist[_stageI] = _npu.LastDecodeMs;
        _periodHist[_stageI] = _maskPeriodMs;
        _stageI = (_stageI + 1) % StageHistCap;
        if (_stageCount < StageHistCap) _stageCount++;
    }

    string StageP50(string _, float[] hist)
    {
        if (_stageCount < 3) return "";
        return $"  p50 {Percentile(hist, _stageCount, 0.5f):F1} ms n={_stageCount}";
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
    /// The mask texture spans the whole camera frame so its UV matches the camera texture's
    /// and the shader needs no remapping. The square inference result is inset into it at a
    /// 1:1 pixel ratio, chosen so a centre crop loses no mask resolution.
    /// </summary>
    void EnsureMaskGeometry()
    {
        int outW = _npu.OutputWidth, outH = _npu.OutputHeight;
        int mw = outW, mh = outH;

        if (centreCrop && _camW > 0 && _camH > 0)
        {
            int side = Mathf.Min(_camW, _camH);
            mw = Mathf.Max(outW, Mathf.RoundToInt(outW * (float)_camW / side));
            mh = Mathf.Max(outH, Mathf.RoundToInt(outH * (float)_camH / side));
        }

        if (mw == _maskW && mh == _maskH && _maskTex != null) return;

        _maskW = mw;
        _maskH = mh;
        _offX = (mw - outW) / 2;
        _offY = (mh - outH) / 2;
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
    /// Grabs a camera frame and queues it. Returns without blocking, so frame time no
    /// longer includes inference.
    /// </summary>
    void SubmitFrame()
    {
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
            try
            {
                image.Convert(conv, handle);
                NativeArray<byte>.Copy(handle, _rgbFit, size);
            }
            catch (Exception e)
            {
                _loadNote = $"convert {_fitW}x{_fitH} failed: {e.Message}";
                return;
            }
            finally
            {
                handle.Dispose();
            }
        }

        int need = inW * inH * 3;
        if (_rgb == null || _rgb.Length < need) _rgb = new byte[need];
        if (_fitW == inW && _fitH == inH)
            Array.Copy(_rgbFit, _rgb, need);
        else
            ResizeRgb(_rgbFit, _fitW, _fitH, _rgb, inW, inH);

        EnsureMaskGeometry();

        if (!_npu.Submit(Upright(_rgb)) && !string.IsNullOrEmpty(_npu.LastError))
            _loadNote = $"submit failed: {_npu.LastError}";
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
        float now = Time.realtimeSinceStartup;
        if (_lastMaskAt >= 0f)
            _maskPeriodMs = (now - _lastMaskAt) * 1000f;
        _lastMaskAt = now;
        RecordStages();
        // Coral keeps the thing/stuff vote. Mattes (MODNet and friends) now pack a
        // depth into alpha so the silhouette occludes; inverse-depth maps stay tint-only
        // because their range is not metres.
        if (_npu.ScalarOutput || IsCanny) PaintScalarView();
        else if (_npu.OutputChannels == 19) PaintClassView();
        else
        {
            GrabDepth();
            VoteAndExpand();
        }
        if (_maskTex == null) return;
        _maskTex.LoadRawTextureData(_overlay);
        _maskTex.Apply(false, false);
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
            || f == "dis_isnet_1024.tflite"
            || f.StartsWith("mediapipe_selfie");
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
