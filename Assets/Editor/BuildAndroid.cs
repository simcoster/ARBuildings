using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Command-line Android build, so a device build no longer requires driving the Editor by
/// hand — the Editor holds a project lock, so the two are mutually exclusive and a build has
/// to be either one or the other.
///
/// Scenes come from EditorBuildSettings rather than a hard-coded list, so this cannot drift
/// out of step with what a Build &amp; Run from the Editor would produce.
///
/// Usage:
///   Unity.exe -quit -batchmode -nographics -projectPath &lt;proj&gt; -buildTarget Android \
///             -executeMethod BuildAndroid.Build -outputPath &lt;file.apk&gt; -logFile &lt;log&gt;
/// </summary>
public static class BuildAndroid
{
    const string DefaultOutput = "Builds/ARBuildings.apk";

    [MenuItem("Build/Android APK")]
    public static void Build()
    {
        string output = ArgValue("-outputPath") ?? DefaultOutput;

        var scenes = EditorBuildSettings.scenes
                                        .Where(s => s.enabled)
                                        .Select(s => s.path)
                                        .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[Build] no enabled scenes in EditorBuildSettings — nothing to build.");
            Fail();
            return;
        }

        string stamp = WriteBuildStamp();

        Debug.Log($"[Build] {stamp}");
        Debug.Log($"[Build] {scenes.Length} scene(s) -> {output}");
        foreach (var s in scenes) Debug.Log($"[Build]   {s}");

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        BuildReport report;
        try
        {
            report = BuildPipeline.BuildPlayer(options);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Build] threw: {e}");
            Fail();
            return;
        }

        var summary = report.summary;
        Debug.Log($"[Build] result={summary.result} errors={summary.totalErrors} " +
                  $"warnings={summary.totalWarnings} size={summary.totalSize / (1024 * 1024)} MB " +
                  $"time={summary.totalTime}");

        if (summary.result != BuildResult.Succeeded)
        {
            Fail();
            return;
        }

        Debug.Log($"[Build] SUCCEEDED -> {summary.outputPath}");
        Finish(0);
    }

    /// <summary>
    /// Stamps the build with a timestamp so the running app can say WHICH build it is.
    ///
    /// Without this, "did my change reach the phone?" is unanswerable from the device, and on
    /// 2026-08-23 a whole build cycle was spent testing an APK that turned out to be identical
    /// to the one already installed. The stamp goes in Resources so it survives into the
    /// player, and is reported in the capture dump and over RemoteControl `state`.
    /// </summary>
    static string WriteBuildStamp()
    {
        const string dir = "Assets/Resources";
        const string path = dir + "/BuildStamp.txt";

        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        File.WriteAllText(path, stamp);

        // Import it NOW: an asset written during a build that the database has not seen yet
        // does not make it into the player, and the stamp would silently be the previous one.
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

        return $"build stamp {stamp}";
    }

    /// <summary>Non-zero exit, or a failed batch build looks exactly like a successful one.</summary>
    static void Fail() => Finish(1);

    /// <summary>
    /// Exit the process ONLY in batchmode. The same method is reachable from the
    /// Build/Android APK menu item and from the MCP bridge, where exiting would close the
    /// user's Editor out from under them the moment a build succeeded.
    /// </summary>
    static void Finish(int code)
    {
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(code);
            return;
        }

        Debug.Log($"[Build] done (exit code would be {code}); Editor left running.");
    }

    static string ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];

        return null;
    }
}
