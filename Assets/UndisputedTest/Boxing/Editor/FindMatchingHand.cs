using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ranks every right-hand punch by how closely its arm moves like the left-hand clip you are
/// already happy with.
/// </summary>
/// <remarks>
/// Judging this by filename does not work — the names describe the tag, not the motion. So the
/// clip is sampled on the real rig: the punching hand's path is recorded in the CHEST's local
/// space over normalised time, which strips out where the performer stood and how long the take
/// was, leaving only the shape of the punch. The right hand's path is mirrored across X before
/// comparing, so a left and a right punch that are the same motion score near zero.
///
/// Reads whatever is currently in <see cref="AimStudyBoxer.LeftJab"/>, so it always compares
/// against the clip you are really using.
/// </remarks>
public static class FindMatchingHand
{
    private const string PunchRoot = "Assets/Animations/Undisputed/Orthodox/Punches";
    private const int Samples = 40;

    [MenuItem("Tools/Undisputed/Boxing/Find Matching Right Hand")]
    private static void Run()
    {
        AimStudyBoxer study = Object.FindAnyObjectByType<AimStudyBoxer>();
        if (study == null)
        {
            Debug.LogError("Match: no AimStudyBoxer in the scene. Build the Aim Study Boxer first.");
            return;
        }

        Animator animator = study.GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogError("Match: the boxer needs a Humanoid Animator.", study);
            return;
        }

        if (study.LeftJab == null)
        {
            Debug.LogError("Match: LeftJab is empty on the study rig — assign the left clip you " +
                "want matched, then run this again.", study);
            return;
        }

        List<AnimationClip> candidates = RightHandClips();
        if (candidates.Count == 0)
        {
            Debug.LogError("Match: no right-hand punch clips found under " + PunchRoot);
            return;
        }

        var results = new List<(AnimationClip clip, float shape, float length)>();

        AnimationMode.StartAnimationMode();
        try
        {
            Vector3[] reference = Path(animator, study.LeftJab, left: true);

            for (int i = 0; i < candidates.Count; i++)
            {
                AnimationClip c = candidates[i];
                EditorUtility.DisplayProgressBar("Comparing punches", c.name,
                    (i + 1f) / candidates.Count);

                Vector3[] path = Path(animator, c, left: false);
                results.Add((c, Difference(reference, path), c.length));
            }
        }
        finally
        {
            AnimationMode.StopAnimationMode();
            EditorUtility.ClearProgressBar();
        }

        results.Sort((a, b) => a.shape.CompareTo(b.shape));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Right-hand clips ranked against '{study.LeftJab.name}' " +
                      $"({study.LeftJab.length:0.00}s):");
        sb.AppendLine();
        sb.AppendLine($"   {"shape",-8}{"len",-8}{"dlen",-8}clip");
        sb.AppendLine("   " + new string('-', 92));

        foreach (var r in results.Take(12))
        {
            sb.AppendLine($"   {r.shape,-8:0.000}{r.length,-8:0.00}" +
                          $"{Mathf.Abs(r.length - study.LeftJab.length),-8:0.00}{r.clip.name}");
        }

        sb.AppendLine();
        sb.AppendLine("   shape = how differently the arm travels, measured in the chest's own frame");
        sb.AppendLine("   with the right hand mirrored. 0 would be the same motion. Under ~0.05 is");
        sb.AppendLine("   a genuine pair; above ~0.15 it is a different punch wearing the same tag.");
        sb.AppendLine();
        sb.AppendLine("   If nothing scores low, mirror the left clip instead — that is exactly the");
        sb.AppendLine("   case Mirror Selected Clips exists for.");

        Debug.Log(sb.ToString(), study);
    }

    private static List<AnimationClip> RightHandClips()
    {
        var list = new List<AnimationClip>();

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { PunchRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                continue;

            string n = clip.name;
            string low = n.ToLowerInvariant();

            if (low.Contains("lowblow") || low.Contains("block") || low.Contains("dash"))
                continue;

            if (n.Contains("RJab") || n.Contains("RHook") || n.Contains("RUppercut") || n.Contains("_R_"))
                list.Add(clip);
        }

        return list;
    }

    /// <summary>
    /// The punching hand's path in the chest's local space, over normalised time.
    /// </summary>
    /// <remarks>
    /// Chest-local rather than world, so where the performer stood and which way he faced drop
    /// out. Normalised time, so a long clip and a short one are still comparable. Scaled by arm
    /// length so it is a shape, not a size. The right hand is mirrored across X so the two sides
    /// can be compared directly.
    /// </remarks>
    private static Vector3[] Path(Animator animator, AnimationClip clip, bool left)
    {
        Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest)
                       ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        Transform upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm
                                                         : HumanBodyBones.RightUpperArm);
        Transform hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand
                                                        : HumanBodyBones.RightHand);

        var path = new Vector3[Samples];
        if (chest == null || upper == null || hand == null)
            return path;

        float length = Mathf.Max(0.01f, clip.length);

        for (int i = 0; i < Samples; i++)
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(animator.gameObject, clip, i / (Samples - 1f) * length);
            AnimationMode.EndSampling();

            float arm = Vector3.Distance(upper.position, hand.position);
            float scale = Mathf.Max(0.01f, Vector3.Distance(chest.position, upper.position) * 4f);

            Vector3 local = chest.InverseTransformPoint(hand.position) / scale;
            if (!left)
                local.x = -local.x;          // mirror, so the sides are comparable

            path[i] = local;
        }

        return path;
    }

    private static float Difference(Vector3[] a, Vector3[] b)
    {
        float total = 0f;
        int n = Mathf.Min(a.Length, b.Length);

        for (int i = 0; i < n; i++)
            total += (a[i] - b[i]).sqrMagnitude;

        return n > 0 ? Mathf.Sqrt(total / n) : 99f;
    }
}
