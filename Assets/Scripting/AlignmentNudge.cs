using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

/// <summary>
/// On-site manual correction (Step 9). VPS gets you close; "close" at building scale still
/// looks wrong.
///
/// The readout is the actual point — align by eye, read off the numbers, then bake them
/// into buildings.json. PlayerPrefs is a per-user refinement on top of a correct baseline,
/// not where the authoritative value lives.
/// </summary>
public class AlignmentNudge : MonoBehaviour
{
    [SerializeField] Camera arCamera;
    [SerializeField] float panMetresPerPixel = 0.02f;

    Transform _nudgeRoot;      // bound at runtime — the anchor doesn't exist at scene load
    string _siteKey;
    bool _dirty;

    Vector2 _prevA, _prevB;
    float _prevTwistAngle;
    bool _tracking;

    // Read these off your debug UI, then bake them into buildings.json.
    public Vector3 PositionOffset { get; private set; }
    public float HeadingOffset { get; private set; }
    public float HeightOffset { get; private set; }

    public string DebugReadout =>
        $"pos {PositionOffset.x:F2}, {PositionOffset.z:F2} m\n" +
        $"heading {HeadingOffset:+0.0;-0.0}°\n" +
        $"height {HeightOffset:+0.00;-0.00} m";

    void OnEnable() => EnhancedTouchSupport.Enable();

    void OnDisable()
    {
        EnhancedTouchSupport.Disable();
        if (_dirty) Save();
    }

    public void Bind(Transform root, string siteId)
    {
        _nudgeRoot = root;
        _siteKey = $"nudge_{siteId}";
        Load();
    }

    void Update()
    {
        if (_nudgeRoot == null) return;   // anchor not resolved yet — this guard matters

        var touches = Touch.activeTouches;
        if (touches.Count != 2) { _tracking = false; return; }

        Vector2 a = touches[0].screenPosition;
        Vector2 b = touches[1].screenPosition;
        float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;

        if (!_tracking)
        {
            _prevA = a; _prevB = b; _prevTwistAngle = angle;
            _tracking = true;
            return;
        }

        // twist -> heading, with a dead zone (fingers rotate during any two-finger drag)
        float dAngle = Mathf.DeltaAngle(_prevTwistAngle, angle);
        if (Mathf.Abs(dAngle) > 0.15f) HeadingOffset -= dAngle;

        // two-finger drag -> pan on the ground plane
        Vector2 centreDelta = ((a + b) - (_prevA + _prevB)) * 0.5f;
        Vector3 fwd = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(arCamera.transform.right, Vector3.up).normalized;

        // Scale by distance so it feels 1:1 at whatever range you're standing.
        float dist = Vector3.Distance(arCamera.transform.position, _nudgeRoot.position);
        float scale = panMetresPerPixel * Mathf.Clamp(dist / 20f, 0.5f, 4f);

        PositionOffset += (right * centreDelta.x + fwd * centreDelta.y) * scale;

        _prevA = a; _prevB = b; _prevTwistAngle = angle;
        Apply();
    }

    public void SetHeight(float metres) { HeightOffset = metres; Apply(); }   // wire to a Slider

    void Apply()
    {
        if (_nudgeRoot == null) return;

        _nudgeRoot.localPosition = PositionOffset + Vector3.up * HeightOffset;
        _nudgeRoot.localRotation = Quaternion.Euler(0f, HeadingOffset, 0f);
        _dirty = true;
    }

    public void ResetNudge()
    {
        PositionOffset = Vector3.zero;
        HeadingOffset = 0f;
        HeightOffset = 0f;
        Apply();
        Save();
    }

    void Load()
    {
        PositionOffset = new Vector3(
            PlayerPrefs.GetFloat(_siteKey + "_x", 0f), 0f,
            PlayerPrefs.GetFloat(_siteKey + "_z", 0f));
        HeadingOffset = PlayerPrefs.GetFloat(_siteKey + "_h", 0f);
        HeightOffset = PlayerPrefs.GetFloat(_siteKey + "_y", 0f);

        Apply();
        _dirty = false;   // freshly loaded values aren't pending changes
    }

    void Save()
    {
        if (_siteKey == null) return;

        PlayerPrefs.SetFloat(_siteKey + "_x", PositionOffset.x);
        PlayerPrefs.SetFloat(_siteKey + "_z", PositionOffset.z);
        PlayerPrefs.SetFloat(_siteKey + "_h", HeadingOffset);
        PlayerPrefs.SetFloat(_siteKey + "_y", HeightOffset);
        PlayerPrefs.Save();
        _dirty = false;
    }

    // Android kills apps without reliably calling OnApplicationQuit.
    void OnApplicationPause(bool paused) { if (paused && _dirty) Save(); }
    void OnApplicationFocus(bool focus) { if (!focus && _dirty) Save(); }
}
