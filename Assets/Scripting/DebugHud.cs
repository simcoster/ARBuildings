using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

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
    [SerializeField] AdaptiveQuality quality;

    bool _visible = true;
    GUIStyle _label, _button, _box, _sliderTrack, _sliderThumb;

    /// <summary>What the next tap on the world will do. Armed by a button, fires once.</summary>
    enum TapAction { None, PlacePreview }

    // Armed explicitly and cleared after one tap, so a stray touch while walking can't
    // silently re-ghost a building or teleport the model.
    TapAction _tapAction = TapAction.None;
    int _armedOnFrame;
    string _tapResult = "";
    bool _showLight;
    string _captureResult = "";

    /// <summary>Placement lives on the same object as the controller.</summary>
    BuildingPlacement Placement =>
        geospatial != null ? geospatial.GetComponent<BuildingPlacement>() : null;

    void Awake()
    {
        // Auto-wire so there is nothing to forget in the inspector.
        if (geospatial == null) geospatial = FindAnyObjectByType<GeospatialController>();
        if (nudge == null) nudge = FindAnyObjectByType<AlignmentNudge>();
        if (lighting == null) lighting = FindAnyObjectByType<LightingController>();
        if (loader == null) loader = FindAnyObjectByType<BuildingLoader>();
        if (streetscape == null) streetscape = FindAnyObjectByType<StreetscapeShadowSetup>();
        if (quality == null) quality = FindAnyObjectByType<AdaptiveQuality>();
    }

    // AlignmentNudge enables this too; the support is reference-counted, so both can.
    //
    // onFingerDown is an EVENT, not a poll. Polling activeTouches for phase == Began misses
    // any tap quick enough to begin and end between two Update calls — at 30 fps that is
    // most normal taps, which showed up as the button working about one time in six.
    void OnEnable()
    {
        EnhancedTouchSupport.Enable();
        Touch.onFingerDown += OnFingerDown;
    }

    void OnDisable()
    {
        Touch.onFingerDown -= OnFingerDown;
        EnhancedTouchSupport.Disable();
    }

    Vector2? _pendingTap;

    void OnFingerDown(Finger finger) => _pendingTap = finger.screenPosition;

    void Update()
    {
        if (_tapAction == TapAction.None) { _pendingTap = null; return; }

        // Ignore the frame the arming button was pressed on, or that press acts as the tap.
        if (Time.frameCount <= _armedOnFrame + 1) { _pendingTap = null; return; }

        Vector2? point = _pendingTap;
        _pendingTap = null;

        // Editor convenience — there is no touchscreen on a desktop.
        if (point == null && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            point = Mouse.current.position.ReadValue();

        if (point == null) return;

        _tapAction = TapAction.None;

        var cam = Camera.main;
        if (cam == null) { _tapResult = "no camera"; return; }

        bool placed = geospatial != null && geospatial.TryPlacePreviewAt(point.Value);
        _tapResult = geospatial != null ? geospatial.PlacementSource : "no controller";
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

        // IMGUI's stock slider thumb is about 10 px — fine with a mouse, hopeless with a
        // thumb on a phone held at arm's length. Both track and knob are scaled to the
        // screen so the knob is a real touch target.
        _sliderTrack = new GUIStyle(GUI.skin.horizontalSlider)
        {
            fixedHeight = Screen.height * 0.03f
        };

        _sliderThumb = new GUIStyle(GUI.skin.horizontalSliderThumb)
        {
            fixedWidth = Screen.width * 0.11f,
            fixedHeight = Screen.height * 0.045f
        };
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
            text += $"[{geospatial.CurrentPhase}] cfg:{SiteCatalog.LastSource}\n" +
                    $"{geospatial.DebugReadout}\n\n";

        // Near the top: the tier silently changes shadow distance, so when shadows go
        // missing this is the first line worth reading.
        if (quality != null) text += quality.DebugReadout + "\n\n";

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
        {
            text += $"streetscape: {streetscape.MeshCount} meshes\n" +
                    $"  {streetscape.GeometryTypeBreakdown}\n" +
                    $"  target: {streetscape.BuildingProximityReadout}\n" +
                    $"  cutout: {streetscape.CutoutReadout}\n";

            if (streetscape.DebugMaterialMissing)
                text += "  NO DEBUG MATERIAL — assign it on XR Origin\n";

            if (_tapResult != "") text += $"  tap: {_tapResult}\n";

            text += "\n";
        }

        var placement = geospatial != null ? geospatial.GetComponent<BuildingPlacement>() : null;
        if (placement != null) text += placement.PlacementReadout + "\n\n";

        if (geospatial != null && geospatial.PreviewActive)
            text += geospatial.PreviewReadout + "\n\n";

        if (nudge != null) text += nudge.DebugReadout + "\n\n";
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

        // --- light estimation dome ---
        if (lighting != null)
        {
            var lightBg = GUI.backgroundColor;
            if (_showLight) GUI.backgroundColor = Color.cyan;

            if (GUI.Button(new Rect(w - pad - w * 0.22f, pad + btnH * 4.8f, w * 0.22f, btnH),
                           "light", _button))
                _showLight = !_showLight;

            GUI.backgroundColor = lightBg;

            if (_showLight) DrawLightDome(w, pad, btnH);
        }

        // --- master occlusion switch ---
        if (streetscape != null)
        {
            var occBg = GUI.backgroundColor;
            if (!streetscape.OccludersEnabled) GUI.backgroundColor = new Color(1f, 0.55f, 0.1f);

            if (GUI.Button(new Rect(w - pad - w * 0.22f, pad + btnH * 7.2f, w * 0.22f, btnH),
                           streetscape.OccludersEnabled ? "occlude" : "OCC OFF", _button))
                streetscape.OccludersEnabled = !streetscape.OccludersEnabled;

            GUI.backgroundColor = occBg;
        }

        // --- cutout on/off, to compare against the real world without a rebuild ---
        if (streetscape != null)
        {
            var cutBg = GUI.backgroundColor;
            if (streetscape.CutoutEnabled) GUI.backgroundColor = Color.cyan;

            if (GUI.Button(new Rect(w - pad - w * 0.22f, pad + btnH * 3.6f, w * 0.22f, btnH),
                           streetscape.CutoutEnabled ? "cut ON" : "cut", _button))
                streetscape.CutoutEnabled = !streetscape.CutoutEnabled;

            GUI.backgroundColor = cutBg;
        }

        // --- reload site config, so a pushed buildings.json needs no rebuild ---
        if (geospatial != null &&
            GUI.Button(new Rect(w - pad - w * 0.22f, pad + btnH * 2.4f, w * 0.22f, btnH),
                       "reload", _button))
            geospatial.ReloadSite();

        // --- capture: screenshot + full state dump, for diagnosing this away from the site ---
        if (GUI.Button(new Rect(w - pad - w * 0.22f, pad + btnH * 6f, w * 0.22f, btnH),
                       "capture", _button))
            _captureResult = DebugCapture.Take(geospatial, loader, nudge, lighting,
                                               streetscape, Placement);

        if (_captureResult != "")
            GUI.Label(new Rect(w - pad - w * 0.42f, pad + btnH * 7.1f, w * 0.4f, btnH),
                      _captureResult, _label);

        DrawPreviewControls(w, pad, btnH);

        if (nudge == null) return;

        DrawAdjustControls(w, pad, btnH);
    }

    /// <summary>
    /// One slider drives whichever of the five values is selected. Applies in preview and in
    /// real geospatial placement alike, and "save" writes them to disk for the next run.
    /// </summary>
    void DrawAdjustControls(float w, float pad, float btnH)
    {
        // Not Enum.GetValues: the row is 5 buttons with the aspect lock on and 6 with it off,
        // and the nudge owns which.
        var parameters = nudge.ActiveParams;

        float rowY = Screen.height - pad - btnH * 4.6f;
        float cellW = (w - pad * 2f - pad * 0.4f * (parameters.Length - 1)) / parameters.Length;

        // --- which value the slider edits ---
        for (int i = 0; i < parameters.Length; i++)
        {
            bool active = nudge.Selected == parameters[i];
            var prevBg = GUI.backgroundColor;
            if (active) GUI.backgroundColor = Color.cyan;

            if (GUI.Button(new Rect(pad + i * (cellW + pad * 0.4f), rowY, cellW, btnH),
                           nudge.LabelOf(parameters[i]), _button))
                nudge.Selected = parameters[i];

            GUI.backgroundColor = prevBg;
        }

        // --- the slider ---
        var selected = nudge.Selected;
        AlignmentNudge.RangeOf(selected, out float min, out float max, out bool logarithmic);

        float value = nudge.GetValue(selected);
        float sliderY = rowY + btnH * 1.5f;

        GUI.Label(new Rect(pad, sliderY - btnH * 0.9f, w * 0.58f, btnH),
                  $"{nudge.LabelOf(selected)}: {nudge.ValueText(selected)}", _label);

        // Aspect lock rides on the value-label row — the only free width left down here, and it
        // belongs next to the scale it governs rather than in the right-hand column.
        bool locked = nudge.Current.keepAspect;
        var prevAspectBg = GUI.backgroundColor;
        if (!locked) GUI.backgroundColor = Color.yellow;

        if (GUI.Button(new Rect(w * 0.6f, sliderY - btnH * 0.95f, w * 0.4f - pad, btnH * 0.9f),
                       locked ? "aspect LOCK" : "aspect FREE", _button))
            nudge.SetKeepAspect(!locked);

        GUI.backgroundColor = prevAspectBg;

        float t = AlignmentNudge.ToSlider(value, min, max, logarithmic);

        float newT = GUI.HorizontalSlider(new Rect(pad, sliderY, w - pad * 2f, btnH), t, 0f, 1f,
                                          _sliderTrack, _sliderThumb);

        // Guard the NaN case explicitly rather than relying on Approximately, which returns
        // false for NaN and so treats a broken mapping as "the user moved the slider".
        if (!float.IsNaN(newT) && !Mathf.Approximately(newT, t))
            nudge.SetValue(selected, AlignmentNudge.FromSlider(newT, min, max, logarithmic));

        // --- save / reset / clear ---
        float actionY = Screen.height - pad - btnH;
        float gap = pad * 0.4f;
        float actionW = (w - pad * 2f - gap * 2f) / 3f;

        // Saving is only allowed with a good enough fix, so the state of the fix is shown
        // BEFORE the button is pressed — being told why afterwards is no help on site.
        bool canSave = true;
        string saveReason = "";
        if (geospatial != null) canSave = geospatial.CanSaveCoordinates(out saveReason);

        // Saving is never blocked — scale, rotation and offsets are worth keeping with or
        // without a fix. What the line above the button says is what you are about to get.
        string status = nudge.LastSaveMessage != ""
            ? nudge.LastSaveMessage
            : (canSave ? saveReason : $"{saveReason} — will save settings only");

        if (status != "")
        {
            var prevColour = GUI.color;
            GUI.color = canSave ? Color.white : new Color(1f, 0.75f, 0.3f);
            GUI.Label(new Rect(pad, actionY - btnH * 0.95f, w - pad * 2f, btnH), status, _label);
            GUI.color = prevColour;
        }

        var saveBg = GUI.backgroundColor;
        if (nudge.Dirty) GUI.backgroundColor = Color.yellow;

        // Routed through the controller, not straight to the nudge: the save also captures
        // the building's real coordinates, and only the controller can convert them.
        if (GUI.Button(new Rect(pad, actionY, actionW, btnH),
                       canSave ? "save + GPS" : "save", _button))
        {
            if (geospatial != null) geospatial.SaveAdjustment();
            else nudge.Save();
        }

        GUI.backgroundColor = saveBg;

        // Zeroes the sliders only — nothing on disk changes until you save.
        if (GUI.Button(new Rect(pad + actionW + gap, actionY, actionW, btnH), "reset", _button))
            nudge.ResetAll();

        // Deletes the saved entry, restoring the project's own coordinates.
        if (GUI.Button(new Rect(pad + (actionW + gap) * 2f, actionY, actionW, btnH),
                       "clear saved", _button))
            nudge.ClearSaved();
    }

    /// <summary>
    /// Preview mode: a scale model in the room with you, sized as if you were standing
    /// however far away the slider says. Lives on the left, clear of the height slider.
    /// </summary>
    void DrawPreviewControls(float w, float pad, float btnH)
    {
        if (geospatial == null) return;

        bool active = geospatial.PreviewActive;

        // Sits between the info box (top 46%) and the edit-mode row (~84%). The block is
        // four rows tall when preview is on, so it starts high enough to clear both.
        float y = Screen.height * 0.54f;
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

        // The viewing-distance slider is gone: placement auto-fits the model to the view, so
        // the number was something to fight rather than something to set. The info panel
        // still reports the distance it settled on.

        // --- stand it on the floor where you tap ---
        bool placing = _tapAction == TapAction.PlacePreview;
        float placeY = y + btnH * 1.4f;

        var placeBg = GUI.backgroundColor;
        if (placing) GUI.backgroundColor = Color.yellow;

        if (GUI.Button(new Rect(pad, placeY, btnW * 1.4f, btnH),
                       placing ? "tap the floor..." : "place on floor", _button))
            ArmTap(placing ? TapAction.None : TapAction.PlacePreview);

        GUI.backgroundColor = placeBg;

        if (_tapResult != "" && !placing)
            GUI.Label(new Rect(pad * 1.4f + btnW * 1.4f, placeY, w * 0.4f, btnH),
                      _tapResult, _label);
    }

    /// <summary>
    /// A sky dome seen from above: centre is straight up, the rim is the horizon, north is
    /// up-screen. Plots where ARCore thinks the light comes from against where the sun
    /// actually is, over a ring showing the ambient probe's energy by direction.
    /// </summary>
    void DrawLightDome(float w, float pad, float btnH)
    {
        if (lighting == null) return;

        // Right edge stops short of the vertical height slider's column, and it starts below
        // the right-hand button stack. It does overlay the info box while shown — that's the
        // trade for a chart big enough to read in sunlight, and it's one tap to dismiss.
        float size = w * 0.34f;
        float x = w - pad - w * 0.13f - size;
        float y = pad + btnH * 6f;
        float cx = x + size * 0.5f, cy = y + size * 0.5f;
        float radius = size * 0.42f;

        var prev = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x, y, size, size), Texture2D.whiteTexture);

        // --- ambient probe, drawn as a ring of cells around the rim ---
        var directions = LightingController.SampleDirections;
        var colours = lighting.SampleColours;

        if (colours != null)
        {
            float peak = 0.0001f;
            foreach (var c in colours)
                peak = Mathf.Max(peak, c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f);

            for (int i = 0; i < directions.Length; i++)
            {
                if (!ProjectToDome(directions[i], cx, cy, radius, out Vector2 p)) continue;

                var c = colours[i];
                float luma = (c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f) / peak;

                GUI.color = new Color(c.r / peak, c.g / peak, c.b / peak,
                                      Mathf.Clamp01(0.15f + luma * 0.85f));
                float d = size * 0.045f;
                GUI.DrawTexture(new Rect(p.x - d * 0.5f, p.y - d * 0.5f, d, d),
                                Texture2D.whiteTexture);
            }
        }

        // --- horizon rim ---
        GUI.color = new Color(1f, 1f, 1f, 0.25f);
        for (int i = 0; i < 48; i++)
        {
            float a = i / 48f * Mathf.PI * 2f;
            GUI.DrawTexture(new Rect(cx + Mathf.Cos(a) * radius, cy + Mathf.Sin(a) * radius,
                                     2f, 2f), Texture2D.whiteTexture);
        }

        // --- computed sun ---
        Vector3 sunDirection = FromAzimuthElevation(lighting.SolarAzimuthDeg,
                                                    lighting.SolarElevationDeg);
        if (ProjectToDome(sunDirection, cx, cy, radius, out Vector2 sp))
        {
            GUI.color = new Color(1f, 0.85f, 0.2f, 0.95f);
            float d = size * 0.09f;
            GUI.DrawTexture(new Rect(sp.x - d * 0.5f, sp.y - d * 0.5f, d, d),
                            Texture2D.whiteTexture);
        }

        // --- ARCore's estimate: negate travel direction to point back at the source ---
        if (lighting.EstimatedLightTravel.HasValue &&
            ProjectToDome(-lighting.EstimatedLightTravel.Value.normalized, cx, cy, radius,
                          out Vector2 ep))
        {
            var c = lighting.EstimatedColour;
            GUI.color = new Color(c.r, c.g, c.b, 0.95f);
            float d = size * 0.06f;
            GUI.DrawTexture(new Rect(ep.x - d * 0.5f, ep.y - d * 0.5f, d, d),
                            Texture2D.whiteTexture);
        }

        GUI.color = prev;

        string estimate = lighting.EstimatedLightTravel.HasValue
            ? $"est {Azimuth(-lighting.EstimatedLightTravel.Value):F0}°/" +
              $"{Elevation(-lighting.EstimatedLightTravel.Value):F0}°"
            : "est: none";

        GUI.Label(new Rect(x, y + size, size, btnH * 4f),
                  $"sun {lighting.SolarAzimuthDeg:F0}°/{lighting.SolarElevationDeg:F0}°\n" +
                  $"{estimate}  {lighting.EstimatedLumens:F0} lm\n" +
                  $"lobes {lighting.AmbientLobeCount}, dir {lighting.AmbientDirectionality:F1}x\n" +
                  (lighting.NorthKnown
                      ? $"north {lighting.NorthOffsetDeg:F0}° (from VPS)"
                      : $"north {lighting.NorthOffsetDeg:F0}° GUESSED"),
                  _label);
    }

    /// <summary>World direction to a point on the dome. Returns false for below the horizon.</summary>
    static bool ProjectToDome(Vector3 direction, float cx, float cy, float radius, out Vector2 point)
    {
        point = default;
        if (direction.y < 0f) return false;      // below the horizon, not on this chart

        // Elevation drives distance from centre: straight up lands dead centre, the horizon
        // lands on the rim. Unity is +Z north, +X east; screen up is north.
        float r = (1f - direction.y) * radius;
        Vector2 flat = new Vector2(direction.x, direction.z);

        if (flat.sqrMagnitude > 1e-6f) flat.Normalize();

        point = new Vector2(cx + flat.x * r, cy - flat.y * r);
        return true;
    }

    static Vector3 FromAzimuthElevation(float azimuthDeg, float elevationDeg)
    {
        float a = azimuthDeg * Mathf.Deg2Rad, e = elevationDeg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(e) * Mathf.Sin(a), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Cos(a));
    }

    static float Azimuth(Vector3 d) => (Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + 360f) % 360f;
    static float Elevation(Vector3 d) => Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

    void ArmTap(TapAction action)
    {
        _tapAction = action;
        _armedOnFrame = Time.frameCount;
        _tapResult = "";
    }
}
