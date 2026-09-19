using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Animancer;
using Boxing;
using RootMotion;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the punch library, the upper-body mask and the playable rig.
/// </summary>
public static class BoxingRigSetup
{
    private const string Root = "Assets/Animations/Undisputed";
    private const string OutFolder = "Assets/UndisputedTest/Boxing";
    private const string LibraryPath = OutFolder + "/PunchLibrary.asset";
    private const string MaskPath = OutFolder + "/UpperBody.mask";
    private const string BennettPath = "Assets/Character/Bennett.fbx";
    private const string RigName = "Bennett_Boxing";

    // ..._OptimizedForHead_LHook_Power_Orthodox_MxM_MOD
    private static readonly Regex Tagged = new Regex(
        @"OptimizedFor(?<target>Head|Body)_(?<hand>[LR])(?<type>Jab|Hook|Uppercut)_?(?<variant>.*?)_(?<stance>Orthodox|Southpaw)",
        RegexOptions.IgnoreCase);

    private static readonly Regex Loose = new Regex(
        @"(?<hand>[LR])[_ ]?(?<type>Jab|Hook|Uppercut|Straight)", RegexOptions.IgnoreCase);

    /************************************************************************************/

    [MenuItem("Tools/Undisputed/Boxing/Build Punch Library")]
    public static PunchLibrary BuildLibrary()
    {
        var library = AssetDatabase.LoadAssetAtPath<PunchLibrary>(LibraryPath);
        if (library == null)
        {
            library = ScriptableObject.CreateInstance<PunchLibrary>();
            AssetDatabase.CreateAsset(library, LibraryPath);
        }

        library.Entries.Clear();

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { Root }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);

            // Reactions mention punches in their names but are not thrown punches.
            if (path.Contains("/HitReaction/") || path.Contains("/Knockdown/") ||
                path.Contains("/Knockout/") || path.Contains("/Stunned/") ||
                path.Contains("/Block/") || path.Contains("/Locomotion/"))
                continue;

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                continue;

            PunchLibrary.Entry entry = Parse(clip);
            if (entry != null)
                library.Entries.Add(entry);
        }

        library.AdditiveVariations = PickAdditives();
        library.Idle = PickIdle();
        library.ClearCache();

        EditorUtility.SetDirty(library);
        AssetDatabase.SaveAssets();

        var byCell = library.Entries
            .GroupBy(e => $"{e.Hand} {e.Type} {e.Target}")
            .OrderBy(g => g.Key);

        Debug.Log($"Boxing: library built - {library.Entries.Count} punches, " +
            $"{library.AdditiveVariations.Count} additive variations, " +
            $"idle = {(library.Idle != null ? library.Idle.name : "none")}", library);

        foreach (var g in byCell)
            Debug.Log($"   {g.Key,-22} x{g.Count()}   tiers " +
                string.Join(",", g.Select(e => e.Tier).Distinct().OrderBy(t => t)));

        foreach (var missing in AllCells().Where(c => !byCell.Any(g => g.Key == c)))
            Debug.LogWarning($"Boxing: no clips for {missing} - that gesture will fall back.");

        return library;
    }

    private static IEnumerable<string> AllCells()
    {
        foreach (Hand h in new[] { Hand.Left, Hand.Right })
            foreach (PunchType t in new[] { PunchType.Jab, PunchType.Hook, PunchType.Uppercut })
                foreach (PunchTarget g in new[] { PunchTarget.Head, PunchTarget.Body })
                    yield return $"{h} {t} {g}";
    }

    private static PunchLibrary.Entry Parse(AnimationClip clip)
    {
        string name = clip.name;
        Match m = Tagged.Match(name);

        string hand, type, target, variant;

        if (m.Success)
        {
            hand = m.Groups["hand"].Value.ToUpperInvariant();
            type = m.Groups["type"].Value;
            target = m.Groups["target"].Value;
            variant = m.Groups["variant"].Value.ToLowerInvariant();
        }
        else
        {
            Match l = Loose.Match(name);
            if (!l.Success)
                return null;

            hand = l.Groups["hand"].Value.ToUpperInvariant();
            type = l.Groups["type"].Value;
            target = name.IndexOf("body", System.StringComparison.OrdinalIgnoreCase) >= 0
                ? "Body" : "Head";
            variant = "";
        }

        if (type.Equals("Straight", System.StringComparison.OrdinalIgnoreCase))
            type = "Jab";

        string lower = name.ToLowerInvariant();

        var entry = new PunchLibrary.Entry
        {
            Clip = clip,
            Hand = hand == "L" ? Hand.Left : Hand.Right,
            Type = type.Equals("Hook", System.StringComparison.OrdinalIgnoreCase) ? PunchType.Hook
                 : type.Equals("Uppercut", System.StringComparison.OrdinalIgnoreCase) ? PunchType.Uppercut
                 : PunchType.Jab,
            Target = target.Equals("Body", System.StringComparison.OrdinalIgnoreCase)
                 ? PunchTarget.Body : PunchTarget.Head,
            Length = clip.length,
            Tier = Tier(lower, variant),
            Lunge = LungeOf(lower),
            // Impact lands slightly later on committed shots than on fast inside ones.
            StrikeTime = 0.42f,
        };

        entry.StrikeTime = Mathf.Lerp(0.38f, 0.52f, entry.Tier / 3f);
        return entry;
    }

    private static int Tier(string lower, string variant)
    {
        if (lower.Contains("power")) return 3;
        if (variant.Contains("r0") || lower.Contains("_inside_")) return 0;
        if (lower.Contains("int5") || lower.Contains("int4")) return 2;
        return 1;
    }

    private static LungeType LungeOf(string lower)
    {
        if (lower.Contains("lunge_in")) return LungeType.In;
        if (lower.Contains("lunge_out")) return LungeType.Out;
        if (lower.Contains("lunge_left")) return LungeType.Left;
        if (lower.Contains("lunge_right")) return LungeType.Right;
        return LungeType.None;
    }

    /// <summary>
    /// Short slips and weaves make the best additive variation - they are upper-body lean
    /// with little root travel, which is exactly what should stack on top of a punch.
    /// </summary>
    private static List<AnimationClip> PickAdditives()
    {
        var picked = new List<AnimationClip>();

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip",
                     new[] { Root + "/Orthodox/Dodge" }))
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                AssetDatabase.GUIDToAssetPath(guid));

            if (clip == null || clip.length > 2.5f)
                continue;

            string n = clip.name.ToLowerInvariant();
            if (n.Contains("weave") || n.Contains("slip") || n.Contains("lean"))
                picked.Add(clip);
        }

        return picked.OrderBy(c => c.name).Take(12).ToList();
    }

    /// <summary>
    /// Fills LookAtIK's bone chain from the humanoid rig rather than by hand.
    /// </summary>
    /// <remarks>
    /// UpperChest is optional on a humanoid avatar, so it is only added when the rig has it -
    /// passing a null bone makes the solver throw on the first frame.
    /// </remarks>
    private static void WireLookAt(LookAtIK lookAt, Animator animator)
    {
        var spine = new List<IKSolverLookAt.LookAtBone>();

        foreach (HumanBodyBones bone in new[]
                 { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
        {
            Transform t = animator.GetBoneTransform(bone);
            if (t != null)
                spine.Add(new IKSolverLookAt.LookAtBone(t));
        }

        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);

        lookAt.solver.spine = spine.ToArray();
        lookAt.solver.head = head != null
            ? new IKSolverLookAt.LookAtBone(head)
            : new IKSolverLookAt.LookAtBone();

        var eyes = new List<IKSolverLookAt.LookAtBone>();
        foreach (HumanBodyBones bone in new[] { HumanBodyBones.LeftEye, HumanBodyBones.RightEye })
        {
            Transform t = animator.GetBoneTransform(bone);
            if (t != null)
                eyes.Add(new IKSolverLookAt.LookAtBone(t));
        }
        lookAt.solver.eyes = eyes.ToArray();

        Debug.Log($"Boxing: LookAtIK wired - {spine.Count} spine bone(s), " +
            $"head {(head != null ? head.name : "MISSING")}, {eyes.Count} eye bone(s).");
    }

    /// <summary>
    /// The thing to square up on. Prefers what is already in the scene over inventing one.
    /// </summary>
    /// <remarks>
    /// Order matters: an explicit BoxingTarget marker beats the bag itself, and the bag beats
    /// a stand-in. Creating a dummy when a real PunchingBag exists is what left the boxer
    /// facing an empty capsule while the bag sat behind him.
    /// </remarks>
    private static Transform FindOrCreateTarget(Transform boxer)
    {
        var marker = Object.FindFirstObjectByType<BoxingTarget>();
        if (marker != null)
        {
            Debug.Log("Boxing: squaring up on BoxingTarget '" + marker.name + "'.", marker);
            return marker.transform;
        }

        var bag = Object.FindFirstObjectByType<PunchingBag>();
        if (bag != null)
        {
            Debug.Log("Boxing: squaring up on the PunchingBag '" + bag.name + "'.", bag);
            return bag.transform;
        }

        GameObject existing = GameObject.Find("Boxing_Target");
        if (existing != null)
            return existing.transform;

        Debug.LogWarning("Boxing: no PunchingBag or BoxingTarget in the scene - " +
            "creating a stand-in capsule. Punches will not hit anything.");

        GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        dummy.name = "Boxing_Target";
        dummy.transform.position = boxer.position + boxer.forward * 1.6f;
        dummy.transform.localScale = new Vector3(0.45f, 0.85f, 0.45f);

        // No collider - nothing here does physics, and a stray trigger would only confuse.
        Collider collider = dummy.GetComponent<Collider>();
        if (collider != null)
            Object.DestroyImmediate(collider);

        Debug.Log("Boxing: created 'Boxing_Target' as a stand-in opponent. " +
            "Replace it with the real one and re-assign it on BoxerStance and BoxingController.",
            dummy);

        return dummy.transform;
    }

    /// <summary>
    /// Points the existing CameraFollow at this boxer.
    /// </summary>
    /// <remarks>
    /// CameraFollow falls back to "the first Controller in the scene" when its target is
    /// empty. This rig uses BoxingController, not Controller, so that fallback finds nothing
    /// and the camera simply never follows - which is exactly what was happening.
    /// </remarks>
    private static void WireCamera(Transform boxer, Transform target)
    {
        var follow = Object.FindFirstObjectByType<CameraFollow>();
        if (follow == null)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("Boxing: no CameraFollow and no Main Camera - camera not wired.");
                return;
            }

            follow = cam.gameObject.AddComponent<CameraFollow>();
            Debug.Log("Boxing: added CameraFollow to '" + cam.name + "'.", cam);
        }

        follow.SetTarget(boxer, snap: true);
        follow.SetOpponent(target);
        EditorUtility.SetDirty(follow);

        Debug.Log("Boxing: camera follows '" + boxer.name + "', framed on '" + target.name + "'.",
            follow);
    }

    private static AnimationClip PickIdle()
    {
        var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(
            Root + "/Locomotion/Move_Idle.anim");

        if (idle != null)
            return idle;

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip",
                     new[] { Root + "/Orthodox/Idle" }))
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                AssetDatabase.GUIDToAssetPath(guid));
            if (clip != null)
                return clip;
        }

        return null;
    }

    /************************************************************************************/

    [MenuItem("Tools/Undisputed/Boxing/Build Upper Body Mask")]
    public static AvatarMask BuildMask()
    {
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
        if (mask == null)
        {
            mask = new AvatarMask();
            AssetDatabase.CreateAsset(mask, MaskPath);
        }

        // Root off so the punch layer never moves the character - whatever drives the legs
        // keeps ownership of position. Legs off for the same reason.
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers, true);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFootIK, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFootIK, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftHandIK, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightHandIK, false);

        EditorUtility.SetDirty(mask);
        AssetDatabase.SaveAssets();
        Debug.Log("Boxing: upper-body mask written to " + MaskPath, mask);
        return mask;
    }

    /************************************************************************************/

    [MenuItem("Tools/Undisputed/Boxing/Build Boxing Rig")]
    public static void BuildRig()
    {
        PunchLibrary library = BuildLibrary();
        AvatarMask mask = BuildMask();

        if (library.Entries.Count == 0)
        {
            Debug.LogError("Boxing: library is empty; not building a rig.");
            return;
        }

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(BennettPath);
        if (model == null)
        {
            Debug.LogError("Boxing: could not find " + BennettPath);
            return;
        }

        GameObject existing = GameObject.Find(RigName);
        if (existing != null)
            Object.DestroyImmediate(existing);

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        instance.name = RigName;
        instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogError("Boxing: Bennett needs a Humanoid Animator.");
            return;
        }

        animator.runtimeAnimatorController = null;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.applyRootMotion = false;   // the legs animator owns movement

        GameObject host = animator.gameObject;

        AnimancerComponent animancer = host.GetComponent<AnimancerComponent>()
            ?? host.AddComponent<AnimancerComponent>();
        animancer.Animator = animator;

        // FinalIK must solve after Animancer writes the pose; adding it here and letting
        // BipedReferences auto-detect avoids hand-assigning 15 bones.
        FullBodyBipedIK ik = host.GetComponent<FullBodyBipedIK>();
        if (ik == null)
        {
            ik = host.AddComponent<FullBodyBipedIK>();
            var references = new BipedReferences();
            BipedReferences.AutoDetectReferences(
                ref references, host.transform, BipedReferences.AutoDetectParams.Default);
            ik.solver.SetToReferences(references);
        }

        BoxingController controller = host.GetComponent<BoxingController>()
            ?? host.AddComponent<BoxingController>();

        controller.Library = library;
        controller.UpperBodyMask = mask;
        controller.IK = ik;

        LookAtIK lookAt = host.GetComponent<LookAtIK>() ?? host.AddComponent<LookAtIK>();
        WireLookAt(lookAt, animator);

        // Two IK components on one object solve in an order Unity does not guarantee, which
        // shows up as the head fighting the punch. IKExecutionOrder pins it: body first,
        // then the look-at on top.
        IKExecutionOrder order = host.GetComponent<IKExecutionOrder>()
            ?? host.AddComponent<IKExecutionOrder>();
        order.IKComponents = new IK[] { ik, lookAt };
        EditorUtility.SetDirty(order);

        Transform target = FindOrCreateTarget(instance.transform);

        // Stance goes on the instance root, not the Animator's object. If the Animator ever
        // sits on a child, moving that child would slide the rig inside itself.
        BoxerStance stance = instance.GetComponent<BoxerStance>()
            ?? instance.AddComponent<BoxerStance>();
        stance.Target = target;
        stance.LookAt = lookAt;

        // The existing glove sensors. They self-configure from the Animator and find the bag
        // themselves; all this rig has to do is arm them across the strike window.
        BoxerHitboxes hitboxes = host.GetComponent<BoxerHitboxes>()
            ?? host.AddComponent<BoxerHitboxes>();
        EditorUtility.SetDirty(hitboxes);

        controller.Hitboxes = hitboxes;
        controller.Stance = stance;

        WireCamera(instance.transform, target);

        // Punch aiming and body facing must agree, so both read the same transform.
        controller.Target = target;

        EditorUtility.SetDirty(stance);
        EditorUtility.SetDirty(controller);
        EditorSceneManager.MarkSceneDirty(instance.scene);
        Selection.activeGameObject = instance;

        Debug.Log($"Boxing: built '{RigName}' with {library.Entries.Count} punches. " +
            "Press Play. LMB = left hand, RMB = right hand; flick for a jab, " +
            "drag sideways for a hook, up for an uppercut, down for the body.", instance);
    }
}
