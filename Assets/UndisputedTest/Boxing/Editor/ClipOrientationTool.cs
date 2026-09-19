using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reports and aligns <c>m_OrientationOffsetY</c> — the Root Transform Rotation offset that
/// decides which way a humanoid clip faces.
/// </summary>
/// <remarks>
/// Undisputed's clips were captured at whatever heading the performer happened to stand at, and
/// each one carries its own offset to correct that. Within one library that is invisible, but
/// mix an idle at 180 with a jab at 192 and the upper body twists 12 degrees the moment the
/// punch blends in — which reads as "that punch is wrong" without anything obviously broken.
///
/// Aligning the offsets makes clips from different takes agree. Nothing about the motion
/// changes; only the heading the clip is played at.
/// </remarks>
public static class ClipOrientationTool
{
    private const string Menu = "Tools/Undisputed/Boxing/Orientation/";

    [MenuItem(Menu + "Report Selected", true)]
    [MenuItem(Menu + "Align Selected To 180", true)]
    private static bool Validate()
    {
        foreach (Object o in Selection.objects)
            if (o is AnimationClip)
                return true;
        return false;
    }

    [MenuItem(Menu + "Report Selected")]
    private static void Report()
    {
        foreach (AnimationClip clip in Selected())
        {
            float? off = Read(clip);
            Debug.Log(off.HasValue
                ? $"Orientation: '{clip.name}'  offsetY {off.Value:0.#}"
                : $"Orientation: '{clip.name}'  no m_OrientationOffsetY (not a standalone .anim?)",
                clip);
        }
    }

    [MenuItem(Menu + "Align Selected To 180")]
    private static void AlignTo180() => Align(180f);

    /// <summary>Writes the same offset onto every selected clip.</summary>
    public static void Align(float degrees)
    {
        int done = 0;
        foreach (AnimationClip clip in Selected())
        {
            float? was = Read(clip);
            if (!Write(clip, degrees))
                continue;

            done++;
            Debug.Log($"Orientation: '{clip.name}'  {(was.HasValue ? was.Value.ToString("0.#") : "?")}" +
                      $"  ->  {degrees:0.#}", clip);
        }

        if (done > 0)
        {
            AssetDatabase.Refresh();
            Debug.Log($"Orientation: aligned {done} clip(s) to {degrees:0.#}. Only the heading " +
                "changed — the motion is untouched.");
        }
    }

    private static IEnumerable<AnimationClip> Selected()
    {
        foreach (Object o in Selection.objects)
            if (o is AnimationClip clip)
                yield return clip;
    }

    private static float? Read(AnimationClip clip)
    {
        string path = FileOf(clip);
        if (path == null)
            return null;

        Match m = Regex.Match(File.ReadAllText(path),
            @"^\s+m_OrientationOffsetY:\s*(-?[\d.]+)\s*$", RegexOptions.Multiline);

        return m.Success ? float.Parse(m.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture) : (float?)null;
    }

    private static bool Write(AnimationClip clip, float degrees)
    {
        string path = FileOf(clip);
        if (path == null)
        {
            Debug.LogError($"Orientation: '{clip.name}' is not a standalone .anim — clips inside " +
                "an FBX carry this on the model importer instead.", clip);
            return false;
        }

        string text = File.ReadAllText(path);
        string next = Regex.Replace(text, @"^(\s+m_OrientationOffsetY:\s*)-?[\d.]+\s*$",
            "${1}" + degrees.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            RegexOptions.Multiline);

        if (next == text)
        {
            Debug.LogWarning($"Orientation: no m_OrientationOffsetY in '{clip.name}'.", clip);
            return false;
        }

        File.WriteAllText(path, next);
        AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(clip),
            ImportAssetOptions.ForceSynchronousImport);
        return true;
    }

    private static string FileOf(AnimationClip clip)
    {
        string asset = AssetDatabase.GetAssetPath(clip);
        if (string.IsNullOrEmpty(asset) || !asset.EndsWith(".anim"))
            return null;

        return Path.GetFullPath(asset);
    }
}
