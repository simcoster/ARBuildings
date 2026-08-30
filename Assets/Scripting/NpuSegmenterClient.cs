using System;
using UnityEngine;

/// <summary>
/// JNI front for <c>com.pavel.arbuildings.NpuSegmenter</c>. Exists only so the rest of
/// the C# can talk about "load a model, get a label map" without AndroidJavaObject
/// leaking into the fusion code. Editor and non-Android builds report REJECT.
/// </summary>
public sealed class NpuSegmenterClient : IDisposable
{
    public string LastError { get; private set; } = "not loaded";
    public string Ep { get; private set; } = "none";
    public float LastInferenceMs { get; private set; } = -1f;
    public float LastFillMs { get; private set; } = -1f;
    public float LastRunMs { get; private set; } = -1f;
    public float LastDecodeMs { get; private set; } = -1f;
    public int InputWidth { get; private set; }
    public int InputHeight { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public int OutputChannels { get; private set; }
    public bool Ready { get; private set; }
    public string Normalization { get; private set; } = "n/a";
    public string OutputSpec { get; private set; } = "n/a";

    /// <summary>
    /// True when the label bytes are a quantised continuous value — a matte's alpha or a
    /// depth map — rather than class ids. The class tables and the thing/stuff split mean
    /// nothing in that case.
    /// </summary>
    public bool ScalarOutput { get; private set; }
    public string OutputKind { get; private set; } = "n/a";
    public string ScalarRange { get; private set; } = "n/a";

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject _java;
#endif

    public bool LoadCanny()
    {
        Dispose();
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _java = new AndroidJavaObject("com.pavel.arbuildings.NpuSegmenter");
            bool ok = _java.Call<bool>("loadCanny");
            return FinishLoad(ok);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Ep = "REJECT";
            Ready = false;
            Dispose();
            return false;
        }
#else
        LastError = "Canny segmenter is Android-only";
        Ep = "REJECT";
        Ready = false;
        return false;
#endif
    }

    public bool LoadFile(string path, string backend)
    {
        Dispose();
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _java = new AndroidJavaObject("com.pavel.arbuildings.NpuSegmenter");
            bool ok = _java.Call<bool>("loadFile", path, backend ?? "npu");
            return FinishLoad(ok);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Ep = "REJECT";
            Ready = false;
            Dispose();
            return false;
        }
#else
        LastError = "NPU segmenter is Android-only";
        Ep = "REJECT";
        Ready = false;
        return false;
#endif
    }

    bool FinishLoad(bool ok)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        LastError = _java.Call<string>("lastError") ?? "";
        Ep = _java.Call<string>("ep") ?? "REJECT";
        if (!ok)
        {
            Ready = false;
            Dispose();
            return false;
        }
        InputWidth = _java.Call<int>("inputWidth");
        InputHeight = _java.Call<int>("inputHeight");
        OutputWidth = _java.Call<int>("outputWidth");
        OutputHeight = _java.Call<int>("outputHeight");
        OutputChannels = _java.Call<int>("outputChannels");
        Normalization = _java.Call<string>("normalization") ?? "n/a";
        OutputSpec = _java.Call<string>("outputSpec") ?? "n/a";
        ScalarOutput = _java.Call<bool>("scalarOutput");
        OutputKind = _java.Call<string>("outputKind") ?? "n/a";
        Ready = true;
        return true;
#else
        return false;
#endif
    }

    public bool Load(byte[] modelBytes, string backend)
    {
        Dispose();
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _java = new AndroidJavaObject("com.pavel.arbuildings.NpuSegmenter");
            bool ok = _java.Call<bool>("loadBytes", modelBytes, backend ?? "npu");
            return FinishLoad(ok);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Ep = "REJECT";
            Ready = false;
            Dispose();
            return false;
        }
#else
        LastError = "NPU segmenter is Android-only";
        Ep = "REJECT";
        Ready = false;
        return false;
#endif
    }

    /// <summary>
    /// Queues a frame on the segmenter's worker thread and returns at once. False means a
    /// job is already in flight and this frame should be dropped. Inference used to run
    /// inline here, which made Unity's frame time equal the inference time.
    /// </summary>
    public bool Submit(byte[] rgb)
    {
        if (!Ready) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            return _java.Call<bool>("submit", rgb);
        }
        catch (Exception e)
        {
            LastError = $"submit: {e.Message}";
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>The newest finished label map, or null if none is waiting.</summary>
    public byte[] PollLabels()
    {
        if (!Ready) return null;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            var labels = _java.Call<byte[]>("pollLabels");
            if (labels == null) return null;
            PullStageTimes();
            LastError = _java.Call<string>("lastError") ?? "";
            if (ScalarOutput)
            {
                // Per frame, not per load: the range is what distinguishes a 0..1 matte from
                // a scaleless depth map, and "auto" re-decides on every inference.
                ScalarRange = _java.Call<string>("scalarRange") ?? "n/a";
                OutputKind = _java.Call<string>("outputKind") ?? "n/a";
            }
            return labels;
        }
        catch (Exception e)
        {
            LastError = $"poll: {e.Message}";
            return null;
        }
#else
        return null;
#endif
    }

    /// <summary>True while the worker is mid-inference, so submitting would be refused.</summary>
    public bool Busy
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Ready) return false;
            try { return _java.Call<bool>("busy"); } catch { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Synchronous inference; kept for one-shot diagnostics. Returns the label map, or
    /// null on failure. It has to be a return value: Unity's JNI copies a managed array
    /// into a fresh Java array on the way in and never copies it back, so an
    /// <c>int[] labelsOut</c> parameter is written on the Java side and discarded — which
    /// looks exactly like a model that labels nothing.
    /// </summary>
    public byte[] InferLabels(byte[] rgb)
    {
        if (!Ready) return null;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            var labels = _java.Call<byte[]>("inferLabels", rgb);
            PullStageTimes();
            if (labels == null) LastError = _java.Call<string>("lastError") ?? "infer failed";
            else LastError = "";
            return labels;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            return null;
        }
#else
        return null;
#endif
    }

    public void SetNormalization(float mean, float scale)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_java == null) return;
        try
        {
            _java.Call("setNormalization", mean, scale);
            Normalization = _java.Call<string>("normalization") ?? "n/a";
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
#endif
    }

    /// <summary>auto | labels | alpha | depth. See NpuSegmenter.setOutputKind.</summary>
    public void SetOutputKind(string kind)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_java == null) return;
        try
        {
            _java.Call("setOutputKind", kind);
            ScalarOutput = _java.Call<bool>("scalarOutput");
            OutputKind = _java.Call<string>("outputKind") ?? "n/a";
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    void PullStageTimes()
    {
        LastInferenceMs = _java.Call<float>("lastInferenceMs");
        LastFillMs = _java.Call<float>("fillMs");
        LastRunMs = _java.Call<float>("runMs");
        LastDecodeMs = _java.Call<float>("decodeMs");
    }
#endif

    public void Dispose()
    {
        Ready = false;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_java != null)
        {
            try { _java.Call("close"); } catch { /* already closed */ }
            _java.Dispose();
            _java = null;
        }
#endif
    }
}
