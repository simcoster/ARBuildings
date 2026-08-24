using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Reads site data from StreamingAssets/buildings.json so the coordinates live in a file
/// that can be edited, diffed and reviewed — not typed into the inspector one field at a
/// time, where the only copy is inside a scene and a typo is invisible.
///
/// The file is authoritative: anything it specifies overrides the inspector at startup.
/// Leave a field out and the inspector value stands.
/// </summary>
public static class SiteCatalog
{
    [Serializable]
    public class Corner
    {
        public double latitude;
        public double longitude;
    }

    [Serializable]
    public class Footprint
    {
        public Corner cornerA;
        public Corner cornerB;
    }

    [Serializable]
    public class Site
    {
        public string id;
        public string name;
        public string model;

        public double latitude;
        public double longitude;
        public double altitudeAboveTerrain;

        public float headingDeg;
        public float headingOffsetFromABDeg;
        public float modelFrontOffsetDeg;

        /// <summary>"FootprintWidth" | "TargetHeight" | "FixedScale". Blank = leave as set.</summary>
        public string sizeMode;

        /// <summary>"X" or "Z" — which model axis is being fitted.</summary>
        public string footprintAxis;

        /// <summary>
        /// Metres to fit that axis to. Overrides the corner-to-corner distance, because
        /// the dimension worth fitting is often NOT the one that was pinned — here the
        /// pins measure a narrow entrance block while the model box spans the main mass.
        /// 0 = use the pinned distance.
        /// </summary>
        public float fitMetres;

        public Footprint footprint;

        /// <summary>True when both corners are present, i.e. footprint mode is usable.</summary>
        public bool HasFootprint =>
            footprint != null && footprint.cornerA != null && footprint.cornerB != null &&
            Math.Abs(footprint.cornerA.latitude) > 0.0001 &&
            Math.Abs(footprint.cornerB.latitude) > 0.0001;
    }

    [Serializable]
    class Catalog
    {
        public Site[] buildings;
    }

    public const string FileName = "buildings.json";

    /// <summary>Which copy was used last: "device" or "apk". Shown in the HUD.</summary>
    public static string LastSource { get; private set; } = "none";

    /// <summary>
    /// A writable override, so coordinates can be corrected with `adb push` instead of a
    /// rebuild. The APK copy is the seed and the fallback.
    /// </summary>
    public static string DevicePath =>
        System.IO.Path.Combine(Application.persistentDataPath, FileName);

    /// <summary>
    /// Fetches the entry for a site id. Uses UnityWebRequest for the same reason the GLB
    /// loader does: on Android, StreamingAssets lives inside the APK and cannot be opened
    /// as a file.
    /// </summary>
    public static IEnumerator Load(string siteId, Action<Site> onDone)
    {
        // Device copy first: pushing one over adb turns a wrong coordinate from a 15-minute
        // rebuild into a file copy and an app restart.
        if (System.IO.File.Exists(DevicePath))
        {
            LastSource = "device";
            Debug.Log($"[Sites] using DEVICE copy {DevicePath}");
            Deliver(System.IO.File.ReadAllText(DevicePath), siteId, onDone);
            yield break;
        }

        LastSource = "apk";

        var path = $"{Application.streamingAssetsPath}/{FileName}";
        var url = path.Contains("://") ? path : $"file://{path}";

        using var request = UnityWebRequest.Get(url);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[Sites] could not read {url}: {request.error} — " +
                             "falling back to the inspector values.");
            onDone(null);
            yield break;
        }

        Deliver(request.downloadHandler.text, siteId, onDone);
    }

    static void Deliver(string json, string siteId, Action<Site> onDone)
    {
        Catalog catalog;
        try
        {
            catalog = JsonUtility.FromJson<Catalog>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Sites] {FileName} is not valid JSON: {e.Message}");
            onDone(null);
            return;
        }

        if (catalog?.buildings == null || catalog.buildings.Length == 0)
        {
            Debug.LogWarning($"[Sites] {FileName} has no buildings array.");
            onDone(null);
            return;
        }

        var site = Array.Find(catalog.buildings, b => b != null && b.id == siteId);

        if (site == null)
        {
            Debug.LogWarning($"[Sites] no entry for '{siteId}' in {FileName}. " +
                             $"Known ids: {string.Join(", ", Array.ConvertAll(catalog.buildings, b => b?.id))}");
            onDone(null);
            return;
        }

        Debug.Log($"[Sites] loaded '{site.id}' ({site.name}) from {FileName}");
        onDone(site);
    }
}
