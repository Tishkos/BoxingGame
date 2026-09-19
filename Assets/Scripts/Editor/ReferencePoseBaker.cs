using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools ▸ Boxer ▸ Poses ▸ Reference Pose Baker — samples a one-frame clip (a UMotion pose export, .anim or FBX)
/// onto the boxer and stores the humanoid muscle values as a <see cref="ReferencePose"/> asset.
///
/// "Bake Folders" scans Assets/Refrences (and variants), WIPES Assets/Resources/ReferencePoses first (old bakes are
/// never reused), bakes every clip whose file name names a pose, and wires everything into the character's
/// <see cref="ReferencePoseMixer"/>. Naming it understands (case/space insensitive):
///   guards:  HeadGuard → Guard slot · BodyGuard / LowGuard / Crouch → GuardBody (R1)
///   hand:    Left / Right (or Lead / Rear); a lone "jab" defaults to the lead (left) hand
///   type:    Jab / Straight / Cross · Hook · Uppercut · Loaded / Coil / Windup
///   height:  Face / Head · Body · LowerBody / Low  (default Body) — e.g. JabFaceRightHand, LeftHookLowerBody
/// Folders named "Idles" and files containing "energy" are skipped — those are stance ANIMATIONS, not poses.
/// </summary>
public class ReferencePoseBaker : EditorWindow
{
    private static readonly string[] PoseFolders = { "Assets/Refrences", "Assets/References", "Assets/Reference", "Assets/Animations/Reference", "Assets/Animations/Poses" };
    private const string OutputFolder = "Assets/Resources/ReferencePoses";

    private Animator character;
    private AnimationClip clip;
    private float time;
    private string poseName = "";
    private ReferencePose.Category category = ReferencePose.Category.Other;
    private BoxerPunchController.Hand hand = BoxerPunchController.Hand.Right;
    private ReferencePose.TargetHeight height = ReferencePose.TargetHeight.Body;

    [MenuItem("Tools/Boxer/Poses/Reference Pose Baker")]
    private static void Open() => GetWindow<ReferencePoseBaker>("Reference Pose Baker");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Author single-frame poses in UMotion, export them into Assets/Refrences, then press 'Bake Folders'. " +
            "The output folder is WIPED first so old bakes are never reused. Poses are stored in humanoid muscle " +
            "space and used only as anatomical guidance by ReferencePoseMixer — never played as attacks.", MessageType.Info);

        character = (Animator)EditorGUILayout.ObjectField("Character (scene)", character, typeof(Animator), true);
        if (character == null && Selection.activeGameObject != null) character = Selection.activeGameObject.GetComponent<Animator>();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Bake one clip", EditorStyles.boldLabel);
        clip = (AnimationClip)EditorGUILayout.ObjectField("Pose clip", clip, typeof(AnimationClip), false);
        time = EditorGUILayout.FloatField("Time (s)", time);
        poseName = EditorGUILayout.TextField("Pose name", string.IsNullOrEmpty(poseName) && clip != null ? clip.name : poseName);
        category = (ReferencePose.Category)EditorGUILayout.EnumPopup("Category", category);
        hand = (BoxerPunchController.Hand)EditorGUILayout.EnumPopup("Hand", hand);
        height = (ReferencePose.TargetHeight)EditorGUILayout.EnumPopup("Aim height", height);

        using (new EditorGUI.DisabledScope(character == null || clip == null))
        {
            if (GUILayout.Button("Bake Pose"))
            {
                ReferencePose asset = Bake(character, clip, time, string.IsNullOrEmpty(poseName) ? clip.name : poseName, category, hand, height);
                if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Bake the reference folders (wipes old bakes first)", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(string.Join(", ", PoseFolders), EditorStyles.miniLabel);
        EditorGUILayout.LabelField("Names: HeadGuard/BodyGuard · Left|Right + Jab/Straight/Hook/Uppercut/Loaded + Face/Body/LowerBody", EditorStyles.miniLabel);
        using (new EditorGUI.DisabledScope(character == null))
        {
            if (GUILayout.Button("Bake Folders & Assign To Mixer", GUILayout.Height(28))) BakeFolders(character);
        }
    }

    // ---------------------------------------------------------------- Baking

    public static ReferencePose Bake(Animator animator, AnimationClip clip, float time, string name,
        ReferencePose.Category category, BoxerPunchController.Hand hand, ReferencePose.TargetHeight height,
        int loadLevel = -1)
    {
        if (animator == null || clip == null) return null;
        if (animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogError("Reference Pose Baker: the character needs a Humanoid avatar.", animator);
            return null;
        }

        GameObject go = animator.gameObject;
        HumanPose humanPose = new HumanPose();
        float extension = 0f;
        float chestTwist = 0f;

        // Sample the clip onto the character inside AnimationMode so the scene pose is restored afterwards.
        AnimationMode.StartAnimationMode();
        try
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(go, clip, Mathf.Clamp(time, 0f, Mathf.Max(0f, clip.length)));
            AnimationMode.EndSampling();

            using (HumanPoseHandler handler = new HumanPoseHandler(animator.avatar, animator.transform))
            {
                handler.GetHumanPose(ref humanPose);
            }

            extension = MeasureExtension(animator, hand);
            chestTwist = MeasureChestTwist(animator, hand);
        }
        finally
        {
            AnimationMode.StopAnimationMode();
        }

        if (humanPose.muscles == null || humanPose.muscles.Length != HumanTrait.MuscleCount)
        {
            Debug.LogError("Reference Pose Baker: could not read the humanoid pose.", animator);
            return null;
        }

        Directory.CreateDirectory(OutputFolder);
        string path = $"{OutputFolder}/{Sanitize(name)}.asset";
        ReferencePose asset = AssetDatabase.LoadAssetAtPath<ReferencePose>(path);
        // A different source clip mapping to the same slot becomes a VARIANT (RightHookBody2, 3…) — the mixer
        // rolls a random one per throw so punches never look identical.
        int variant = 2;
        while (asset != null && asset.sourceClip != null && asset.sourceClip != clip)
        {
            path = $"{OutputFolder}/{Sanitize(name)}{variant}.asset";
            asset = AssetDatabase.LoadAssetAtPath<ReferencePose>(path);
            variant++;
        }
        bool isNew = asset == null;
        if (isNew) asset = ScriptableObject.CreateInstance<ReferencePose>();

        asset.poseName = name;
        asset.category = category;
        asset.hand = hand;
        asset.height = height;
        asset.loadLevel = loadLevel;
        asset.sourceClip = clip;
        asset.sourceTime = time;
        asset.muscles = new float[0];
        asset.muscleNames = (string[])HumanTrait.MuscleName.Clone();
        asset.muscleValues = (float[])humanPose.muscles.Clone();
        asset.Invalidate();
        asset.bodyPosition = humanPose.bodyPosition;
        asset.bodyRotation = humanPose.bodyRotation;
        asset.extension = extension;
        asset.chestTwist = chestTwist;

        if (isNew) AssetDatabase.CreateAsset(asset, path);
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        Debug.Log($"Reference pose baked: {name} ({hand} {category} {height}, extension {extension:0.00}, chest twist {chestTwist:0}°) → {path}", asset);
        return asset;
    }

    private static void BakeFolders(Animator animator)
    {
        ReferencePoseMixer mixer = animator.GetComponent<ReferencePoseMixer>();
        if (mixer == null) mixer = Undo.AddComponent<ReferencePoseMixer>(animator.gameObject);

        List<string> folders = new List<string>();
        foreach (string f in PoseFolders) if (AssetDatabase.IsValidFolder(f)) folders.Add(f);
        if (folders.Count == 0)
        {
            Debug.LogWarning("Reference poses: none of these folders exist yet: " + string.Join(", ", PoseFolders), animator);
            return;
        }

        // Old bakes are never reused: wipe the output folder and every mixer slot before baking.
        int wiped = 0;
        if (AssetDatabase.IsValidFolder(OutputFolder))
            foreach (string guid in AssetDatabase.FindAssets("t:ReferencePose", new[] { OutputFolder }))
            {
                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));
                wiped++;
            }
        Undo.RecordObject(mixer, "Rebake Reference Poses");
        mixer.guard = null;
        mixer.guardBody = null;
        mixer.left = new ReferencePoseMixer.HandPoses();
        mixer.right = new ReferencePoseMixer.HandPoses();

        int baked = 0;
        List<string> report = new List<string>();
        // Wind-up poses are collected, then sorted low → high into each hand's ladder once everything is baked.
        List<ReferencePose> leftLoads = new List<ReferencePose>();
        List<ReferencePose> rightLoads = new List<ReferencePose>();
        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", folders.ToArray()))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.StartsWith(OutputFolder)) continue;

            string label = Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
            if (path.Replace('\\', '/').Contains("/Idles/") || label.Contains("energy") || label.Contains("idle"))
            {
                report.Add($"skipped '{Path.GetFileName(path)}' (idle/energy stance animation, not a pose)");
                continue;
            }

            if (!Classify(label, out ReferencePose.Category cat, out BoxerPunchController.Hand hand, out ReferencePose.TargetHeight h, out string name, out int loadLevel))
            {
                report.Add($"skipped '{Path.GetFileName(path)}' (name doesn't say guard / left|right + jab|straight|hook|uppercut|loaded)");
                continue;
            }

            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(o is AnimationClip c) || c.name.StartsWith("__preview__")) continue;

                // A UMotion pose export is nominally one frame, but several of these came out with TWO — and if
                // the pose sits on the second one, sampling at t = 0 bakes the rest frame instead of the pose.
                // The LAST frame is the authored one in both cases, so that is what gets sampled.
                float sampleAt = c.length;
                if (c.length > 0.0001f)
                    report.Add($"'{Path.GetFileName(path)}' is {Mathf.RoundToInt(c.length * c.frameRate) + 1} frame(s) — baked the last one");

                ReferencePose asset = Bake(animator, c, sampleAt, name, cat, hand, h, loadLevel);
                if (asset == null) continue;
                baked++;

                if (cat == ReferencePose.Category.Guard) mixer.guard = asset;
                else if (cat == ReferencePose.Category.GuardBody) mixer.guardBody = asset;
                else
                {
                    ReferencePoseMixer.HandPoses set = hand == BoxerPunchController.Hand.Left ? mixer.left : mixer.right;
                    if (cat == ReferencePose.Category.Loaded)
                        (hand == BoxerPunchController.Hand.Left ? leftLoads : rightLoads).Add(asset);
                    else set.For(cat)?.Set(h, asset);
                }
                report.Add($"{name} ← {Path.GetFileName(path)}");
                break; // one clip per file
            }
        }
        mixer.left.loadedLadder = SortLadder(leftLoads);
        mixer.right.loadedLadder = SortLadder(rightLoads);
        if (leftLoads.Count + rightLoads.Count > 0)
            report.Add($"wind-up ladder: left [{string.Join(", ", leftLoads.ConvertAll(a => a.name))}] " +
                       $"right [{string.Join(", ", rightLoads.ConvertAll(a => a.name))}]  (low → high)");
        else
            report.Add("no load poses found — chambers will be synthesised from guard + strike");
        EditorUtility.SetDirty(mixer);

        Debug.Log(baked > 0
            ? $"Reference poses: wiped {wiped} old bake(s), baked {baked} pose(s) and assigned them to {animator.name}'s ReferencePoseMixer.\n  " + string.Join("\n  ", report)
            : $"Reference poses: wiped {wiped} old bake(s) but found no usable clips.\n  " + string.Join("\n  ", report), animator);
    }

    /// <summary>File name → pose slot: category + hand + aim height.</summary>
    private static bool Classify(string label, out ReferencePose.Category category, out BoxerPunchController.Hand hand,
        out ReferencePose.TargetHeight height, out string name, out int loadLevel)
    {
        category = ReferencePose.Category.Other;
        hand = BoxerPunchController.Hand.Right;
        height = ReferencePose.TargetHeight.Body;
        name = "";
        loadLevel = -1;

        if ((label.Contains("guard") && (label.Contains("body") || label.Contains("low") || label.Contains("crouch"))) || label.Contains("bodyguard"))
        {
            category = ReferencePose.Category.GuardBody;
            name = "GuardBody";
            return true;
        }
        if (label.Contains("guard") || label.Contains("stance"))
        {
            category = ReferencePose.Category.Guard;
            name = "Guard";
            return true;
        }

        bool isLeft = label.Contains("left") || label.Contains("lead");
        bool isRight = label.Contains("right") || label.Contains("rear");
        if (!isLeft && !isRight)
        {
            if (label.Contains("jab")) isLeft = true;            // a lone "jab" is the lead (left) hand by convention
            else if (label.Contains("cross")) isRight = true;    // a lone "cross" is the rear (right) hand
            else return false;
        }
        hand = isLeft ? BoxerPunchController.Hand.Left : BoxerPunchController.Hand.Right;
        string side = isLeft ? "Left" : "Right";

        // ORDER MATTERS: "lowerbody" contains "body", so check it first.
        string heightName;
        if (label.Contains("lowerbody") || (label.Contains("low") && !label.Contains("lowerarm"))) { height = ReferencePose.TargetHeight.Low; heightName = "Low"; }
        else if (label.Contains("face") || label.Contains("head")) { height = ReferencePose.TargetHeight.Head; heightName = "Head"; }
        else if (label.Contains("body")) { height = ReferencePose.TargetHeight.Body; heightName = "Body"; }
        else { height = ReferencePose.TargetHeight.Body; heightName = "Body"; }

        if (label.Contains("uppercut") || label.Contains("upper")) { category = ReferencePose.Category.Uppercut; name = side + "Uppercut" + heightName; }
        else if (label.Contains("hook"))                           { category = ReferencePose.Category.Hook;     name = side + "Hook" + heightName; }
        else if ((label.Contains("load") && !label.Contains("lowerbody")) || label.Contains("coil") || label.Contains("windup"))
        {
            category = ReferencePose.Category.Loaded;
            // The wind-up LADDER: a trailing digit is the rung, 1 = lowest. "Load1RightHand" … "Load4RightHand".
            // Without a digit fall back to the height word, so LoadLow / LoadBody / LoadHead also work.
            loadLevel = TrailingDigit(label);
            if (loadLevel < 0)
                loadLevel = height == ReferencePose.TargetHeight.Low ? 0
                          : height == ReferencePose.TargetHeight.Head ? 3 : 1;
            name = side + "Load" + (loadLevel + 1);
        }
        else if (label.Contains("straight") || label.Contains("jab") || label.Contains("cross")) { category = ReferencePose.Category.Straight; name = side + "Straight" + heightName; }
        else return false;
        return true;
    }

    /// <summary>Order a hand's load poses low → high: by loadLevel when it was named, else alphabetically.</summary>
    private static ReferencePose[] SortLadder(List<ReferencePose> loads)
    {
        loads.Sort((a, b) =>
        {
            int la = a.loadLevel >= 0 ? a.loadLevel : int.MaxValue;
            int lb = b.loadLevel >= 0 ? b.loadLevel : int.MaxValue;
            return la != lb ? la.CompareTo(lb) : string.CompareOrdinal(a.name, b.name);
        });
        return loads.ToArray();
    }

    /// <summary>Last digit run in the name, as a 0-based rung (Load1 → 0). -1 when there is no digit.</summary>
    private static int TrailingDigit(string label)
    {
        for (int i = label.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(label[i])) continue;
            int end = i;
            while (i > 0 && char.IsDigit(label[i - 1])) i--;
            if (int.TryParse(label.Substring(i, end - i + 1), out int n)) return Mathf.Max(0, n - 1);
            return -1;
        }
        return -1;
    }

    // ---------------------------------------------------------------- Measurements

    private static float MeasureExtension(Animator animator, BoxerPunchController.Hand hand)
    {
        bool right = hand == BoxerPunchController.Hand.Right;
        Transform upper = animator.GetBoneTransform(right ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
        Transform lower = animator.GetBoneTransform(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
        Transform handBone = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
        if (upper == null || lower == null || handBone == null) return 0f;
        float armLength = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, handBone.position);
        return armLength > 0.01f ? Mathf.Clamp01(Vector3.Distance(upper.position, handBone.position) / armLength) : 0f;
    }

    private static float MeasureChestTwist(Animator animator, BoxerPunchController.Hand hand)
    {
        Transform lUp = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm), rUp = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform lLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg), rLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        if (lUp == null || rUp == null || lLeg == null || rLeg == null) return 0f;

        Vector3 shoulders = Vector3.ProjectOnPlane(rUp.position - lUp.position, Vector3.up);
        Vector3 hipLine = Vector3.ProjectOnPlane(rLeg.position - lLeg.position, Vector3.up);
        float twist = Vector3.SignedAngle(hipLine, shoulders, Vector3.up);
        return hand == BoxerPunchController.Hand.Right ? -twist : twist;
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
