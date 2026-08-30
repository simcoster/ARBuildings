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
///     seg on|off      semantic occlusion. PASCAL things expand ARCore depth; alpha
///                     mattes (MODNet) occlude from the silhouette even without a depth hit.
///     seg cpu|gpu|npu|gpudec  interpreter: XNNPACK / NNAPI-hybrid / ENN / Mali GpuDelegate
///     segint N        seconds between submits (0 = as soon as the worker is free)
///     segmin N        pixels of ARCore-depth overlap before a segment is expanded
///     segdebug on|off tint accepted segments
///     segmax N        max occlusion distance in metres (0 = no cap)
///     segbox on|off   occlude the whole bounding box of an object, not its silhouette
///     segrot N        rotate the camera image N deg clockwise before inference (0/90/180/270)
///     segcrop on|off  centred square crop instead of squashing the whole frame
///     segdump         write the exact image the network was handed, and its mask, as PNGs
///     segmodel FILE   swap the .tflite at runtime — pair it with pushing one to the device
///                     `canny` is built in (CPU edges, no file)
///     segnext         next model in the cycle — the same order as the HUD's model button
///     seglist         every model available, shipped or pushed, and which is live
///     segxnn on|off   XNNPACK, or TFLite's built-in kernels for graphs XNNPACK refuses
///     segnorm M S     input normalisation (v-M)/S. 127.5 127.5 for DeepLab, 0 255 for [0,1]
///     segkind K       auto|labels|alpha|depth — how to read a one-channel float output
///     segfloor N      0-255 threshold on the scalar view; raise it to isolate a peak
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
    [SerializeField] SemanticOcclusion seg;
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
        if (seg == null) seg = FindAnyObjectByType<SemanticOcclusion>();
        if (seg == null) seg = gameObject.AddComponent<SemanticOcclusion>();
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

            case "seg":
                if (seg == null) return "no semantic occlusion";
                switch (arg.ToLowerInvariant())
                {
                    case "cpu": return seg.SetBackend(SemanticOcclusion.SegBackend.Cpu);
                    case "gpu": return seg.SetBackend(SemanticOcclusion.SegBackend.Gpu);
                    case "gpudec": return seg.SetBackend(SemanticOcclusion.SegBackend.GpuDec);
                    case "npu": return seg.SetBackend(SemanticOcclusion.SegBackend.Npu);
                    default:
                        seg.Enabled = OnOff(arg);
                        return $"seg {(seg.Enabled ? "on" : "off")} {SemanticOcclusion.LabelOf(seg.Backend)}";
                }

            case "segint":
                if (seg == null) return "no semantic occlusion";
                if (arg.Length == 0) return $"segint {seg.InferIntervalSeconds:F3}";
                if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float segInt))
                    return $"ERROR '{arg}' is not a number";
                return seg.SetInferInterval(segInt);

            case "segmin":
                if (seg == null) return "no semantic occlusion";
                if (arg.Length == 0) return $"segmin {seg.MinVotePixels}";
                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minPx))
                    return $"ERROR '{arg}' is not an int";
                seg.MinVotePixels = minPx;
                return $"segmin {seg.MinVotePixels}";

            case "segdebug":
                if (seg == null) return "no semantic occlusion";
                seg.DebugTint = OnOff(arg);
                return $"segdebug {(seg.DebugTint ? "on" : "off")}";

            // The CPU image arrives in sensor orientation, so an upright object reaches the
            // network lying down. Which quarter turn corrects it is a device fact, so it is
            // a knob: dialling it is free, rebuilding to try the other one is not.
            case "segrot":
                if (seg == null) return "no semantic occlusion";
                if (arg.Length == 0) return $"segrot {seg.RotationDegrees}";
                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rotDeg))
                    return $"ERROR '{arg}' is not an int";
                seg.RotationDegrees = rotDeg;
                return $"segrot {seg.RotationDegrees} deg clockwise before inference";

            case "segcrop":
                if (seg == null) return "no semantic occlusion";
                seg.CentreCrop = OnOff(arg);
                return $"segcrop {(seg.CentreCrop ? "on — centred square, no squash" : "off — whole frame squashed")}";

            case "segdump":
                if (seg == null) return "no semantic occlusion";
                return seg.DumpInput();

            case "segmodel":
                if (seg == null) return "no semantic occlusion";
                return seg.SetModel(arg);

            case "segnext":
                if (seg == null) return "no semantic occlusion";
                return seg.CycleModel();

            case "seglist":
                if (seg == null) return "no semantic occlusion";
                return seg.ListModels();

            case "segxnn":
                if (seg == null) return "no semantic occlusion";
                seg.UseXnnpack = OnOff(arg);
                return $"segxnn {(seg.UseXnnpack ? "on (XNNPACK)" : "off (TFLite built-in kernels)")}";

            case "segbox":
                if (seg == null) return "no semantic occlusion";
                seg.BoundingBox = OnOff(arg);
                return $"segbox {(seg.BoundingBox ? "on — whole box occludes" : "off — silhouette occludes")}";

            // (v - mean) / scale. DeepLab float32 wants 127.5 127.5; 0 255 is the [0,1]
            // reading that returns background everywhere and looks like a blind model.
            case "segnorm":
                if (seg == null) return "no semantic occlusion";
                if (parts.Length < 3) return "ERROR segnorm needs <mean> <scale>";
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float nMean) ||
                    !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float nScale))
                    return $"ERROR '{parts[1]} {parts[2]}' is not two numbers";
                return seg.SetNormalization(nMean, nScale);

            // A matte and a depth map are both [1,H,W,1] FLOAT32, so the shape cannot tell
            // them apart. auto guesses from the observed range; this overrides the guess.
            case "segkind":
                if (seg == null) return "no semantic occlusion";
                return seg.SetOutputKind(arg);

            // Threshold on the scalar view. A compressed sigmoid needs this raised before
            // the peak can be told from the floor it is sitting on.
            case "segfloor":
                if (seg == null) return "no semantic occlusion";
                if (!int.TryParse(arg, out int sFloor))
                    return $"ERROR '{arg}' is not a number 0-255";
                return seg.SetScalarFloor(sFloor);

            case "segmax":
                if (seg == null) return "no semantic occlusion";
                if (arg.Length == 0) return $"segmax {seg.MaxOcclusionDistance:F1}";
                if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float maxM))
                    return $"ERROR '{arg}' is not a number";
                seg.MaxOcclusionDistance = maxM;
                return $"segmax {seg.MaxOcclusionDistance:F1}";

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
        if (seg != null) report.AppendLine(seg.StateReport);
        if (quality != null) report.AppendLine(quality.StateReport);
        var perf = FindAnyObjectByType<PerfMeters>();
        if (perf != null) report.AppendLine(perf.StateReport);

        return report.ToString();
    }
}
