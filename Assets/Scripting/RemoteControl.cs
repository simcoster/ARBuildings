using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Drives the app from a laptop over adb while the phone sits on a tripod pointed at the
/// building.
///
/// On site the expensive things have always been the same two: reaching past a tripod-mounted
/// phone to press a HUD button (which moves the camera, which is the one thing that must not
/// move), and rebuilding to change a number. This removes both. Push a text file, the app
/// applies it within a quarter of a second and writes back what it did.
///
///     adb push cmd.txt /sdcard/Android/data/com.pavel.arbuildings/files/command.txt
///     adb pull    /sdcard/Android/data/com.pavel.arbuildings/files/remote_result.txt
///
/// The file is plain text, one command per line, because it has to be writable from a shell
/// one-liner with cold hands. Blank lines and anything after '#' are ignored.
///
///     rot +2.5        heading, RELATIVE — a leading + or - always means relative
///     rot 9.57        heading, absolute
///     scale 1.02      east -0.4      north +0.2      up 0      scalev 1.0
///     save            reset          clear           reload
///     preview on      sun on         aspect on
///     depth on|off    real-world depth occlusion — the only occluder there is.
///                     `occlude` is an alias for it; `cut` and `mesh` are gone with streetscape
///     quality a|b|c   pin a tier (auto off). quality auto resumes. quality + / - nudge
///     recenter        capture        state
///
/// Every run also drops <see cref="StateFileName"/> next to it, so the current numbers can be
/// pulled without sending anything at all.
/// </summary>
public class RemoteControl : MonoBehaviour
{
    public const string CommandFileName = "command.txt";
    public const string ResultFileName = "remote_result.txt";
    public const string StateFileName = "state.txt";

    [Tooltip("How often to look for a pushed command file. Cheap: a File.Exists and a " +
             "timestamp compare.")]
    [SerializeField] float pollIntervalSeconds = 0.25f;

    [Tooltip("How often to rewrite state.txt so it can be pulled at any moment. 0 disables.")]
    [SerializeField] float stateIntervalSeconds = 1f;

    [Tooltip("Echo every command and its result to the log as well, so `adb logcat -s Unity:V` " +
             "shows the same story as the result file.")]
    [SerializeField] bool logCommands = true;

    [Header("Wiring — all found automatically if left empty")]
    [SerializeField] GeospatialController geospatial;
    [SerializeField] AlignmentNudge nudge;
    [SerializeField] LightingController lighting;
    [SerializeField] BuildingLoader loader;
    [SerializeField] DepthOcclusion depth;
    [SerializeField] AdaptiveQuality quality;

    float _pollTimer;
    float _stateTimer;
    DateTime _lastHandled = DateTime.MinValue;

    string CommandPath => Path.Combine(Application.persistentDataPath, CommandFileName);
    string ResultPath => Path.Combine(Application.persistentDataPath, ResultFileName);
    string StatePath => Path.Combine(Application.persistentDataPath, StateFileName);

    void Awake()
    {
        if (geospatial == null) geospatial = FindAnyObjectByType<GeospatialController>();
        if (nudge == null) nudge = FindAnyObjectByType<AlignmentNudge>();
        if (lighting == null) lighting = FindAnyObjectByType<LightingController>();
        if (loader == null) loader = FindAnyObjectByType<BuildingLoader>();

        // Added in code rather than to the scene deliberately: the scene is the one thing that
        // cannot be edited while the Editor holds it, and this switch had to exist on the
        // phone the same day. It carries its own defaults, so nothing is lost by not being
        // serialized in the scene.
        if (depth == null) depth = FindAnyObjectByType<DepthOcclusion>();
        if (depth == null) depth = gameObject.AddComponent<DepthOcclusion>();
        if (quality == null) quality = FindAnyObjectByType<AdaptiveQuality>();

        Debug.Log($"[Remote] listening on {CommandPath}");
    }

    void Update()
    {
        _pollTimer += Time.deltaTime;
        if (_pollTimer >= pollIntervalSeconds)
        {
            _pollTimer = 0f;
            PollForCommand();
        }

        if (stateIntervalSeconds <= 0f) return;

        _stateTimer += Time.deltaTime;
        if (_stateTimer >= stateIntervalSeconds)
        {
            _stateTimer = 0f;
            TryWrite(StatePath, BuildStateReport());
        }
    }

    void PollForCommand()
    {
        string path = CommandPath;

        try
        {
            if (!File.Exists(path)) return;

            // Deleting the file after handling is the primary "consumed" signal, but a failed
            // delete must not turn into an infinite re-execution loop — so the write time is
            // checked too. Either mechanism alone is enough.
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (stamp <= _lastHandled) return;
            _lastHandled = stamp;

            string text = File.ReadAllText(path);
            string transcript = Execute(text);

            try { File.Delete(path); }
            catch (Exception e) { transcript += $"\n(could not delete {CommandFileName}: {e.Message})"; }

            TryWrite(ResultPath, transcript);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Remote] poll failed: {e}");
        }
    }

    /// <summary>Runs a whole command file and returns the transcript written to the result.</summary>
    public string Execute(string text)
    {
        var report = new StringBuilder();
        report.AppendLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine;

            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);

            line = line.Trim();
            if (line.Length == 0) continue;

            string outcome;
            try
            {
                outcome = RunOne(line);
            }
            catch (Exception e)
            {
                outcome = $"ERROR {e.Message}";
            }

            report.AppendLine($"{line,-24} -> {outcome}");
            if (logCommands) Debug.Log($"[Remote] {line} -> {outcome}");
        }

        report.AppendLine();
        report.Append(BuildStateReport());
        return report.ToString();
    }

    string RunOne(string line)
    {
        string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string op = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1] : "";

        switch (op)
        {
            case "rot":
            case "heading": return Adjust(AlignmentNudge.Param.Rotate, arg);
            case "scale": return Adjust(AlignmentNudge.Param.Scale, arg);
            case "scalev": return Adjust(AlignmentNudge.Param.ScaleV, arg);
            case "east": return Adjust(AlignmentNudge.Param.East, arg);
            case "north": return Adjust(AlignmentNudge.Param.North, arg);
            case "up":
            case "height": return Adjust(AlignmentNudge.Param.Height, arg);

            case "save":
                if (geospatial == null) return "no controller";
                geospatial.SaveAdjustment();
                return nudge != null && nudge.LastSaveMessage != "" ? nudge.LastSaveMessage : "saved";

            case "reset":
                if (nudge == null) return "no nudge";
                nudge.ResetAll();
                return "all adjustments reset";

            case "clear":
                if (nudge == null) return "no nudge";
                nudge.ClearSaved();
                return "saved entry cleared";

            case "reload":
                if (geospatial == null) return "no controller";
                geospatial.ReloadSite();
                return "re-reading buildings.json and re-placing";

            case "recenter":
                if (geospatial == null) return "no controller";
                geospatial.PositionPreview();
                return "preview recentred";

            case "capture":
                return DebugCapture.Take(geospatial, loader, nudge, lighting,
                    geospatial != null ? geospatial.GetComponent<BuildingPlacement>() : null);

            case "preview":
                if (geospatial == null) return "no controller";
                geospatial.SetPreview(OnOff(arg));
                return $"preview {(geospatial.PreviewActive ? "on" : "off")}";

            // The only occluder there is. `occlude`, `cut` and `mesh` were streetscape
            // geometry and are gone — see the occlusion section in CLAUDE.md.
            case "occlude":
            case "depth":
                if (depth == null) return "no depth switch";
                depth.Enabled = OnOff(arg);
                return $"depth occlusion {(depth.Enabled ? "on" : "off")}";

            // Diagnostic, not a look: paints the shadow catcher green where the shadow map
            // says lit and red where it says shadowed.
            case "catcher":
                if (geospatial == null) return "no controller";
                geospatial.ShadowCatcherDebug = OnOff(arg);
                return $"catcher debug {(geospatial.ShadowCatcherDebug ? "on" : "off")}";

            case "sun":
                if (lighting == null) return "no lighting";
                lighting.ForceDaylight = OnOff(arg);
                return $"forced daylight {(lighting.ForceDaylight ? "on" : "off")}";

            case "aspect":
                if (nudge == null) return "no nudge";
                nudge.SetKeepAspect(OnOff(arg));
                return $"keep aspect {(OnOff(arg) ? "on" : "off")}";

            case "quality":
            case "tier":
                return RunQuality(arg);

            case "state":
                return "state written";

            default:
                return $"UNKNOWN command '{op}'";
        }
    }

    /// <summary>
    /// A leading + or - means "change by this much", anything else means "set to this".
    /// That distinction is the whole ergonomics of the thing on site: nudging is what you do
    /// forty times, and it should not require knowing the current value.
    /// </summary>
    string Adjust(AlignmentNudge.Param param, string arg)
    {
        if (nudge == null) return "no nudge";
        if (arg.Length == 0) return $"{nudge.LabelOf(param)} is {nudge.GetValue(param):F3}";

        bool relative = arg[0] == '+' || arg[0] == '-';

        if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            return $"ERROR '{arg}' is not a number";

        float before = nudge.GetValue(param);
        float after = relative ? before + value : value;

        nudge.SetValue(param, after);

        float applied = nudge.GetValue(param);   // may have been clamped
        return $"{nudge.LabelOf(param)} {before:F3} -> {applied:F3}"
             + (Mathf.Abs(applied - after) > 0.0001f ? " (clamped)" : "");
    }

    string RunQuality(string arg)
    {
        if (quality == null) return "no AdaptiveQuality";

        string a = arg.ToLowerInvariant();
        switch (a)
        {
            case "":
            case "state":
                return quality.StateReport.Replace('\n', ' ');
            case "auto":
            case "on":
                return quality.ResumeAuto();
            case "a":
            case "0":
                return quality.ForceTier(0);
            case "b":
            case "1":
                return quality.ForceTier(1);
            case "c":
            case "2":
                return quality.ForceTier(2);
            case "+":
                return quality.ForceTier(quality.CurrentTier - 1);
            case "-":
                return quality.ForceTier(quality.CurrentTier + 1);
            default:
                return "quality a|b|c|auto|+|-";
        }
    }

    static bool OnOff(string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "off":
            case "0":
            case "false":
            case "no": return false;
            default: return true;   // bare "occlude" means turn it on
        }
    }

    /// <summary>
    /// Writing must never take the app down: the phone is on a tripod pointed at a building
    /// and a crash costs a whole visit.
    /// </summary>
    static void TryWrite(string path, string contents)
    {
        try
        {
            File.WriteAllText(path, contents);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Remote] could not write {Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>
    /// Everything the components know, in one pullable file. Same StateReport properties the
    /// capture button uses, so this cannot drift out of step with the code either.
    /// </summary>
    string BuildStateReport()
    {
        var report = new StringBuilder();
        report.AppendLine($"# state {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (geospatial != null) report.AppendLine(geospatial.StateReport);
        if (nudge != null) report.AppendLine(nudge.StateReport);
        if (loader != null) report.AppendLine(loader.StateReport);
        if (lighting != null) report.AppendLine(lighting.StateReport);
        if (depth != null) report.AppendLine(depth.StateReport);
        if (quality != null) report.AppendLine(quality.StateReport);

        return report.ToString();
    }
}
