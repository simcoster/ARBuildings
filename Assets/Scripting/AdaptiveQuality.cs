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

    public int CurrentTier => _tier;

    void Start()
    {
        Application.targetFrameRate = targetFrameRate;

        _tier = startTier >= 0 ? startTier : GuessTier();
        _tier = Mathf.Clamp(_tier, 0, LowestTier);
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
            QualitySettings.SetQualityLevel(_tier, true);
            Debug.Log($"Dropped to quality tier {_tier} (avg {avgMs:F1} ms)");
        }
    }
}
