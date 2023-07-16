#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class AssetBundleBuilder
{
    [MenuItem("TakoTako/Build Asset Bundles")]
    public static void BuildAllAssetBundles()
    {
        const string tempDirectory = "./Temp/AssetBundlesTemp";
        const string finalDirectory = "../TakoTako/IncludedContent";

        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);

        Directory.CreateDirectory(tempDirectory);

        BuildPipeline.BuildAssetBundles(tempDirectory,
            BuildAssetBundleOptions.AssetBundleStripUnityVersion | BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows);

        var assetPath = Path.Combine(tempDirectory, "content");
        if (!File.Exists(Path.Combine(tempDirectory, "content")))
        {
            Debug.LogError($"Cannot find {tempDirectory}");
            return;
        }

        var finalPath = Path.Combine(finalDirectory, "content");
        File.Copy(assetPath, finalPath, true);
        Directory.Delete(tempDirectory, true);
    }
}

#endif
