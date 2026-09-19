using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Makes a mirrored copy of a humanoid clip, so the other hand's version is the SAME
/// performance rather than a different take.
/// </summary>
/// <remarks>
/// Undisputed's left and right jabs are separate captures and do not match: the right takes
/// 556 ms to land, the left 280 ms. No amount of tuning makes two different performances agree,
/// because the difference is in the mocap.
///
/// Mirroring solves it by construction. A humanoid clip carries <c>m_Mirror</c> in its
/// AnimationClipSettings; with it on, Unity plays the clip left-right reversed through the
/// humanoid rig. Mirror the right jab and the left is the same motion, the same length, the
/// same strike frame — a matched pair.
///
/// The copy is made by duplicating the .anim FILE and flipping that one line, not by
/// <c>Object.Instantiate</c>. Instantiating a humanoid clip and saving it can silently write a
/// hollow asset, because the motion the API sees is not always the motion in the file.
/// </remarks>
public static class ClipMirrorTool
{
    [MenuItem("Tools/Undisputed/Boxing/Mirror Selected Clips")]
    private static void MirrorSelected()
    {
        var clips = new List<AnimationClip>();
        foreach (Object o in Selection.objects)
        {
            if (o is AnimationClip clip)
                clips.Add(clip);
        }

        if (clips.Count == 0)
        {
            Debug.LogError("Mirror: select one or more AnimationClip assets in the Project window.");
            return;
        }

        int made = 0;
        foreach (AnimationClip clip in clips)
        {
            AnimationClip result = Mirror(clip);
            if (result != null)
            {
                made++;
                Debug.Log($"Mirror: '{clip.name}'  ->  '{result.name}'  " +
                          $"({result.length:0.00}s, identical timing to the source)", result);
            }
        }

        if (made > 0)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        Debug.Log($"Mirror: {made} clip(s) written.");
    }

    [MenuItem("Tools/Undisputed/Boxing/Mirror Selected Clips", true)]
    private static bool ValidateMirror()
    {
        foreach (Object o in Selection.objects)
            if (o is AnimationClip)
                return true;
        return false;
    }

    /// <summary>
    /// Writes a mirrored duplicate next to the source and returns it.
    /// </summary>
    public static AnimationClip Mirror(AnimationClip clip)
    {
        string src = AssetDatabase.GetAssetPath(clip);
        if (string.IsNullOrEmpty(src) || !src.EndsWith(".anim"))
        {
            Debug.LogError($"Mirror: '{clip.name}' is not a standalone .anim asset " +
                "(clips inside an FBX cannot be mirrored this way — duplicate them out first).", clip);
            return null;
        }

        string dst = AssetDatabase.GenerateUniqueAssetPath(
            Path.Combine(Path.GetDirectoryName(src), MirroredName(clip.name) + ".anim")
                .Replace('\\', '/'));

        if (!AssetDatabase.CopyAsset(src, dst))
        {
            Debug.LogError($"Mirror: could not copy '{src}'.", clip);
            return null;
        }

        AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);

        string full = Path.GetFullPath(dst);
        string text = File.ReadAllText(full);

        string flipped = Regex.Replace(text, @"^(\s+m_Mirror:\s*)0\s*$", "${1}1",
            RegexOptions.Multiline);

        if (flipped == text)
        {
            // Already mirrored, or the field is missing entirely.
            if (Regex.IsMatch(text, @"^\s+m_Mirror:\s*1\s*$", RegexOptions.Multiline))
                Debug.LogWarning($"Mirror: '{clip.name}' was ALREADY mirrored; the copy matches it.", clip);
            else
                Debug.LogError($"Mirror: no m_Mirror field in '{clip.name}' — is it a humanoid clip?", clip);
        }
        else
        {
            File.WriteAllText(full, flipped);
        }

        // Rename inside the file too, or the asset keeps the source's name in the Inspector.
        text = File.ReadAllText(full);
        text = Regex.Replace(text, @"^(\s+m_Name:\s*).*$",
            "${1}" + Path.GetFileNameWithoutExtension(dst), RegexOptions.Multiline);
        File.WriteAllText(full, text);

        AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<AnimationClip>(dst);
    }

    /// <summary>
    /// Swaps the hand token so the name describes what the clip now does, and marks it a mirror
    /// so it is never mistaken for an original capture.
    /// </summary>
    private static string MirroredName(string name)
    {
        string swapped = Regex.Replace(name, @"\b([LR])(Jab|Hook|Uppercut)\b",
            m => (m.Groups[1].Value == "L" ? "R" : "L") + m.Groups[2].Value);

        if (swapped != name)
            return swapped + "_Mirror";

        // Otherwise swap the words, through a placeholder so the second pass cannot undo the first.
        const string Token = "__HAND__";
        swapped = name.Replace("Left", Token).Replace("Right", "Left").Replace(Token, "Right");

        return swapped + "_Mirror";
    }
}
