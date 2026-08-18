using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// On-site manual correction (Step 9). VPS gets you close; "close" at building scale still
/// looks wrong.
///
/// Five values, one slider: heading, scale, and three offsets. Gestures are gone — holding a
/// phone produces stray contacts, and a drag that silently moves a building by metres is
/// worse than no control at all. A slider does exactly what you asked and nothing else.
///
/// The values are saved to a JSON file and reloaded on the next run, so they apply to BOTH
/// preview and real geospatial placement — tune it indoors, and the same numbers are used on
/// site.
/// </summary>
public class AlignmentNudge : MonoBehaviour
{
    /// <summary>Which value the single slider is currently editing.</summary>
    public enum Param { Rotate, Scale, East, North, Height }

    /// <summary>One site's saved adjustment. Serialised straight to JSON.</summary>
    [Serializable]
    public class Adjustment
    {
        public string siteId = "";
        public float headingDeg;
        public float scale = 1f;
        public float eastMetres;
        public float northMetres;
        public float heightMetres;

        /// <summary>
        /// Set once a save has captured real coordinates, replacing the ones configured in
        /// the inspector. Only ever written on site: it takes a live Earth fix to know where
        /// a point actually is, so a preview placement in your living room cannot produce it.
        /// </summary>
        public bool hasCoordinates;
        public double latitude;
        public double longitude;
    }

    [Serializable]
    class AdjustmentFile
    {
        public List<Adjustment> entries = new List<Adjustment>();
    }

    [Tooltip("File name under Application.persistentDataPath. Pull it off the device with " +
             "adb to see exactly what was saved.")]
    [SerializeField] string fileName = "adjustments.json";

    /// <summary>Which parameter the slider drives.</summary>
    public Param Selected { get; set; } = Param.Rotate;

    /// <summary>The live values. Never null.</summary>
    public Adjustment Current { get; private set; } = new Adjustment();

    /// <summary>True when there are changes the save button has not written yet.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Set after a save, so the HUD can confirm it actually happened.</summary>
    public string LastSaveMessage { get; private set; } = "";

    Transform _root;      // bound at runtime — the anchor doesn't exist at scene load
    string _siteId = "";

    string FilePath => Path.Combine(Application.persistentDataPath, fileName);

    public string DebugReadout =>
        $"rot {Current.headingDeg:+0.0;-0.0}°  scale {Current.scale:F2}x\n" +
        $"E {Current.eastMetres:+0.0;-0.0}  N {Current.northMetres:+0.0;-0.0}  " +
        $"up {Current.heightMetres:+0.0;-0.0} m{(Dirty ? "  *unsaved*" : "")}";

    // ------------------------------------------------------------------ binding

    /// <summary>
    /// Called once the placement hierarchy exists. Preview and geospatial share one site id
    /// on purpose: the whole point is that what you line up indoors is what gets used on site.
    /// </summary>
    public void Bind(Transform root, string siteId)
    {
        _root = root;
        _siteId = siteId;
        Current = Load(siteId);
        Dirty = false;
        Apply();
    }

    // ------------------------------------------------------------------- values

    public float GetValue(Param p)
    {
        switch (p)
        {
            case Param.Rotate: return Current.headingDeg;
            case Param.Scale:  return Current.scale;
            case Param.East:   return Current.eastMetres;
            case Param.North:  return Current.northMetres;
            default:           return Current.heightMetres;
        }
    }

    public void SetValue(Param p, float value)
    {
        switch (p)
        {
            case Param.Rotate: Current.headingDeg = value; break;
            case Param.Scale:  Current.scale = Mathf.Clamp(value, 0.1f, 50f); break;
            case Param.East:   Current.eastMetres = value; break;
            case Param.North:  Current.northMetres = value; break;
            default:           Current.heightMetres = value; break;
        }

        Dirty = true;
        Apply();
    }

    public static void RangeOf(Param p, out float min, out float max, out bool logarithmic)
    {
        switch (p)
        {
            case Param.Rotate: min = -180f; max = 180f; logarithmic = false; break;

            // 0.1x to 50x is a 500-fold span. On a linear slider everything below 1x would
            // live in the first 2% of the travel, so this one is logarithmic.
            case Param.Scale:  min = 0.1f;  max = 50f;  logarithmic = true;  break;

            // Ground offsets reach much further than height ever needs to: VPS can be tens
            // of metres out, and you may want to walk the building down the street.
            case Param.East:
            case Param.North:  min = -200f; max = 200f; logarithmic = false; break;

            default:           min = -50f;  max = 50f;  logarithmic = false; break;
        }
    }

    public static string Label(Param p)
    {
        switch (p)
        {
            case Param.Rotate: return "rot";
            case Param.Scale:  return "scale";
            case Param.East:   return "X";
            case Param.North:  return "Y";
            default:           return "up";
        }
    }

    public string ValueText(Param p)
    {
        float v = GetValue(p);
        switch (p)
        {
            case Param.Rotate: return $"{v:+0.0;-0.0}°";
            case Param.Scale:  return $"{v:F2}x";
            default:           return $"{v:+0.00;-0.00} m";
        }
    }

    // ------------------------------------------------------------------- apply

    /// <summary>
    /// Offsets are world-axis aligned, not aligned to the building. ARCore's geospatial
    /// frame is EUS — +X east, +Y up, +Z south — so north is -Z. Compensating for the
    /// parent's rotation means "east" stays east however the building is turned.
    ///
    /// InverseTransformDirection ignores scale, so the offset keeps its length in the
    /// parent's units — real building metres in both modes, which is why a 10 m nudge looks
    /// like 10 m of building even when preview has shrunk the whole hierarchy.
    /// </summary>
    void Apply()
    {
        if (_root == null) return;

        var world = new Vector3(Current.eastMetres, Current.heightMetres, -Current.northMetres);

        _root.localPosition = _root.parent != null
            ? _root.parent.InverseTransformDirection(world)
            : world;

        _root.localRotation = Quaternion.Euler(0f, Current.headingDeg, 0f);
        _root.localScale = Vector3.one * Mathf.Max(0.01f, Current.scale);
    }

    public void ResetAll()
    {
        Current = new Adjustment { siteId = _siteId };
        Dirty = true;
        Apply();
    }

    /// <summary>
    /// Rewrites the site's coordinates to where the building actually ended up, and zeroes
    /// the east/north offsets because they are now baked into that position. Without the
    /// zeroing the offsets would be applied a second time on the next run and the building
    /// would walk further away every time you saved.
    /// </summary>
    public void BakeCoordinates(double latitude, double longitude)
    {
        Current.hasCoordinates = true;
        Current.latitude = latitude;
        Current.longitude = longitude;
        Current.eastMetres = 0f;
        Current.northMetres = 0f;

        Dirty = true;
        Apply();
    }

    /// <summary>
    /// Reads a site's saved coordinates without binding — placement needs them before the
    /// hierarchy that Bind attaches to exists.
    /// </summary>
    public bool TryGetSavedCoordinates(string siteId, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        var entry = ReadFile().entries.Find(e => e.siteId == siteId);
        if (entry == null || !entry.hasCoordinates) return false;

        latitude = entry.latitude;
        longitude = entry.longitude;
        return true;
    }

    // ------------------------------------------------------------- persistence

    /// <summary>
    /// Writes to a JSON file rather than PlayerPrefs so the saved numbers can be read,
    /// diffed and copied into buildings.json — PlayerPrefs is opaque and one wipe from gone.
    /// </summary>
    public void Save()
    {
        Current.siteId = _siteId;

        var file = ReadFile();
        int index = file.entries.FindIndex(e => e.siteId == _siteId);

        if (index >= 0) file.entries[index] = Current;
        else file.entries.Add(Current);

        try
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(file, true));
            Dirty = false;

            // Which of the two kinds of save happened matters: one redefines where the
            // building is on Earth, the other only stores offsets.
            LastSaveMessage = Current.hasCoordinates
                ? $"saved + coords {Current.latitude:F6},{Current.longitude:F6}"
                : "saved (offsets only — no GPS fix)";
            Debug.Log($"[Adjust] saved '{_siteId}' to {FilePath}: {DebugReadout.Replace('\n', ' ')}");
        }
        catch (Exception e)
        {
            LastSaveMessage = "SAVE FAILED";
            Debug.LogError($"[Adjust] could not write {FilePath}: {e.Message}");
        }
    }

    /// <summary>
    /// Deletes this site's saved entry, so placement falls back to the coordinates built
    /// into the project — the inspector values or buildings.json.
    ///
    /// Distinct from ResetAll, which only zeroes the live sliders: this one touches the
    /// file, and is the way back once a bad on-site save has replaced the coordinates.
    /// </summary>
    public void ClearSaved()
    {
        var file = ReadFile();
        int removed = file.entries.RemoveAll(e => e.siteId == _siteId);

        try
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(file, true));

            LastSaveMessage = removed > 0
                ? "cleared — project coords on next run"
                : "nothing saved to clear";

            Debug.Log($"[Adjust] cleared {removed} saved entry for '{_siteId}'");
        }
        catch (Exception e)
        {
            LastSaveMessage = "CLEAR FAILED";
            Debug.LogError($"[Adjust] could not write {FilePath}: {e.Message}");
        }

        Current = new Adjustment { siteId = _siteId };
        Dirty = false;
        Apply();
    }

    Adjustment Load(string siteId)
    {
        var entry = ReadFile().entries.Find(e => e.siteId == siteId);

        if (entry == null) return new Adjustment { siteId = siteId };

        Debug.Log($"[Adjust] loaded '{siteId}': rot {entry.headingDeg:F1}, " +
                  $"scale {entry.scale:F2}, E {entry.eastMetres:F1}, " +
                  $"N {entry.northMetres:F1}, up {entry.heightMetres:F1}");
        return entry;
    }

    AdjustmentFile ReadFile()
    {
        if (!File.Exists(FilePath)) return new AdjustmentFile();

        try
        {
            var parsed = JsonUtility.FromJson<AdjustmentFile>(File.ReadAllText(FilePath));
            return parsed ?? new AdjustmentFile();
        }
        catch (Exception e)
        {
            // A corrupt file must not take placement down with it.
            Debug.LogWarning($"[Adjust] {FilePath} unreadable, starting fresh: {e.Message}");
            return new AdjustmentFile();
        }
    }

    // Android kills apps without reliably calling OnApplicationQuit. Saving here would
    // defeat the point of an explicit save button, so this only warns.
    void OnApplicationPause(bool paused)
    {
        if (paused && Dirty) Debug.LogWarning("[Adjust] leaving with unsaved adjustments.");
    }
}
