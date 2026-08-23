using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

/// <summary>
/// Stamps EVERY build, not just ones started from Build/Android APK.
///
/// The stamp existed already, written by BuildAndroid — but a build started from
/// File > Build Settings bypassed it, and the device then honestly reported "unstamped",
/// which is no more useful than having no stamp at all. A build preprocessor runs whichever
/// way the build was launched, which is the only version of this that can be relied on.
/// </summary>
public class BuildStampProcessor : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        const string dir = "Assets/Resources";
        const string path = dir + "/BuildStamp.txt";

        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        // Synchronous: an asset the database has not seen yet does not reach the player, and
        // the build would silently ship the PREVIOUS stamp — worse than none, because it
        // would look like a fresh build that did not contain the change.
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
    }
}
