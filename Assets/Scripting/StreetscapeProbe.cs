using System.Collections.Generic;
using System.Text;
using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Point the camera at a building and be told whether Google has a mesh there.
///
/// A crosshair casts a ray into the streetscape geometry every few frames and reports the
/// nearest Building hit and the nearest Terrain hit separately. That separation is the whole
/// point: a ray aimed at a facade that returns terrain at 30 m has gone straight THROUGH the
/// building and hit the hillside behind it, which is exactly the "Google has nothing here"
/// case this site turned out to be. A count of streetscape meshes cannot tell you that —
/// there can be two dozen of them and every one across the street.
///
/// Ids are ARCore TrackableIds, i.e. session-local handles. There is no persistent per-
/// building identifier in the Streetscape Geometry API; the id tells you two meshes apart
/// in this run and nothing more.
///
/// A plain class, not a MonoBehaviour, so it needs no GameObject and no inspector wiring —
/// DebugHud owns it and drives Tick/Draw, and it cannot be broken by a scene edit.
/// </summary>
public class StreetscapeProbe
{
    /// <summary>Beyond this the answer stops being about the building in front of you.</summary>
    const float MaxDistance = 250f;

    /// <summary>Full mesh raycasts, so not every frame. Fast enough to feel continuous.</summary>
    const float ProbeInterval = 0.15f;

    public bool Active { get; set; }

    /// <summary>Tag every Building mesh in view, not just the one under the crosshair.</summary>
    public bool LabelAll { get; set; } = true;

    readonly List<StreetscapeShadowSetup.ProbeHit> _hits = new();
    readonly List<StreetscapeShadowSetup.GeometryInfo> _geometries = new();
    readonly List<Pin> _pins = new();

    struct Pin
    {
        public Vector3 position;
        public string text;
    }

    float _lastProbe = -99f;
    bool _hasBuilding, _hasTerrain, _earthKnown;
    StreetscapeShadowSetup.ProbeHit _building, _terrain;
    double _latitude, _longitude;
    int _buildingsInView, _terrainInView;
    string _status = "no probe yet";

    // --------------------------------------------------------------------- tick

    public void Tick(StreetscapeShadowSetup streetscape, GeospatialController geospatial)
    {
        if (!Active) return;
        if (Time.unscaledTime - _lastProbe < ProbeInterval) return;
        _lastProbe = Time.unscaledTime;

        _hasBuilding = _hasTerrain = _earthKnown = false;

        if (streetscape == null) { _status = "no streetscape component"; return; }

        var cam = Camera.main;
        if (cam == null) { _status = "no camera"; return; }

        streetscape.CollectGeometries(_geometries);

        _buildingsInView = _terrainInView = 0;
        foreach (var g in _geometries)
        {
            if (!InView(cam, g.bounds.center)) continue;
            if (g.type == StreetscapeGeometryType.Building) _buildingsInView++;
            else _terrainInView++;
        }

        if (_geometries.Count == 0)
        {
            _status = "no streetscape geometry streamed — indoors, or none served here";
            return;
        }

        var ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        streetscape.Probe(ray, MaxDistance, _hits);

        // Nearest of each type rather than nearest overall. Terrain in front of a building
        // is the normal case (you are stood on it); a building behind terrain is not.
        foreach (var hit in _hits)
        {
            if (hit.type == StreetscapeGeometryType.Building && !_hasBuilding)
            {
                _building = hit;
                _hasBuilding = true;
            }
            else if (hit.type != StreetscapeGeometryType.Building && !_hasTerrain)
            {
                _terrain = hit;
                _hasTerrain = true;
            }
        }

        _status = _hasBuilding
            ? $"MESH — id {StreetscapeShadowSetup.ShortId(_building.id)}"
            : "NO BUILDING MESH on this ray";

        // The coordinate of whatever was actually struck. Answers "which address is this
        // mesh" without leaving the app, and works for terrain hits too.
        if (geospatial != null && (_hasBuilding || _hasTerrain))
        {
            Vector3 point = _hasBuilding ? _building.point : _terrain.point;
            _earthKnown = geospatial.TryConvertPoint(point, out _latitude, out _longitude, out _);
        }
    }

    static bool InView(Camera cam, Vector3 world)
    {
        var v = cam.WorldToViewportPoint(world);
        return v.z > 0f && v.x > 0f && v.x < 1f && v.y > 0f && v.y < 1f;
    }

    // --------------------------------------------------------------------- pins

    /// <summary>Leaves the current reading stuck to the world, so several can be compared.</summary>
    public void PinCurrent()
    {
        if (!_hasBuilding && !_hasTerrain) return;

        var hit = _hasBuilding ? _building : _terrain;
        string kind = _hasBuilding ? "BLD" : "TER";

        _pins.Add(new Pin
        {
            position = hit.point,
            text = $"{kind} {StreetscapeShadowSetup.ShortId(hit.id)}\n{hit.distance:F1} m",
        });

        Debug.Log($"[Probe] pinned {kind} {hit.id} at {hit.distance:F1} m, " +
                  $"{hit.Surface}, {hit.triangleCount} tris, quality {hit.quality}");
    }

    public void ClearPins() => _pins.Clear();

    public int PinCount => _pins.Count;

    // --------------------------------------------------------------------- draw

    /// <summary>
    /// Crosshair, readout and world-anchored tags. Returns nothing to the HUD — it owns the
    /// whole middle band of the screen while active.
    /// </summary>
    public void Draw(GUIStyle label, float buttonHeight, GUIStyle button)
    {
        if (!Active) return;

        var cam = Camera.main;
        float w = Screen.width, h = Screen.height;
        float cx = w * 0.5f, cy = h * 0.5f;

        DrawCrosshair(cx, cy, w);

        if (cam != null && LabelAll) DrawGeometryTags(cam, label);
        if (cam != null) DrawPins(cam, label);

        // --- readout, just under the crosshair where the eye already is ---
        float boxW = w * 0.62f, boxH = h * 0.2f;
        float boxX = cx - boxW * 0.5f, boxY = cy + h * 0.05f;

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.DrawTexture(new Rect(boxX, boxY, boxW, boxH), Texture2D.whiteTexture);

        GUI.color = _hasBuilding ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.6f, 0.3f);
        GUI.Label(new Rect(boxX + w * 0.015f, boxY + h * 0.008f, boxW, boxH), Readout, label);
        GUI.color = prev;

        // --- pin / clear / label-all ---
        float row = boxY + boxH + h * 0.008f;
        float cell = boxW / 3f - w * 0.008f;

        if (GUI.Button(new Rect(boxX, row, cell, buttonHeight), "pin", button))
            PinCurrent();

        if (GUI.Button(new Rect(boxX + cell + w * 0.012f, row, cell, buttonHeight),
                       $"clear ({_pins.Count})", button))
            ClearPins();

        var tagBg = GUI.backgroundColor;
        if (LabelAll) GUI.backgroundColor = Color.cyan;

        if (GUI.Button(new Rect(boxX + (cell + w * 0.012f) * 2f, row, cell, buttonHeight),
                       "tags", button))
            LabelAll = !LabelAll;

        GUI.backgroundColor = tagBg;
    }

    static void DrawCrosshair(float cx, float cy, float w)
    {
        float arm = w * 0.03f, thick = Mathf.Max(2f, w * 0.004f);

        var prev = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(cx - arm, cy - thick * 0.5f, arm * 2f, thick),
                        Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy - arm, thick, arm * 2f),
                        Texture2D.whiteTexture);
        GUI.color = prev;
    }

    /// <summary>
    /// A tag on every Building mesh in view. Answers the broader question the crosshair
    /// cannot — whether ARCore reconstructs anything at all around here, and where the
    /// meshes it does have actually sit.
    /// </summary>
    void DrawGeometryTags(Camera cam, GUIStyle label)
    {
        var prev = GUI.color;

        foreach (var g in _geometries)
        {
            if (g.type != StreetscapeGeometryType.Building) continue;

            var v = cam.WorldToViewportPoint(g.bounds.center);
            if (v.z <= 0f || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) continue;

            float x = v.x * Screen.width;
            float y = (1f - v.y) * Screen.height;

            bool isHit = _hasBuilding && g.id == _building.id;

            GUI.color = isHit ? new Color(0f, 0f, 0f, 0.85f) : new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(x - Screen.width * 0.055f, y - Screen.height * 0.012f,
                                     Screen.width * 0.11f, Screen.height * 0.024f),
                            Texture2D.whiteTexture);

            GUI.color = isHit ? new Color(0.4f, 1f, 0.5f) : new Color(0.75f, 0.85f, 1f, 0.8f);
            GUI.Label(new Rect(x - Screen.width * 0.05f, y - Screen.height * 0.012f,
                               Screen.width * 0.12f, Screen.height * 0.03f),
                      $"{StreetscapeShadowSetup.ShortId(g.id)} · {v.z:F0}m", label);
        }

        GUI.color = prev;
    }

    void DrawPins(Camera cam, GUIStyle label)
    {
        var prev = GUI.color;

        foreach (var pin in _pins)
        {
            var v = cam.WorldToViewportPoint(pin.position);
            if (v.z <= 0f) continue;

            float x = v.x * Screen.width;
            float y = (1f - v.y) * Screen.height;

            GUI.color = new Color(1f, 0.85f, 0.2f, 0.95f);
            float d = Screen.width * 0.012f;
            GUI.DrawTexture(new Rect(x - d * 0.5f, y - d * 0.5f, d, d), Texture2D.whiteTexture);

            GUI.Label(new Rect(x + d, y - d, Screen.width * 0.2f, Screen.height * 0.06f),
                      pin.text, label);
        }

        GUI.color = prev;
    }

    // ------------------------------------------------------------------ readout

    string Readout
    {
        get
        {
            if (!Active) return "";

            var text = new StringBuilder();
            text.AppendLine($"PROBE  {_status}");

            if (_hasBuilding)
                text.AppendLine($"building : {_building.distance:F1} m  {_building.Surface}  " +
                                $"{_building.triangleCount} tris  {Quality(_building.quality)}");
            else
                text.AppendLine("building : none along this ray");

            text.AppendLine(_hasTerrain
                ? $"terrain  : {_terrain.distance:F1} m  id " +
                  $"{StreetscapeShadowSetup.ShortId(_terrain.id)}"
                : "terrain  : none along this ray");

            text.AppendLine(_earthKnown
                ? $"aimed at : {_latitude:F6}, {_longitude:F6}"
                : "aimed at : no VPS fix, coordinates unknown");

            text.Append($"in view  : {_buildingsInView} building, {_terrainInView} terrain " +
                        $"(of {_geometries.Count} streamed)");

            return text.ToString();
        }
    }

    /// <summary>
    /// LOD says how much of the answer to trust. An extruded footprint has a flat top and
    /// empty space under any real roof, so a hit near the top of a tall mesh may be air.
    /// </summary>
    static string Quality(StreetscapeGeometryQuality quality) =>
        quality switch
        {
            StreetscapeGeometryQuality.BuildingLOD1 => "LOD1 (extruded footprint)",
            StreetscapeGeometryQuality.BuildingLOD2 => "LOD2 (roof detail)",
            StreetscapeGeometryQuality.None => "no LOD",
            _ => quality.ToString(),
        };

    /// <summary>Full probe state for the capture dump, including every mesh streamed.</summary>
    public string StateReport
    {
        get
        {
            var text = new StringBuilder();

            text.AppendLine($"probe active       : {Active}");
            text.AppendLine($"status             : {_status}");

            if (_hasBuilding)
                text.AppendLine($"building hit       : {_building.id}\n" +
                                $"                     {_building.distance:F2} m, " +
                                $"{_building.Surface}, {_building.triangleCount} tris, " +
                                $"quality {_building.quality}");
            else
                text.AppendLine("building hit       : NONE on the crosshair ray");

            if (_hasTerrain)
                text.AppendLine($"terrain hit        : {_terrain.id} at {_terrain.distance:F2} m");

            text.AppendLine(_earthKnown
                ? $"aim coordinates    : {_latitude:F7}, {_longitude:F7}"
                : "aim coordinates    : unavailable (no VPS fix)");

            text.AppendLine($"in view            : {_buildingsInView} building, " +
                            $"{_terrainInView} terrain");
            text.AppendLine($"pins               : {_pins.Count}");

            var cam = Camera.main;
            text.AppendLine($"streamed meshes    : {_geometries.Count}");

            foreach (var g in _geometries)
            {
                float distance = cam != null
                    ? Mathf.Sqrt(g.bounds.SqrDistance(cam.transform.position))
                    : -1f;

                text.AppendLine($"  {StreetscapeShadowSetup.ShortId(g.id)} {g.type,-8} " +
                                $"{distance,6:F1} m  {g.triangleCount,5} tris  " +
                                $"{g.bounds.size.x:F0}x{g.bounds.size.y:F0}x{g.bounds.size.z:F0} m");
            }

            foreach (var pin in _pins)
                text.AppendLine($"  pin {pin.text.Replace('\n', ' ')} @ {pin.position}");

            return text.ToString();
        }
    }
}
