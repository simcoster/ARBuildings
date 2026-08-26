using System;
using System.Collections;
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
    public const string DefaultModelFile = "deeplabv3_257_mv_gpu.tflite";

    [SerializeField] bool enableOnStart;
    [SerializeField] string modelFile = DefaultModelFile;
    [SerializeField] int minVotePixels = 50;
    [SerializeField] float maxOcclusionDistance = 12f;
    [SerializeField] bool debugTint;
    [SerializeField] float inferIntervalSeconds = 0.2f;

    ARCameraManager _camera;
    AROcclusionManager _occlusion;
    ARCameraBackground _background;
    Material _mat;
    Texture2D _maskTex;
    NpuSegmenterClient _npu = new NpuSegmenterClient();

    byte[] _rgb;
    int[] _labels;
    byte[] _overlay;
    int[] _parent;
    int[] _votes;
    int[] _counts;
    float[] _depthM;
    int _depthW, _depthH;
    bool[] _isThing = BuildPascalThing();

    float _inferTimer;
    string _loadNote = "not loaded";
    int _lastThingPixels;
    int _lastStuffPixels;
    int _lastExpanded;
    int _lastComponents;

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
            if (value && _npu.Ready) InferOnce();
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

    public bool NpuReady => _npu.Ready;
    public float LastInferenceMs => _npu.LastInferenceMs;
    public float InferIntervalSeconds => inferIntervalSeconds;

    public string HudReadout =>
        $"seg: {(enableOnStart ? "ON" : "OFF")} overlay {(debugTint ? "ON" : "off")} " +
        $"{_npu.Ep} {_npu.LastInferenceMs:F1}ms\n" +
        $"  {_loadNote}";

    public string StateReport
    {
        get
        {
            var r = new StringBuilder();
            r.AppendLine($"seg occlusion      : {(enableOnStart ? "ON" : "OFF")}");
            r.AppendLine($"seg model          : {modelFile}");
            r.AppendLine($"seg EP             : {_npu.Ep}");
            r.AppendLine($"seg ready          : {_npu.Ready}");
            r.AppendLine($"seg load           : {_loadNote}");
            r.AppendLine($"seg last error     : {_npu.LastError}");
            r.AppendLine($"seg inference      : {_npu.LastInferenceMs:F2} ms");
            r.AppendLine($"seg input          : {_npu.InputWidth}x{_npu.InputHeight}");
            r.AppendLine($"seg labels         : {_npu.OutputWidth}x{_npu.OutputHeight} c={_npu.OutputChannels}");
            r.AppendLine($"segmin             : {minVotePixels} px");
            r.AppendLine($"seg max distance   : {maxOcclusionDistance:F1} m");
            r.AppendLine($"seg debug          : {(debugTint ? "ON" : "OFF")}");
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
        if (!enableOnStart || !_npu.Ready) return;
        _inferTimer += Time.deltaTime;
        if (_inferTimer < inferIntervalSeconds) return;
        _inferTimer = 0f;
        InferOnce();
    }

    IEnumerator LoadModel()
    {
        string devicePath = Path.Combine(Application.persistentDataPath, modelFile);
        byte[] bytes = null;

        if (File.Exists(devicePath))
        {
            bytes = File.ReadAllBytes(devicePath);
            _loadNote = $"device {modelFile} {bytes.Length} bytes";
        }
        else
        {
            string path = $"{Application.streamingAssetsPath}/{modelFile}";
            string url = path.Contains("://") ? path : $"file://{path}";
            using var req = UnityWebRequest.Get(url);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                _loadNote = $"could not read {url}: {req.error}";
                Debug.LogWarning("[Seg] " + _loadNote);
                yield break;
            }
            bytes = req.downloadHandler.data;
            _loadNote = $"apk {modelFile} {bytes.Length} bytes";
        }

        bool ok = _npu.Load(bytes, npuOnly: true);
        if (!ok)
        {
            _loadNote = $"ENN REJECT {_npu.LastError}";
            enableOnStart = false;
            ApplyMaterialFlags();
            Debug.LogWarning($"[Seg] NPU refused the graph — leaving depth unmasked. {_npu.LastError}");
            yield break;
        }

        Allocate(_npu.OutputWidth, _npu.OutputHeight);
        ConfigureThingTable(_npu.OutputChannels);
        _loadNote = $"enn {_npu.InputWidth}x{_npu.InputHeight} -> {_npu.OutputWidth}x{_npu.OutputHeight}";
        Debug.Log($"[Seg] loaded on ENN: {_loadNote}");
        ApplyMaterialFlags();
    }

    void Allocate(int w, int h)
    {
        _labels = new int[w * h];
        _overlay = new byte[w * h * 4];
        _parent = new int[w * h];
        _votes = new int[w * h];
        _counts = new int[w * h];
        _rgb = new byte[_npu.InputWidth * _npu.InputHeight * 3];
        if (_maskTex != null) Destroy(_maskTex);
        _maskTex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        if (_mat != null) _mat.SetTexture(IdMask, _maskTex);
    }

    void InferOnce()
    {
        if (_camera == null || !_camera.TryAcquireLatestCpuImage(out var image))
            return;

        using (image)
        {
            var conv = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(_npu.InputWidth, _npu.InputHeight),
                outputFormat = TextureFormat.RGB24,
                transformation = XRCpuImage.Transformation.None
            };
            int size = image.GetConvertedDataSize(conv);
            if (_rgb == null || _rgb.Length < size) _rgb = new byte[size];
            var handle = new NativeArray<byte>(size, Allocator.Temp);
            try
            {
                image.Convert(conv, handle);
                NativeArray<byte>.Copy(handle, _rgb, size);
            }
            finally
            {
                handle.Dispose();
            }
        }

        if (!_npu.Infer(_rgb, _labels))
        {
            _loadNote = $"infer failed: {_npu.LastError}";
            return;
        }

        GrabDepth();
        VoteAndExpand();
        _maskTex.LoadRawTextureData(_overlay);
        _maskTex.Apply(false, false);
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

    void VoteAndExpand()
    {
        int w = _npu.OutputWidth;
        int h = _npu.OutputHeight;
        int n = w * h;
        Array.Clear(_overlay, 0, _overlay.Length);
        Array.Clear(_votes, 0, n);
        Array.Clear(_counts, 0, n);
        _lastThingPixels = _lastStuffPixels = _lastExpanded = _lastComponents = 0;

        for (int i = 0; i < n; i++)
        {
            _parent[i] = i;
            int cls = _labels[i];
            bool thing = cls >= 0 && cls < _isThing.Length && _isThing[cls];
            if (thing) _lastThingPixels++;
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
                _counts[root]++;
                if (PixelVotes(x, y, w, h)) _votes[root]++;
            }
        }

        var seen = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!IsThing(_labels[i])) continue;
            int root = Find(i);
            if (!seen[root])
            {
                seen[root] = true;
                _lastComponents++;
                if (_votes[root] >= minVotePixels) _lastExpanded++;
            }
            Color32 paint = ColorForClass(_labels[i]);
            int o = i * 4;
            _overlay[o] = paint.r;
            _overlay[o + 1] = paint.g;
            _overlay[o + 2] = paint.b;
            _overlay[o + 3] = _votes[root] >= minVotePixels ? (byte)255 : (byte)0;
        }
    }

    bool PixelVotes(int x, int y, int w, int h)
    {
        if (_depthW <= 0) return false;
        float u = (x + 0.5f) / w;
        float v = (y + 0.5f) / h;
        int dx = Mathf.Clamp((int)(u * _depthW), 0, _depthW - 1);
        int dy = Mathf.Clamp((int)(v * _depthH), 0, _depthH - 1);
        float metres = _depthM[dy * _depthW + dx];
        if (metres <= 0f) return false;
        if (maxOcclusionDistance > 0f && metres >= maxOcclusionDistance) return false;
        return true;
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
        _isThing = channels == 19 ? BuildCityscapesThing() : BuildPascalThing();
    }

    /// <summary>PASCAL VOC: only countable objects. Background (0) is plaza/road/building/sky.</summary>
    static bool[] BuildPascalThing()
    {
        var t = new bool[256];
        foreach (int id in new[] { 1, 2, 4, 6, 7, 14, 15, 19 })
            t[id] = true;
        return t;
    }

    static Color32 ColorForClass(int cls)
    {
        // PASCAL VOC thing colours. Floor / plaza / sky stay unpainted (class 0).
        switch (cls)
        {
            case 15: return new Color32(255, 50, 180, 255);  // person
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

    /// <summary>Cityscapes 19-class trainIds. Road/sidewalk/terrain/sky/building never occlude.</summary>
    static bool[] BuildCityscapesThing()
    {
        var t = new bool[256];
        foreach (int id in new[] { 5, 6, 7, 11, 12, 13, 14, 15, 16, 17, 18 })
            t[id] = true;
        return t;
    }
}
