using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes the Undisputed punch clips into <see cref="ReferencePose"/> strike shapes for
/// <see cref="ReferencePoseMixer"/>.
/// </summary>
/// <remarks>
/// The mixer wants single-frame anatomy, not animation: "if the boxer were frozen at a clean
/// hook, what shape is the body in?" <see cref="PunchPoseChain"/> then synthesises the chamber
/// as the anti-strike and the follow-through past it, so one honest strike pose buys the whole
/// punch. That is why this bakes poses rather than feeding whole clips through
/// <c>PunchShape.Clips</c> - the clip path fights the trajectory clock and can only play one
/// punch at a time, while a pose just makes the existing machine better shaped.
///
/// The strike frame is MEASURED, never assumed. Undisputed's real strike times run from 0.15 to
/// 0.73 of clip length, so the controller's single global <c>clipStrikeTime</c> of 0.4 (tuned
/// for Mixamo) would sample the wind-up on a fast inside shot and the recovery on a committed
/// one. Arm extension - shoulder-to-hand over arm length, on the actual rig - peaks at the
/// strike, so that peak is the frame worth freezing.
///
/// Baking the same slot from several clips is intentional: the baker turns repeats into
/// variants (RightHookHead2, 3, ...) and the mixer's variant pool rolls a fresh one per throw.
/// 52 clips across 12 slots means every punch has several authentic shapes to land in.
/// </remarks>
public static class UndisputedStrikePoseBake
{
    private const string PunchRoot = "Assets/Animations/Undisputed/Orthodox/Punches";

    // ..._OptimizedForHead_LHook_Power_Orthodox_MxM_MOD
    private static readonly Regex Tagged = new Regex(
        @"OptimizedFor(?<target>Head|Body)_(?<hand>[LR])(?<type>Jab|Hook|Uppercut)",
        RegexOptions.IgnoreCase);

    // Fallback for the clips Undisputed left untagged, e.g. SunnyEdwards_Head_R_Hook_R2_TRAD_INT4.
    private static readonly Regex Loose = new Regex(
        @"(?<hand>[LR])[_ ]?(?<type>Jab|Hook|Uppercut|Straight)", RegexOptions.IgnoreCase);

    /// <summary>Not punches, whatever their filename says.</summary>
    private static readonly string[] Exclude = { "lowblow", "foul", "block", "dash" };

    /// <summary>
    /// Leave the legs to the Legs Animator unless the mocap footwork is a clean stance.
    /// </summary>
    /// <remarks>
    /// ReferencePoseMixer drives the legs from the pose at legWork = 0.8 while a punch is
    /// live, because "a punch thrown off the back foot should LOOK like it". That is right for
    /// hand-authored poses, which are built in a deliberate stance - but a lunge's strike frame
    /// catches the feet mid-stride, sometimes nearly square. Authoring THAT yanks the legs into
    /// a travelling pose every time the punch fires, which reads as the boxer flipping between
    /// orthodox and southpaw.
    ///
    /// So the stance is measured at the strike frame and the leg muscles are stripped from any
    /// pose whose footwork is not clean. ReferencePose leaves unauthored muscles to the
    /// animation, so stripping is exactly the right mechanism.
    /// </remarks>
    private const bool StripLegsWhenStanceIsDirty = true;

    /// <summary>Left foot must lead by at least this fraction of a leg length to count as orthodox.</summary>
    private const float MinLead = 0.06f;

    /// <summary>Feet must be at least this far apart laterally, or the stance has collapsed/crossed.</summary>
    private const float MinWidth = 0.05f;

    private sealed class Candidate
    {
        public AnimationClip Clip;
        public BoxerPunchController.Hand Hand;
        public ReferencePose.Category Category;
        public ReferencePose.TargetHeight Height;
        public float StrikeTime;
        public float Extension;
        public string SlotName;

        /// <summary>Lead-foot offset along facing at the strike, in leg-lengths. + = left foot leads.</summary>
        public float Lead;

        /// <summary>Lateral foot separation at the strike, in leg-lengths.</summary>
        public float Width;

        /// <summary>Is the footwork still a clean orthodox stance at the strike frame?</summary>
        public bool StanceClean;
    }

    /************************************************************************************/

    [MenuItem("Tools/Undisputed/Boxing/Bake Strike Poses (report only)")]
    public static void Report() => Run(dryRun: true);

    [MenuItem("Tools/Undisputed/Boxing/Bake Strike Poses")]
    public static void BakeAll() => Run(dryRun: false);

    private static void Run(bool dryRun)
    {
        Animator animator = FindBoxer();
        if (animator == null)
        {
            Debug.LogError("Strike pose bake: no Humanoid Animator found. Select the boxer in " +
                "the scene (or have one present) and try again.");
            return;
        }

        List<Candidate> candidates = Collect();
        if (candidates.Count == 0)
        {
            Debug.LogError("Strike pose bake: no punch clips found under " + PunchRoot);
            return;
        }

        MeasureStrikeTimes(animator, candidates);

        candidates.Sort((a, b) =>
        {
            int s = string.CompareOrdinal(a.SlotName, b.SlotName);
            return s != 0 ? s : b.Extension.CompareTo(a.Extension);
        });

        int drift = 0;
        Debug.Log($"Strike pose bake: {candidates.Count} punch clips on '{animator.name}'" +
                  (dryRun ? "  (REPORT ONLY - nothing written)" : ""));

        foreach (Candidate c in candidates)
        {
            float off = Mathf.Abs(c.StrikeTime - 0.4f);
            if (off > 0.1f)
                drift++;

            Debug.Log($"   {c.SlotName,-22} strike {c.StrikeTime:0.000} " +
                      $"({c.StrikeTime * c.Clip.length * 1000f:0}ms)  extension {c.Extension:0.00}" +
                      $"  lead {c.Lead:+0.00;-0.00}  width {c.Width:0.00}  " +
                      $"{(c.StanceClean ? "stance ok" : "LEGS STRIPPED")}" +
                      $"{(off > 0.1f ? "   <- global 0.40 would miss by " + (off * c.Clip.length * 1000f).ToString("0") + "ms" : "")}" +
                      $"   {c.Clip.name}", c.Clip);
        }

        Debug.Log($"Strike pose bake: {drift}/{candidates.Count} clips strike more than 0.10 away " +
                  "from the controller's global clipStrikeTime of 0.40 - which is why these are " +
                  "baked as poses at their own measured frame rather than scrubbed to a shared one.");

        if (dryRun)
            return;

        int baked = 0, stripped = 0;
        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                EditorUtility.DisplayProgressBar("Baking strike poses",
                    $"{c.SlotName}  ({i + 1}/{candidates.Count})", (i + 1f) / candidates.Count);

                ReferencePose pose = ReferencePoseBaker.Bake(
                    animator, c.Clip, c.StrikeTime * c.Clip.length,
                    c.SlotName, c.Category, c.Hand, c.Height);

                if (pose == null)
                    continue;

                baked++;

                if (StripLegsWhenStanceIsDirty && !c.StanceClean)
                {
                    int removed = StripLegs(pose);
                    if (removed > 0)
                        stripped++;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Strike pose bake: wrote {baked} pose(s) to Assets/Resources/ReferencePoses. " +
            "Repeats of a slot became variants (…2, …3); ReferencePoseMixer rolls one per throw.");
        Debug.Log($"Strike pose bake: legs stripped from {stripped}/{baked} pose(s) whose footwork " +
            "was mid-stride at the strike. The mixer drives legs from poses at legWork = 0.8, so " +
            "authoring a lunging stride there is what made the boxer flip orthodox/southpaw.");
        Debug.LogWarning("Strike pose bake: Tools > Boxer > Poses > Reference Pose Baker > " +
            "\"Bake Folders\" WIPES Resources/ReferencePoses before it runs. Running it will " +
            "delete these; re-run this bake afterwards.");
    }

    /************************************************************************************/

    private const string PoseFolder = "Assets/Resources/ReferencePoses";
    private const string UndisputedRoot = "Assets/Animations/Undisputed";

    [MenuItem("Tools/Undisputed/Boxing/Purge Old Punch Poses (report only)")]
    public static void PurgeReport() => Purge(dryRun: true);

    [MenuItem("Tools/Undisputed/Boxing/Purge Old Punch Poses")]
    public static void PurgeOld() => Purge(dryRun: false);

    /// <summary>
    /// Deletes strike poses that did not come from the Undisputed library.
    /// </summary>
    /// <remarks>
    /// The old hand-posed shapes stay in play after a bake because they are variant #1 of
    /// every slot and ReferencePoseMixer's variant pool rolls all of them - so the boxer keeps
    /// throwing the old anatomy at random. Removing the assets is the only way to retire them.
    ///
    /// Guards and Loaded poses are never touched: Undisputed supplies no guard, and the wind-up
    /// ladder is a separate job. A slot is only purged when an Undisputed pose survives to fill
    /// it, so this can never leave a punch shapeless.
    /// </remarks>
    private static void Purge(bool dryRun)
    {
        if (!AssetDatabase.IsValidFolder(PoseFolder))
        {
            Debug.LogError("Purge: " + PoseFolder + " does not exist.");
            return;
        }

        var old = new List<ReferencePose>();
        var fresh = new Dictionary<int, int>();

        foreach (string guid in AssetDatabase.FindAssets("t:ReferencePose", new[] { PoseFolder }))
        {
            var pose = AssetDatabase.LoadAssetAtPath<ReferencePose>(
                AssetDatabase.GUIDToAssetPath(guid));
            if (pose == null)
                continue;

            bool isStrike = pose.category == ReferencePose.Category.Straight
                         || pose.category == ReferencePose.Category.Hook
                         || pose.category == ReferencePose.Category.Uppercut;
            if (!isStrike)
                continue;

            string src = pose.sourceClip != null
                ? AssetDatabase.GetAssetPath(pose.sourceClip)
                : string.Empty;

            int key = SlotKey(pose);

            if (!string.IsNullOrEmpty(src) && src.StartsWith(UndisputedRoot))
                fresh[key] = fresh.TryGetValue(key, out int n) ? n + 1 : 1;
            else
                old.Add(pose);
        }

        if (old.Count == 0)
        {
            Debug.Log("Purge: no non-Undisputed strike poses left - the punch library is clean.");
            return;
        }

        int deleted = 0, kept = 0;
        foreach (ReferencePose pose in old)
        {
            int key = SlotKey(pose);
            bool covered = fresh.TryGetValue(key, out int n) && n > 0;
            string src = pose.sourceClip != null
                ? AssetDatabase.GetAssetPath(pose.sourceClip) : "<none>";

            if (!covered)
            {
                kept++;
                Debug.LogWarning($"Purge: KEEPING '{pose.name}' ({pose.hand} {pose.category} " +
                    $"{pose.height}) - no Undisputed pose covers that slot yet, so deleting it " +
                    $"would leave the punch shapeless. Run Bake Strike Poses first.  src {src}", pose);
                continue;
            }

            Debug.Log($"Purge: {(dryRun ? "would delete" : "deleting")} '{pose.name}' " +
                $"({pose.hand} {pose.category} {pose.height}) - {n} Undisputed pose(s) cover it.  " +
                $"src {src}", dryRun ? pose : null);

            if (!dryRun)
            {
                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(pose));
                deleted++;
            }
        }

        if (!dryRun)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        Debug.Log($"Purge: {(dryRun ? "would delete" : "deleted")} {(dryRun ? old.Count - kept : deleted)} " +
            $"old strike pose(s); kept {kept} for uncovered slots. Guards and Loaded poses are " +
            "left alone - Undisputed has no guard, and the wind-up ladder is a separate pass.");
    }

    private static int SlotKey(ReferencePose p)
        => ((int)p.hand * 16 + (int)p.category) * 4 + (int)p.height;

    /************************************************************************************/

    private static Animator FindBoxer()
    {
        if (Selection.activeGameObject != null)
        {
            var selected = Selection.activeGameObject.GetComponentInChildren<Animator>();
            if (IsHumanoid(selected))
                return selected;
        }

        foreach (Animator a in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
        {
            if (IsHumanoid(a))
                return a;
        }

        return null;
    }

    private static bool IsHumanoid(Animator a)
        => a != null && a.avatar != null && a.avatar.isHuman;

    private static List<Candidate> Collect()
    {
        var list = new List<Candidate>();

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { PunchRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                continue;

            string lower = clip.name.ToLowerInvariant();
            bool skip = false;
            foreach (string bad in Exclude)
            {
                if (lower.Contains(bad))
                {
                    skip = true;
                    break;
                }
            }
            if (skip)
                continue;

            string hand, type, target;
            Match m = Tagged.Match(clip.name);

            if (m.Success)
            {
                hand = m.Groups["hand"].Value;
                type = m.Groups["type"].Value;
                target = m.Groups["target"].Value;
            }
            else
            {
                Match l = Loose.Match(clip.name);
                if (!l.Success)
                    continue;

                hand = l.Groups["hand"].Value;
                type = l.Groups["type"].Value;
                target = lower.Contains("body") ? "Body" : "Head";
            }

            // The mixer's straight slot covers jabs and crosses alike.
            ReferencePose.Category category =
                type.Equals("Hook", System.StringComparison.OrdinalIgnoreCase) ? ReferencePose.Category.Hook :
                type.Equals("Uppercut", System.StringComparison.OrdinalIgnoreCase) ? ReferencePose.Category.Uppercut :
                ReferencePose.Category.Straight;

            ReferencePose.TargetHeight height =
                target.Equals("Body", System.StringComparison.OrdinalIgnoreCase)
                    ? ReferencePose.TargetHeight.Body
                    : ReferencePose.TargetHeight.Head;

            BoxerPunchController.Hand h = hand.ToUpperInvariant() == "L"
                ? BoxerPunchController.Hand.Left
                : BoxerPunchController.Hand.Right;

            // Matches the existing naming (RightHookHead, LeftStraightBody) so repeats land in
            // the same slot and the baker turns them into numbered variants.
            string slot = (h == BoxerPunchController.Hand.Left ? "Left" : "Right")
                        + category
                        + (height == ReferencePose.TargetHeight.Head ? "Head" : "Body");

            list.Add(new Candidate
            {
                Clip = clip,
                Hand = h,
                Category = category,
                Height = height,
                SlotName = slot,
            });
        }

        return list;
    }

    /// <summary>
    /// Finds each clip's strike frame as the peak of arm extension, on the real rig.
    /// </summary>
    /// <remarks>
    /// One AnimationMode session for the whole sweep. ReferencePoseBaker.Bake opens its own,
    /// and nesting them leaves the editor stuck in animation mode, so measuring and baking
    /// stay separate passes.
    /// </remarks>
    private static void MeasureStrikeTimes(Animator animator, List<Candidate> candidates)
    {
        GameObject go = animator.gameObject;

        AnimationMode.StartAnimationMode();
        try
        {
            foreach (Candidate c in candidates)
            {
                float length = Mathf.Max(0.01f, c.Clip.length);
                int steps = Mathf.Clamp(Mathf.RoundToInt(length * 60f), 12, 240);

                float bestT = 0.4f, bestExt = -1f;

                for (int i = 0; i <= steps; i++)
                {
                    float t01 = i / (float)steps;

                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(go, c.Clip, t01 * length);
                    AnimationMode.EndSampling();

                    float ext = Extension(animator, c.Hand);
                    if (ext > bestExt)
                    {
                        bestExt = ext;
                        bestT = t01;
                    }
                }

                c.StrikeTime = bestT;
                c.Extension = Mathf.Max(0f, bestExt);

                // Re-sample the winning frame and read the footwork there.
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(go, c.Clip, bestT * length);
                AnimationMode.EndSampling();

                MeasureStance(animator, out c.Lead, out c.Width);
                c.StanceClean = c.Lead >= MinLead && c.Width >= MinWidth;
            }
        }
        finally
        {
            AnimationMode.StopAnimationMode();
        }
    }

    /// <summary>
    /// Reads the footwork: how far the left foot leads, and how far apart the feet are, both
    /// in leg-lengths so it is scale independent.
    /// </summary>
    /// <remarks>
    /// Facing is taken from the hip line rather than the transform, because sampling a clip
    /// with root motion moves and turns the transform - the hips are the only thing that
    /// reliably says which way the body points.
    /// </remarks>
    private static void MeasureStance(Animator animator, out float lead, out float width)
    {
        lead = width = 0f;

        Transform lUp = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Transform rUp = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        Transform lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);

        if (lUp == null || rUp == null || lFoot == null || rFoot == null)
            return;

        float legLength = Vector3.Distance(lUp.position, lFoot.position);
        if (legLength < 0.01f)
            return;

        Vector3 hipRight = rUp.position - lUp.position;
        hipRight.y = 0f;
        if (hipRight.sqrMagnitude < 0.000001f)
            return;
        hipRight.Normalize();

        Vector3 forward = Vector3.Cross(hipRight, Vector3.up).normalized;

        Vector3 leftToRight = lFoot.position - rFoot.position;
        lead = Vector3.Dot(leftToRight, forward) / legLength;
        width = Mathf.Abs(Vector3.Dot(leftToRight, hipRight)) / legLength;
    }

    /// <summary>
    /// Removes the leg muscles from a baked pose so the Legs Animator keeps the stance.
    /// </summary>
    /// <remarks>
    /// ReferencePose treats a muscle it never named as unauthored and leaves it to the
    /// animation, so dropping the entries is the supported way to say "this pose has no
    /// opinion about the legs".
    /// </remarks>
    private static int StripLegs(ReferencePose pose)
    {
        if (pose == null || pose.muscleNames == null || pose.muscleValues == null)
            return 0;

        var names = new List<string>(pose.muscleNames.Length);
        var values = new List<float>(pose.muscleValues.Length);
        int removed = 0;

        for (int i = 0; i < pose.muscleNames.Length && i < pose.muscleValues.Length; i++)
        {
            string n = pose.muscleNames[i];
            bool isLeg = n.Contains("Upper Leg") || n.Contains("Lower Leg")
                      || n.Contains("Foot") || n.Contains("Toes");

            if (isLeg)
            {
                removed++;
                continue;
            }

            names.Add(n);
            values.Add(pose.muscleValues[i]);
        }

        if (removed == 0)
            return 0;

        pose.muscleNames = names.ToArray();
        pose.muscleValues = values.ToArray();
        pose.muscles = new float[0];
        pose.Invalidate();
        EditorUtility.SetDirty(pose);
        return removed;
    }

    /// <summary>Shoulder-to-hand distance over total arm length: 0 at guard, 1 fully out.</summary>
    private static float Extension(Animator animator, BoxerPunchController.Hand hand)
    {
        bool right = hand == BoxerPunchController.Hand.Right;

        Transform upper = animator.GetBoneTransform(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
        Transform lower = animator.GetBoneTransform(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
        Transform wrist = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);

        if (upper == null || lower == null || wrist == null)
            return 0f;

        float armLength = Vector3.Distance(upper.position, lower.position)
                        + Vector3.Distance(lower.position, wrist.position);

        return armLength > 0.01f
            ? Mathf.Clamp01(Vector3.Distance(upper.position, wrist.position) / armLength)
            : 0f;
    }
}
