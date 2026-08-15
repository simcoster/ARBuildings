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
    [SerializeField] BuildingLoader loader;
    [SerializeField] StreetscapeShadowSetup streetscape;

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
        if (loader == null) loader = FindAnyObjectByType<BuildingLoader>();
        if (streetscape == null) streetscape = FindAnyObjectByType<StreetscapeShadowSetup>();
    }

    /// <summary>
    /// The question the HUD exists to answer: if the building isn't on screen, is it
    /// missing, or is it just somewhere you aren't looking?
    /// </summary>
    string WhereIsIt()
    {
        var anchor = geospatial != null ? geospatial.AnchorTransform : null;
        if (anchor == null) return "anchor: none yet";

        var cam = Camera.main;
        if (cam == null) return "anchor: placed (no camera)";

        Vector3 toTarget = anchor.position - cam.transform.position;
        float distance = toTarget.magnitude;
        float vertical = toTarget.y;

        Vector3 flatTo = Vector3.ProjectOnPlane(toTarget, Vector3.up);
        Vector3 flatFwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);

        float bearing = (flatTo.sqrMagnitude < 0.01f || flatFwd.sqrMagnitude < 0.01f)
            ? 0f
            : Vector3.SignedAngle(flatFwd, flatTo, Vector3.up);

        string hint;
        if (Mathf.Abs(bearing) < 25f) hint = "ahead";
        else if (Mathf.Abs(bearing) > 150f) hint = "BEHIND YOU — turn around";
        else hint = bearing > 0 ? $"turn RIGHT {bearing:F0}°" : $"turn LEFT {-bearing:F0}°";

        var view = cam.WorldToViewportPoint(anchor.position);
        bool onScreen = view.z > 0 && view.x > 0 && view.x < 1 && view.y > 0 && view.y < 1;

        return $"anchor: {distance:F0} m, {hint}\n" +
               $"  vertical {vertical:+0.0;-0.0} m, {(onScreen ? "ON SCREEN" : "off screen")}";
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

        if (geospatial != null)
            text += $"[{geospatial.CurrentPhase}]\n{geospatial.DebugReadout}\n\n";

        // Only meaningful once something has been placed.
        if (geospatial != null && geospatial.CurrentPhase == GeospatialController.Phase.Placed)
            text += WhereIsIt() + "\n\n";

        if (loader != null)
        {
            text += $"model: {loader.State}";
            if (loader.State == BuildingLoader.LoadState.Loaded)
                // F1, not F0: in preview the model is centimetres tall and F0 renders the
                // whole thing as "0x0x0", which reads as a broken load.
                text += $" ({loader.RendererCount} rend, x{loader.AppliedScale:F2}\n" +
                        $"  {loader.BoundsSize.x:F1}x{loader.BoundsSize.y:F1}x{loader.BoundsSize.z:F1} m)";
            else if (loader.State == BuildingLoader.LoadState.Failed)
                text += $"\n  {loader.LastMessage}";
            text += "\n\n";
        }

        if (streetscape != null)
            text += $"streetscape: {streetscape.MeshCount} meshes, " +
                    $"{streetscape.GhostedCount} ghosted\n\n";

        var placement = geospatial != null ? geospatial.GetComponent<BuildingPlacement>() : null;
        if (placement != null) text += placement.PlacementReadout + "\n\n";

        if (geospatial != null && geospatial.PreviewActive)
            text += geospatial.PreviewReadout + "\n\n";

        if (nudge != null) text += $"edit: {nudge.CurrentMode}\n" + nudge.DebugReadout + "\n\n";
        if (lighting != null) text += lighting.DebugReadout;

        float boxW = w * 0.7f;
        float boxH = Screen.height * 0.46f;

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.Box(new Rect(pad, pad, boxW, boxH), GUIContent.none, _box);
        GUI.color = prev;

        GUI.Label(new Rect(pad * 1.5f, pad * 1.5f, boxW - pad, boxH - pad), text, _label);

        // --- mesh visualisation toggle ---
        if (streetscape != null &&
            GUI.Button(new Rect(w - pad - w * 0.22f, pad + btnH * 1.2f, w * 0.22f, btnH),
                       streetscape.VisualiseMeshes ? "mesh ON" : "mesh", _button))
            streetscape.VisualiseMeshes = !streetscape.VisualiseMeshes;

        DrawPreviewControls(w, pad, btnH);

        if (nudge == null) return;

        // --- edit mode: off by default so stray grip touches can't move the building ---
        float modeY = Screen.height - pad - btnH * 2.3f;
        float modeW = w * 0.3f;

        var modes = new[] { AlignmentNudge.Mode.Off, AlignmentNudge.Mode.Pan, AlignmentNudge.Mode.Rotate };
        for (int i = 0; i < modes.Length; i++)
        {
            bool active = nudge.CurrentMode == modes[i];
            var prevBg = GUI.backgroundColor;
            if (active) GUI.backgroundColor = Color.cyan;

            if (GUI.Button(new Rect(pad + i * (modeW + pad * 0.4f), modeY, modeW, btnH),
                           modes[i].ToString().ToLower(), _button))
                nudge.CurrentMode = modes[i];

            GUI.backgroundColor = prevBg;
        }

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

    /// <summary>
    /// Preview mode: a scale model in the room with you, sized as if you were standing
    /// however far away the slider says. Lives on the left, clear of the height slider.
    /// </summary>
    void DrawPreviewControls(float w, float pad, float btnH)
    {
        if (geospatial == null) return;

        bool active = geospatial.PreviewActive;
        float y = Screen.height * 0.6f;
        float btnW = w * 0.3f;

        var prevBg = GUI.backgroundColor;
        if (active) GUI.backgroundColor = Color.cyan;

        if (GUI.Button(new Rect(pad, y, btnW, btnH), active ? "preview ON" : "preview", _button))
        {
            geospatial.SetPreview(!active);

            // Indoors or after dark the real sun is below the horizon and the model comes
            // out black. Fake daylight follows preview on, and hands the real sun back off.
            if (lighting != null) lighting.ForceDaylight = !active;
        }

        GUI.backgroundColor = prevBg;

        if (!active) return;

        // Re-drop it in front of you — cheaper than walking back to where you started.
        if (GUI.Button(new Rect(pad * 1.4f + btnW, y, btnW, btnH), "recenter", _button))
            geospatial.PositionPreview();

        // Escape hatch: go back to the real sun angle for the site's current time of day.
        if (lighting != null)
        {
            var sunBg = GUI.backgroundColor;
            if (lighting.ForceDaylight) GUI.backgroundColor = Color.cyan;

            if (GUI.Button(new Rect(pad * 1.8f + btnW * 2f, y, btnW * 0.7f, btnH), "sun", _button))
                lighting.ForceDaylight = !lighting.ForceDaylight;

            GUI.backgroundColor = sunBg;
        }

        // Log scale: 10 m to 400 m. Most of the useful range is under 150 m, and a linear
        // slider spends most of its travel out past where the model is a speck.
        float sliderY = y + btnH * 1.4f;
        float distance = Mathf.Clamp(geospatial.PreviewViewDistance, 10f, 400f);

        GUI.Label(new Rect(pad, sliderY, w * 0.6f, btnH),
                  $"as seen from {distance:F0} m", _label);

        float t = Mathf.InverseLerp(Mathf.Log(10f), Mathf.Log(400f), Mathf.Log(distance));
        float newT = GUI.HorizontalSlider(
            new Rect(pad, sliderY + btnH * 0.9f, w * 0.62f, btnH), t, 0f, 1f);

        if (!Mathf.Approximately(newT, t))
            geospatial.PreviewViewDistance =
                Mathf.Exp(Mathf.Lerp(Mathf.Log(10f), Mathf.Log(400f), newT));
    }
}
