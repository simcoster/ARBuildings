using UnityEngine;

/// <summary>
/// On-screen diagnostics and nudge controls for on-site work (Steps 9 and 12).
///
/// Deliberately IMGUI: it needs no Canvas, no font asset and no inspector wiring, so it
/// can't be broken by scene edits. Debugging geospatial without visible numbers is
/// guesswork — this is the instrument panel.
/// </summary>
public class DebugHud : MonoBehaviour
{
    [SerializeField] GeospatialController geospatial;
    [SerializeField] AlignmentNudge nudge;
    [SerializeField] LightingController lighting;

    [Tooltip("Metres of height adjustment either side of the anchor.")]
    [SerializeField] float heightRange = 5f;

    bool _visible = true;
    float _height;
    GUIStyle _label, _button, _box;

    void Awake()
    {
        // Auto-wire so there is nothing to forget in the inspector.
        if (geospatial == null) geospatial = FindAnyObjectByType<GeospatialController>();
        if (nudge == null) nudge = FindAnyObjectByType<AlignmentNudge>();
        if (lighting == null) lighting = FindAnyObjectByType<LightingController>();
    }

    void BuildStyles()
    {
        int fontSize = Mathf.RoundToInt(Screen.height * 0.016f);

        _box = new GUIStyle(GUI.skin.box);
        _box.normal.background = Texture2D.whiteTexture;

        _label = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            richText = false,
            wordWrap = true
        };
        _label.normal.textColor = Color.white;

        _button = new GUIStyle(GUI.skin.button) { fontSize = fontSize };
    }

    void OnGUI()
    {
        if (_label == null) BuildStyles();

        float w = Screen.width;
        float pad = w * 0.03f;
        float btnH = Screen.height * 0.055f;

        // Toggle sits top-right so it never covers the readout.
        if (GUI.Button(new Rect(w - pad - w * 0.22f, pad, w * 0.22f, btnH),
                       _visible ? "hide" : "info", _button))
            _visible = !_visible;

        if (!_visible) return;

        var text = "";
        if (geospatial != null) text += geospatial.DebugReadout + "\n\n";
        if (nudge != null) text += nudge.DebugReadout + "\n\n";
        if (lighting != null) text += lighting.DebugReadout;

        float boxW = w * 0.7f;
        float boxH = Screen.height * 0.28f;

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.Box(new Rect(pad, pad, boxW, boxH), GUIContent.none, _box);
        GUI.color = prev;

        GUI.Label(new Rect(pad * 1.5f, pad * 1.5f, boxW - pad, boxH - pad), text, _label);

        if (nudge == null) return;

        // --- height slider: vertical, right edge, thumb-reachable ---
        float sliderX = w - pad - w * 0.1f;
        float sliderY = pad + btnH + pad;
        float sliderH = Screen.height * 0.4f;

        GUI.Label(new Rect(sliderX - w * 0.02f, sliderY - btnH, w * 0.2f, btnH),
                  $"height {_height:+0.00;-0.00}", _label);

        float newHeight = GUI.VerticalSlider(
            new Rect(sliderX, sliderY, w * 0.1f, sliderH), _height, heightRange, -heightRange);

        if (!Mathf.Approximately(newHeight, _height))
        {
            _height = newHeight;
            nudge.SetHeight(_height);
        }

        // --- reset ---
        if (GUI.Button(new Rect(pad, Screen.height - pad - btnH, w * 0.3f, btnH),
                       "reset nudge", _button))
        {
            _height = 0f;
            nudge.ResetNudge();
        }
    }
}
