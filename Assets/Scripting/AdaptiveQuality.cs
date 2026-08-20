using UnityEngine;

/// <summary>
/// Device tiering (Step 13). In AR, frame rate *consistency* matters more than the number —
/// judder makes the model swim relative to the real world, which destroys registration far
/// more than low fps does. A locked 30 looks more convincing than a fluctuating 40–55.
/// </summary>
public class AdaptiveQuality : MonoBehaviour
{
    [Tooltip("-1 = auto-guess from device specs. 0 = Tier A, 1 = Tier B, 2 = Tier C.")]
    [SerializeField] int startTier = -1;

    [SerializeField] int targetFrameRate = 30;

    [Tooltip("ARCore's initial VPS localization is a genuine spike — don't tier down for it.")]
    [SerializeField] float warmupSeconds = 10f;

    const float Window = 5f;      // seconds per evaluation
    const float BudgetMs = 36f;   // 30fps + headroom
    const int LowestTier = 2;

    int _tier;
    float _accum;
    int _samples;
    float _warmup;
    int _startTier;
    int _drops;
    float _emaMs;

    public int CurrentTier => _tier;

    /// <summary>
    /// HUD line. Shows the tier as a letter, the smoothed frame time against the budget
    /// that drives tiering, and the shadow distance the tier implies — Tier C drops it to
    /// 60 m, so a building further away silently stops casting and the tier is the reason.
    /// </summary>
    public string DebugReadout
    {
        get
        {
            bool warming = _warmup < warmupSeconds;
            string timing = warming
                ? $"{_emaMs:F1} ms (warmup {warmupSeconds - _warmup:F0}s)"
                : $"{_emaMs:F1} ms / {BudgetMs:F0} budget";

            string history = _tier == _startTier
                ? $"start {Letter(_startTier)} ({(startTier >= 0 ? "forced" : "auto")})"
                : $"from {Letter(_startTier)}, dropped {_drops}x";
            if (_tier >= LowestTier) history += ", at floor";

            return $"quality: tier {Letter(_tier)}  {timing}\n" +
                   $"  shadows {QualitySettings.shadowDistance:F0} m - {history}";
        }
    }

    static char Letter(int tier) => (char)('A' + tier);

    void Start()
    {
        Application.targetFrameRate = targetFrameRate;

        _tier = startTier >= 0 ? startTier : GuessTier();
        _tier = Mathf.Clamp(_tier, 0, LowestTier);
        _startTier = _tier;
        QualitySettings.SetQualityLevel(_tier, true);
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

        _accum += Time.unscaledDeltaTime * 1000f;
        _samples++;
        if (_accum < Window * 1000f) return;

        float avgMs = _accum / _samples;
        _accum = 0f;
        _samples = 0;

        // Step DOWN only. As the phone heats performance degrades; as it cools it recovers,
        // so a bidirectional controller oscillates. A visible quality flip mid-session is
        // worse than just running at the lower tier. Pick a floor and stay.
        if (avgMs > BudgetMs && _tier < LowestTier)
        {
            _tier++;
            _drops++;
            QualitySettings.SetQualityLevel(_tier, true);
            Debug.Log($"Dropped to quality tier {_tier} ({Letter(_tier)}) (avg {avgMs:F1} ms)");
        }
    }
}
