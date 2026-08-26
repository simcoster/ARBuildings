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
    public int InputWidth { get; private set; }
    public int InputHeight { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public int OutputChannels { get; private set; }
    public bool Ready { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject _java;
#endif

    public bool Load(byte[] modelBytes, bool npuOnly)
    {
        Dispose();
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _java = new AndroidJavaObject("com.pavel.arbuildings.NpuSegmenter");
            bool ok = _java.Call<bool>("loadBytes", modelBytes, npuOnly);
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
            Ready = true;
            return true;
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

    public bool Infer(byte[] rgb, int[] labelsOut)
    {
        if (!Ready) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            bool ok = _java.Call<bool>("infer", rgb, labelsOut);
            LastInferenceMs = _java.Call<float>("lastInferenceMs");
            if (!ok) LastError = _java.Call<string>("lastError") ?? "infer failed";
            return ok;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            return false;
        }
#else
        return false;
#endif
    }

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
