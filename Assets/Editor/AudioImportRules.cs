using System.IO;
using UnityEditor;
using UnityEngine;

// Import settings for everything under Assets/Audio, applied on a clip's first import and re-applied to all
// clips by Tools > Audio > Apply Import Settings:
//   short SFX:  Decompress On Load, Vorbis, preloaded
//   loops:      Streaming, Vorbis (name contains "Loop", or anything under Ambience)
//   mono:       every 3D sound is forced to mono; stereo is kept only for first-person (*_FP_*), UI and ambience beds
public class AudioImportRules : AssetPostprocessor
{
    public const string Root = "Assets/Audio/";

    void OnPreprocessAudio()
    {
        if (assetPath.StartsWith(Root) && assetImporter.importSettingsMissing) Apply((AudioImporter)assetImporter, assetPath);
    }

    public static bool IsLoop(string path) =>
        path.Contains("/Ambience/") || Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Contains("loop");

    public static bool KeepStereo(string path) =>
        path.Contains("/UI/") || path.Contains("/Ambience/") || Path.GetFileName(path).Contains("_FP_");

    // Returns true if anything changed.
    public static bool Apply(AudioImporter importer, string path)
    {
        bool loop = IsLoop(path);
        var s = importer.defaultSampleSettings;
        var want = s;
        want.loadType = loop ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
        want.compressionFormat = AudioCompressionFormat.Vorbis;
        want.quality = loop ? 0.6f : 0.75f;
        want.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
        want.preloadAudioData = !loop;
        bool mono = !KeepStereo(path);

        bool changed = !s.Equals(want) || importer.forceToMono != mono || importer.loadInBackground != loop;
        if (!changed) return false;
        importer.defaultSampleSettings = want;
        importer.forceToMono = mono;
        importer.loadInBackground = loop;
        return true;
    }
}
