using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Device tiering (Step 13). In AR, frame rate *consistency* matters more than the number —
/// judder makes the model swim relative to the real world, which destroys registration far
/// more than low fps does. A locked 30 looks more convincing than a fluctuating 40–55.
///
/// Drops immediately when a 5 s window is over budget. Recovers C → B → A only after the
/// phone has been comfortably under budget for several windows, and not until a hold after
/// the last drop has elapsed — otherwise heat/cool oscillation flips quality mid-session.
///
/// SetQualityLevel alone is not enough: GraphicsSettings keeps Mobile_RPAsset as the default
/// pipeline, which URP actually renders with. Each apply also assigns that level's
/// TierA/B/C_RPAsset or the switch is cosmetic (legacy shadowDistance, nothing on screen).
/// </summary>
public class AdaptiveQuality : MonoBehaviour
{
    [Tooltip("-1 = auto-guess from device specs. 0 = Tier A, 1 = Tier B, 2 = Tier C.")]
    [SerializeField] int startTier = -1;

    [SerializeField] int targetFrameRate = 30;

    [Tooltip("ARCore's initial VPS localization is a genuine spike — don't tier down for it.")]
    [SerializeField] float warmupSeconds = 10f;

    [Tooltip("Average frame time above this, over one window, drops a tier.")]
    [SerializeField] float dropBudgetMs = 36f;

    [Tooltip("Must sit below this for recoverWindows consecutive windows before climbing.")]
    [SerializeField] float recoverBudgetMs = 28f;

    [Tooltip("Healthy windows required before a recover. 3 × 5 s = 15 s of headroom.")]
    [SerializeField] int recoverWindows = 3;

    [Tooltip("After a drop, ignore recoveries for this long. Lets the SoC cool without bouncing.")]
    [SerializeField] float holdAfterDropSeconds = 30f;

    const float Window = 5f;
    const int LowestTier = 2;

    int _tier;
    float _accum;
    int _samples;
    float _warmup;
    int _startTier;
    int _drops;
    int _recovers;
    int _recoverStreak;
    float _lastDropAt = -999f;
    float _emaMs;
    bool _auto = true;

    UniversalRenderPipelineAsset _appliedUrp;
    RenderPipelineAsset _stockDefaultPipeline;
    readonly Dictionary<UniversalRenderPipelineAsset, float> _stockShadow =
        new Dictionary<UniversalRenderPipelineAsset, float>();

    public int CurrentTier => _tier;
    public bool AutoEnabled => _auto;

    public string DebugReadout
    {
        get
        {
            bool warming = _warmup < warmupSeconds;
            string timing = warming
                ? $"{_emaMs:F1} ms (warmup {warmupSeconds - _warmup:F0}s)"
                : _auto
                    ? $"{_emaMs:F1} ms / {dropBudgetMs:F0} drop / {recoverBudgetMs:F0} recover"
                    : $"{_emaMs:F1} ms";

            string history;
            if (!_auto)
                history = "FIXED";
            else if (_tier == _startTier && _drops == 0 && _recovers == 0)
                history = $"start {Letter(_startTier)} ({(startTier >= 0 ? "forced" : "auto")})";
            else
                history = $"from {Letter(_startTier)}, dropped {_drops}x, recovered {_recovers}x";
            if (_auto && _tier >= LowestTier) history += ", at floor";
            if (_auto && _tier <= 0 && (_drops > 0 || _recovers > 0)) history += ", at ceiling";

            var urp = CurrentUrp();
            string shadows = urp != null
                ? $"shadows {urp.shadowDistance:F0} m ({urp.name}, {urp.shadowCascadeCount} casc)"
                : $"shadows {QualitySettings.shadowDistance:F0} m (no URP asset)";

            string mode = _auto ? "AUTO" : "FIXED";
            return $"quality: {mode} {Letter(_tier)}  {timing}\n" +
                   $"  {shadows} - {history}";
        }
    }

    /// <summary>Capture / remote dump. Same numbers as the HUD line, plus the hold state.</summary>
    public string StateReport =>
        DebugReadout + "\n" +
        $"  auto {(_auto ? "on" : "OFF (FIXED)")}  quality level {QualitySettings.GetQualityLevel()}\n" +
        $"  pipeline {(CurrentUrp() != null ? CurrentUrp().name : "(none)")}";

    static char Letter(int tier) => (char)('A' + Mathf.Clamp(tier, 0, 25));

    /// <summary>
    /// Shadow distance to hold regardless of tier, in metres. 0 leaves the tier's own value.
    ///
    /// A tier's shadow distance is sized for a real building tens of metres away. The preview
    /// miniature is 0.4 m across, and Tier C spreads a 512-pixel shadow map over 60 m — 11.7 cm
    /// per texel, wider than the miniature is TALL. Its shadow rounds away to nothing, and the
    /// 1-texel normal bias finishes the job, so preview shows no shadow at any sun angle. That
    /// reads as broken lighting rather than as a resolution limit, which is why this exists.
    ///
    /// Written onto the live URP asset, not QualitySettings.shadowDistance — URP ignores that.
    /// Re-applied after every tier change: a drop mid-session would otherwise silently restore
    /// the 60 m value and take the shadow with it.
    /// </summary>
    public float ShadowDistanceOverride
    {
        get => _shadowDistanceOverride;
        set { _shadowDistanceOverride = value; ApplyShadowDistance(); }
    }

    float _shadowDistanceOverride;

    void Start()
    {
        Application.targetFrameRate = targetFrameRate;
        _stockDefaultPipeline = GraphicsSettings.defaultRenderPipeline;

        _tier = startTier >= 0 ? startTier : GuessTier();
        _tier = Mathf.Clamp(_tier, 0, LowestTier);
        _startTier = _tier;
        ApplyTier("start");
    }

    void OnDisable()
    {
        // Runtime mutation of a URP asset / GraphicsSettings is visible in the Editor after
        // Play stops unless we put the stock values back.
        foreach (var kv in _stockShadow)
        {
            if (kv.Key != null)
                kv.Key.shadowDistance = kv.Value;
        }
        if (_stockDefaultPipeline != null)
            GraphicsSettings.defaultRenderPipeline = _stockDefaultPipeline;
        _appliedUrp = null;
    }

    static int GuessTier()
    {
        // Crude but adequate as a starting point — measurement corrects it below.
        // A device-name allowlist is a treadmill and is wrong for phones released after ship.
        int mem = SystemInfo.systemMemorySize;   // MB
        int cores = SystemInfo.processorCount;

        if (mem >= 7000 && cores >= 8) return 0;   // Tier A
        if (mem >= 4000) return 1;                 // Tier B
        return LowestTier;                         // Tier C
    }

    void Update()
    {
        // Smoothed outside the warmup gate so the HUD shows a frame time from the first
        // second, not a zero until warmup ends.
        float ms = Time.unscaledDeltaTime * 1000f;
        _emaMs = _emaMs <= 0f ? ms : Mathf.Lerp(_emaMs, ms, 0.05f);

        if (_warmup < warmupSeconds)
        {
            _warmup += Time.unscaledDeltaTime;
            return;
        }

        if (!_auto) return;

        _accum += Time.unscaledDeltaTime * 1000f;
        _samples++;
        if (_accum < Window * 1000f) return;

        float avgMs = _accum / _samples;
        _accum = 0f;
        _samples = 0;

        if (avgMs > dropBudgetMs && _tier < LowestTier)
        {
            _recoverStreak = 0;
            _tier++;
            _drops++;
            _lastDropAt = Time.unscaledTime;
            ApplyTier($"drop avg {avgMs:F1} ms");
            return;
        }

        bool cooled = Time.unscaledTime - _lastDropAt >= holdAfterDropSeconds;
        if (avgMs < recoverBudgetMs && _tier > 0 && cooled)
        {
            _recoverStreak++;
            if (_recoverStreak >= recoverWindows)
            {
                _recoverStreak = 0;
                _tier--;
                _recovers++;
                ApplyTier($"recover avg {avgMs:F1} ms");
            }
        }
        else
        {
            _recoverStreak = 0;
        }
    }

    /// <summary>
    /// Pin a tier and stop auto. <c>quality auto</c> turns measurement back on from here.
    /// </summary>
    public string ForceTier(int tier)
    {
        _auto = false;
        _recoverStreak = 0;
        int next = Mathf.Clamp(tier, 0, LowestTier);
        if (next == _tier)
        {
            ApplyTier("force (already there)");
            return $"held at {Letter(_tier)}";
        }
        _tier = next;
        ApplyTier("force");
        return $"held at {Letter(_tier)}";
    }

    public string ResumeAuto()
    {
        _auto = true;
        _recoverStreak = 0;
        return $"auto on, tier {Letter(_tier)}";
    }

    void ApplyTier(string reason)
    {
        QualitySettings.SetQualityLevel(_tier, true);

        // QualitySettings.customRenderPipeline is bound to TierA/B/C_RPAsset, but the pipeline
        // URP actually draws with is GraphicsSettings.defaultRenderPipeline, which this project
        // leaves on Mobile_RPAsset. Assigning both is what makes a recover change shadows.
        var asset = QualitySettings.GetRenderPipelineAssetAt(_tier);
        if (asset != null)
        {
            QualitySettings.renderPipeline = asset;
            GraphicsSettings.defaultRenderPipeline = asset;
        }

        ApplyShadowDistance();

        var urp = CurrentUrp();
        string pipe = urp != null
            ? $"{urp.name} shadows {urp.shadowDistance:F0} m casc {urp.shadowCascadeCount}"
            : "no URP asset";
        Debug.Log($"[Quality] tier {Letter(_tier)} ({reason}): {pipe}");
    }

    void ApplyShadowDistance()
    {
        var urp = CurrentUrp();
        if (urp == null) return;

        if (_appliedUrp != null && _appliedUrp != urp &&
            _stockShadow.TryGetValue(_appliedUrp, out float previousStock))
            _appliedUrp.shadowDistance = previousStock;

        if (!_stockShadow.ContainsKey(urp))
            _stockShadow[urp] = urp.shadowDistance;

        urp.shadowDistance = _shadowDistanceOverride > 0f
            ? _shadowDistanceOverride
            : _stockShadow[urp];
        _appliedUrp = urp;

        // Keep the legacy field in step so the HUD number is not a lie if something still reads it.
        QualitySettings.shadowDistance = urp.shadowDistance;
    }

    static UniversalRenderPipelineAsset CurrentUrp() =>
        GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
}
