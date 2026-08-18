using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// One button that snapshots everything needed to diagnose a placement remotely: a PNG of
/// exactly what was on screen, and a text dump of every piece of state that produced it.
///
/// The pair matters. A screenshot alone can't say whether north was measured or guessed,
/// whether a building was ghosted, or what the saved coordinates are; a text dump alone
/// can't show that the facade is two metres left of where it should be. Together they
/// usually settle a question that would otherwise take a round trip to the site.
/// </summary>
public static class DebugCapture
{
    public const string FolderName = "captures";

    public static string Folder => Path.Combine(Application.persistentDataPath, FolderName);

    /// <summary>
    /// Writes capture_&lt;stamp&gt;.png and .txt. Returns a short message for the HUD.
    /// The screenshot lands a frame or two later — Unity writes it at end of frame — so the
    /// text file is written first and carries the same stamp.
    /// </summary>
    public static string Take(
        GeospatialController geospatial,
        BuildingLoader loader,
        AlignmentNudge nudge,
        LightingController lighting,
        StreetscapeShadowSetup streetscape,
        BuildingPlacement placement)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        try
        {
            Directory.CreateDirectory(Folder);

            string textPath = Path.Combine(Folder, $"capture_{stamp}.txt");
            // RELATIVE, not absolute: on Android ScreenCapture.CaptureScreenshot
            // resolves against persistentDataPath and silently writes nothing for an
            // absolute path — which is why the first batch produced .txt but no .png.
            string imagePath = $"{FolderName}/capture_{stamp}.png";

            File.WriteAllText(textPath,
                BuildReport(stamp, geospatial, loader, nudge, lighting, streetscape, placement));

            // Captures the composited frame including this HUD, which is deliberate: the
            // on-screen numbers and the dump can then be checked against each other.
            ScreenCapture.CaptureScreenshot(imagePath);

            Debug.Log($"[Capture] wrote capture_{stamp}.png/.txt to {Folder}");
            return $"captured {stamp}";
        }
        catch (Exception e)
        {
            Debug.LogError($"[Capture] failed: {e.Message}");
            return "capture FAILED";
        }
    }

    static string BuildReport(
        string stamp,
        GeospatialController geospatial,
        BuildingLoader loader,
        AlignmentNudge nudge,
        LightingController lighting,
        StreetscapeShadowSetup streetscape,
        BuildingPlacement placement)
    {
        var text = new StringBuilder();

        text.AppendLine($"AR_Buildings capture {stamp}");
        text.AppendLine(new string('=', 60));
        text.AppendLine();

        Section(text, "DEVICE",
            $"model              : {SystemInfo.deviceModel}\n" +
            $"os                 : {SystemInfo.operatingSystem}\n" +
            $"graphics           : {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})\n" +
            $"memory             : {SystemInfo.systemMemorySize} MB, {SystemInfo.processorCount} cores\n" +
            $"screen             : {Screen.width}x{Screen.height}\n" +
            $"quality level      : {QualitySettings.GetQualityLevel()} " +
            $"({QualitySettings.names[QualitySettings.GetQualityLevel()]})\n" +
            $"target frame rate  : {Application.targetFrameRate}\n" +
            $"unity              : {Application.unityVersion}\n");

        Section(text, "PLACEMENT", geospatial != null ? geospatial.StateReport : "no controller");

        if (placement != null)
            Section(text, "HEADING / FOOTPRINT",
                $"{placement.PlacementReadout}\n" +
                $"model front offset : {placement.ModelFrontOffsetDeg} deg\n" +
                $"footprint mode     : {placement.UseFootprint}\n" +
                $"footprint length   : {placement.FootprintLengthMetres:F2} m\n" +
                $"effective heading  : {placement.EffectiveHeadingDeg:F2} deg\n");

        Section(text, "MODEL", loader != null ? loader.StateReport : "no loader");
        Section(text, "ADJUSTMENT", nudge != null ? nudge.StateReport : "no nudge");
        Section(text, "OCCLUSION", streetscape != null ? streetscape.StateReport : "no streetscape");
        Section(text, "LIGHTING", lighting != null ? lighting.StateReport : "no lighting");

        var camera = Camera.main;
        if (camera != null)
            Section(text, "CAMERA",
                $"position           : {camera.transform.position}\n" +
                $"euler              : {camera.transform.eulerAngles}\n" +
                $"fov / near / far   : {camera.fieldOfView:F1} / {camera.nearClipPlane} / {camera.farClipPlane}\n");

        return text.ToString();
    }

    static void Section(StringBuilder text, string title, string body)
    {
        text.AppendLine($"--- {title} " + new string('-', Math.Max(0, 56 - title.Length)));
        text.AppendLine(body.TrimEnd());
        text.AppendLine();
    }
}
