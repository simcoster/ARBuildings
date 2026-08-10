using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

public class AlignmentNudge : MonoBehaviour
{
    [SerializeField] Transform nudgeRoot;
    [SerializeField] Camera arCamera;
    [SerializeField] float panMetresPerPixel = 0.02f;

    Vector2 prevA, prevB;
    float prevTwistAngle;
    bool tracking;
    string siteKey;
    bool dirty;


    // Current offsets — read these off the debug UI and bake them into buildings.json
    public Vector3 PositionOffset { get; private set; }
    public float HeadingOffset { get; private set; }
    public float HeightOffset { get; private set; }

    void OnEnable() { EnhancedTouchSupport.Enable(); }

    void Update()
    {
        var touches = Touch.activeTouches;

        if (touches.Count != 2) { tracking = false; return; }

        Vector2 a = touches[0].screenPosition;
        Vector2 b = touches[1].screenPosition;
        float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;

        if (!tracking)
        {
            prevA = a; prevB = b; prevTwistAngle = angle;
            tracking = true;
            return;
        }

        // --- twist -> heading ---
        float dAngle = Mathf.DeltaAngle(prevTwistAngle, angle);
        if (Mathf.Abs(dAngle) > 0.15f)          // dead zone, twist is noisy
            HeadingOffset -= dAngle;

        // --- two-finger drag -> pan on the ground plane ---
        Vector2 centreDelta = ((a + b) - (prevA + prevB)) * 0.5f;

        Vector3 fwd = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up).normalized;
        Vector3 right = Vector3.ProjectOnPlane(arCamera.transform.right, Vector3.up).normalized;

        // Scale by distance so it feels 1:1 at whatever range you're standing
        float dist = Vector3.Distance(arCamera.transform.position, nudgeRoot.position);
        float scale = panMetresPerPixel * Mathf.Clamp(dist / 20f, 0.5f, 4f);

        PositionOffset += (right * centreDelta.x + fwd * centreDelta.y) * scale;

        prevA = a; prevB = b; prevTwistAngle = angle;
        Apply();
    }

    public void SetHeight(float metres) { HeightOffset = metres; Apply(); }   // hook to a Slider

    void Apply()
    {
        nudgeRoot.localPosition = PositionOffset + Vector3.up * HeightOffset;
        nudgeRoot.localRotation = Quaternion.Euler(0f, HeadingOffset, 0f);
    }

    public void ResetNudge()
    {
        PositionOffset = Vector3.zero;
        HeadingOffset = 0f;
        HeightOffset = 0f;
        Apply();
    }
    string Key(double lat, double lon) => $"nudge_{lat:F5}_{lon:F5}";

    void Load()
    {
        PositionOffset = new Vector3(
            PlayerPrefs.GetFloat(siteKey + "_x", 0f), 0f,
            PlayerPrefs.GetFloat(siteKey + "_z", 0f));
        HeadingOffset = PlayerPrefs.GetFloat(siteKey + "_h", 0f);
        HeightOffset = PlayerPrefs.GetFloat(siteKey + "_y", 0f);
        Apply();
    }


    public void Bind(Transform root, string siteId)
    {
        nudgeRoot = root;
        siteKey = $"nudge_{siteId}";
        Load();
    }


    void Save()
    {
        if (siteKey == null) return;
        PlayerPrefs.SetFloat(siteKey + "_x", PositionOffset.x);
        PlayerPrefs.SetFloat(siteKey + "_z", PositionOffset.z);
        PlayerPrefs.SetFloat(siteKey + "_h", HeadingOffset);
        PlayerPrefs.SetFloat(siteKey + "_y", HeightOffset);
        PlayerPrefs.Save();
        dirty = false;
    }

    void OnApplicationPause(bool paused) { if (paused && dirty) Save(); }
    void OnApplicationFocus(bool focus) { if (!focus && dirty) Save(); }
    void OnDisable() { if (dirty) Save(); }
}