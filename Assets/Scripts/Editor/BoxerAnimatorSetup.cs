using System.Collections.Generic;
using System.IO;
using RootMotion;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// One-click rig for <see cref="BoxerPunchController"/>:
///  • Tools ▸ Boxer ▸ Auto-Assign Clips By Name — sorts every clip under Assets/Animations into hand + punch type
///    from its file name ("left jab", "right hook", "stomach right", "Idle"…) and makes the guard clip loop.
///  • Tools ▸ Boxer ▸ Setup Upper-Body Punch Rig — builds the upper-body avatar mask, the punch layer with one
///    state per clip (conditioned on hand, type and index), the parameters and transitions, and adds/configures
///    the Final IK, hitbox and feedback components on the selected character. Safe to run again any time.
/// </summary>
public static class BoxerAnimatorSetup
{
    private const string SetupMenu = "Tools/Boxer/Setup Upper-Body Punch Rig";
    private const string AssignMenu = "Tools/Boxer/Auto-Assign Clips By Name";
    private const string ClipFolder = "Assets/Animations";
    private const string MaskPath = "Assets/Animations/UpperBody.mask";
    private const string DefaultControllerPath = "Assets/Animations/Player.controller";

    private static readonly BoxerPunchController.PunchType[] AllTypes =
    {
        BoxerPunchController.PunchType.Straight, BoxerPunchController.PunchType.Hook,
        BoxerPunchController.PunchType.Body, BoxerPunchController.PunchType.Uppercut,
    };

    [MenuItem(SetupMenu, true)]
    [MenuItem(AssignMenu, true)]
    private static bool Validate() => Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<Animator>() != null;

    // ---------------------------------------------------------------- Auto-assign

    [MenuItem(AssignMenu)]
    private static void AutoAssignMenu()
    {
        GameObject boxer = Selection.activeGameObject;
        BoxerPunchController punch = boxer.GetComponent<BoxerPunchController>();
        if (punch == null) punch = Undo.AddComponent<BoxerPunchController>(boxer);
        AutoAssignClips(punch);
    }

    /// <summary>Fill the guard clip and the per-hand, per-type lists from clip file names.</summary>
    public static void AutoAssignClips(BoxerPunchController punch)
    {
        Undo.RecordObject(punch, "Auto-Assign Boxer Clips");

        var left = new Dictionary<BoxerPunchController.PunchType, List<AnimationClip>>();
        var right = new Dictionary<BoxerPunchController.PunchType, List<AnimationClip>>();
        foreach (var t in AllTypes) { left[t] = new List<AnimationClip>(); right[t] = new List<AnimationClip>(); }
        AnimationClip guard = null;
        var report = new List<string>();

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { ClipFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(asset is AnimationClip clip) || clip.name.StartsWith("__preview__")) continue;

                string label = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (label == "mixamo.com") label = clip.name.ToLowerInvariant();   // file named like the take: fall back to the clip name
                if (Has(label, "idle", "guard", "stance", "ready"))
                {
                    if (guard == null) { guard = clip; report.Add($"guard ← {label}"); }
                    continue;
                }

                BoxerPunchController.PunchType type =
                    Has(label, "hook") ? BoxerPunchController.PunchType.Hook :
                    Has(label, "upper") ? BoxerPunchController.PunchType.Uppercut :
                    Has(label, "stomach", "body", "liver", "gut", "rib") ? BoxerPunchController.PunchType.Body :
                    BoxerPunchController.PunchType.Straight;

                bool isLeft = Has(label, "left", "lead", "jab");
                bool isRight = Has(label, "right", "rear", "cross");
                if (isLeft && isRight) isLeft = label.IndexOf("left") >= 0 && label.IndexOf("left") < label.IndexOf("right");
                if (!isLeft && !isRight) { report.Add($"skipped '{label}' (no left/right in the name)"); continue; }

                (isLeft ? left : right)[type].Add(clip);
                report.Add($"{(isLeft ? "LEFT" : "RIGHT")} {type} ← {label}");
            }
        }

        if (guard != null) punch.guardClip = guard;
        foreach (var t in AllTypes)
        {
            // Order every type HIGH → LOW (Head, UpperBody, LowerBody) so the controller can pick by aim height.
            left[t].Sort((a, b) => HeightRank(a).CompareTo(HeightRank(b)));
            right[t].Sort((a, b) => HeightRank(a).CompareTo(HeightRank(b)));
            punch.leftClips.Set(t, left[t].ToArray());
            punch.rightClips.Set(t, right[t].ToArray());
        }

        // Say plainly which punches still have no animation of their own (they borrow the nearest type at runtime).
        List<string> missing = new List<string>();
        foreach (var t in AllTypes)
        {
            if (left[t].Count == 0) missing.Add($"Left{t}");
            if (right[t].Count == 0) missing.Add($"Right{t}");
        }
        if (missing.Count > 0)
            report.Add("NO CLIPS YET (these borrow the nearest type — export e.g. RightUppercutHeadFull.fbx / " +
                       "RightUppercutUpperBodyFull.fbx / RightUppercutLowerBodyFull.fbx): " + string.Join(", ", missing));

        EnsureLooping(punch.guardClip, true);
        EditorUtility.SetDirty(punch);
        Debug.Log("Boxer clips assigned:\n  " + string.Join("\n  ", report), punch);
    }

    /// <summary>0 = head/face, 1 = upper body, 2 = lower body — the order the controller expects inside a type.</summary>
    private static int HeightRank(AnimationClip clip)
    {
        string label = clip != null ? clip.name.ToLowerInvariant() : "";
        string path = clip != null ? AssetDatabase.GetAssetPath(clip).ToLowerInvariant() : "";
        string both = (label + " " + Path.GetFileNameWithoutExtension(path)).Replace(" ", "").Replace("_", "");
        if (both.Contains("lowerbody") || both.Contains("lower") || both.Contains("low")) return 2;
        if (both.Contains("head") || both.Contains("face")) return 0;
        return 1;   // "upperbody" / "body" / anything else
    }

    private static bool Has(string label, params string[] words)
    {
        foreach (string w in words) if (label.Contains(w)) return true;
        return false;
    }

    /// <summary>Make sure a clip's import settings loop (guard) or don't (punches).</summary>
    private static void EnsureLooping(AnimationClip clip, bool loop)
    {
        if (clip == null) return;
        string path = AssetDatabase.GetAssetPath(clip);
        if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) return;

        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;

        bool changed = false;
        foreach (ModelImporterClipAnimation c in clips)
        {
            if (c.name != clip.name || c.loopTime == loop) continue;
            c.loopTime = loop;
            c.loopPose = loop;
            changed = true;
        }
        if (!changed) return;

        importer.clipAnimations = clips;
        importer.SaveAndReimport();
    }

    // ---------------------------------------------------------------- Setup

    [MenuItem(SetupMenu)]
    private static void Setup()
    {
        GameObject boxer = Selection.activeGameObject;
        Animator animator = boxer.GetComponent<Animator>();

        if (!animator.isHuman)
        {
            EditorUtility.DisplayDialog("Boxer setup", "The Animator's avatar must be Humanoid (set Rig ▸ Animation Type on the model).", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Setup Boxer Punch Rig");
        int undoGroup = Undo.GetCurrentGroup();

        BoxerPunchController punch = boxer.GetComponent<BoxerPunchController>();
        if (punch == null) punch = Undo.AddComponent<BoxerPunchController>(boxer);
        if (punch.leftClips.IsEmpty && punch.rightClips.IsEmpty) AutoAssignClips(punch);
        else EnsureLooping(punch.guardClip, true);

        AnimatorController controller = GetOrCreateController(animator);
        AvatarMask mask = GetOrCreateUpperBodyMask();

        EnsureParameter(controller, "PunchLeft", AnimatorControllerParameterType.Trigger);
        EnsureParameter(controller, "PunchRight", AnimatorControllerParameterType.Trigger);
        EnsureParameter(controller, "PunchIndex", AnimatorControllerParameterType.Int);
        EnsureParameter(controller, "PunchType", AnimatorControllerParameterType.Int);
        EnsureParameter(controller, "PunchSpeed", AnimatorControllerParameterType.Float, 1f);

        Motion guard = EnsureBaseGuard(controller, punch.guardClip);
        BuildUpperBodyLayer(controller, punch, mask, guard);

        SetupCharacterComponents(boxer, animator, punch);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Undo.CollapseUndoOperations(undoGroup);

        Debug.Log($"Boxer punch rig ready on '{boxer.name}'. Left mouse = left hand, right mouse = right hand; " +
                  "hold to aim/charge, release to throw; middle mouse / Ctrl to block.", boxer);
    }

    private static AnimatorController GetOrCreateController(Animator animator)
    {
        if (animator.runtimeAnimatorController is AnimatorController existing) return existing;

        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(DefaultControllerPath);
        if (controller == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultControllerPath));
            controller = AnimatorController.CreateAnimatorControllerAtPath(DefaultControllerPath);
        }

        Undo.RecordObject(animator, "Assign Animator Controller");
        animator.runtimeAnimatorController = controller;
        return controller;
    }

    private static AvatarMask GetOrCreateUpperBodyMask()
    {
        AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
        if (mask != null) return mask;

        mask = new AvatarMask();
        for (AvatarMaskBodyPart part = 0; part < AvatarMaskBodyPart.LastBodyPart; part++)
            mask.SetHumanoidBodyPartActive(part, true);

        // Legs and root stay with the base layer (locomotion / Legs Animator); everything above the hips punches.
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFootIK, false);
        mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFootIK, false);

        AssetDatabase.CreateAsset(mask, MaskPath);
        return mask;
    }

    private static void EnsureParameter(AnimatorController controller, string name, AnimatorControllerParameterType type, float defaultFloat = 0f)
    {
        foreach (AnimatorControllerParameter p in controller.parameters)
            if (p.name == name) return;

        controller.AddParameter(new AnimatorControllerParameter { name = name, type = type, defaultFloat = defaultFloat });
    }

    // ---------------------------------------------------------------- Stance energy idle blend

    private const string IdleBlendMenu = "Tools/Boxer/Setup Stance Idle Blend (Energy)";

    [MenuItem(IdleBlendMenu, true)]
    private static bool ValidateIdleBlend() => Validate();

    /// <summary>
    /// Builds a 1D blend tree (parameter "Energy") from the High / Mid / Low energy stance idles in
    /// Assets/Refrences/Idles and makes it the base layer's default state. BoxerPunchController then drives
    /// "Energy" from stamina, so a fresh boxer bounces and a gassed one sags — smoothly.
    /// </summary>
    [MenuItem(IdleBlendMenu)]
    private static void SetupIdleBlend()
    {
        GameObject boxer = Selection.activeGameObject;
        Animator animator = boxer.GetComponent<Animator>();
        AnimatorController controller = GetOrCreateController(animator);

        AnimationClip low = null, mid = null, high = null;
        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets/Refrences", "Assets/Animations" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string label = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (!label.Contains("energy")) continue;
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(o is AnimationClip c) || c.name.StartsWith("__preview__")) continue;
                if (label.Contains("low")) low = c;
                else if (label.Contains("mid")) mid = c;
                else if (label.Contains("high")) high = c;
                EnsureLooping(c, true);
                break;
            }
        }

        if (low == null && mid == null && high == null)
        {
            Debug.LogWarning("Stance idle blend: no clips with 'energy' in the name found under Assets/Refrences or Assets/Animations.", boxer);
            return;
        }

        EnsureParameter(controller, "Energy", AnimatorControllerParameterType.Float, 1f);

        BlendTree tree = new BlendTree
        {
            name = "Stance Energy",
            blendParameter = "Energy",
            blendType = BlendTreeType.Simple1D,
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(tree, controller);
        if (low != null) tree.AddChild(low, 0f);
        if (mid != null) tree.AddChild(mid, 0.55f);
        if (high != null) tree.AddChild(high, 1f);

        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        AnimatorState state = machine.defaultState;
        if (state == null) { state = machine.AddState("Stance"); machine.defaultState = state; }
        state.motion = tree;

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log($"Stance idle blend ready on '{boxer.name}': Energy 0 → {(low != null ? low.name : "-")}, " +
                  $"0.55 → {(mid != null ? mid.name : "-")}, 1 → {(high != null ? high.name : "-")}. Stamina drives it automatically.", boxer);
    }

    /// <summary>
    /// Wire ONLY the animator side for this boxer: parameters + the punch layer built from the assigned clips.
    /// Never touches the base layer (a hand-made Stance Energy blend stays exactly as it is) — its default
    /// motion is reused as the punch layer's guard state. Used by <c>BoxerAutoWire</c>; safe to call any time.
    /// </summary>
    public static void WireAnimator(BoxerPunchController punch)
    {
        Animator animator = punch.GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return;

        AnimatorController controller = GetOrCreateController(animator);
        AvatarMask mask = GetOrCreateUpperBodyMask();

        EnsureParameter(controller, "PunchLeft", AnimatorControllerParameterType.Trigger);
        EnsureParameter(controller, "PunchRight", AnimatorControllerParameterType.Trigger);
        EnsureParameter(controller, "PunchIndex", AnimatorControllerParameterType.Int);
        EnsureParameter(controller, "PunchType", AnimatorControllerParameterType.Int);
        EnsureParameter(controller, "PunchSpeed", AnimatorControllerParameterType.Float, 1f);

        Motion guardMotion = punch.guardClip;
        if (guardMotion == null && controller.layers.Length > 0 && controller.layers[0].stateMachine.defaultState != null)
            guardMotion = controller.layers[0].stateMachine.defaultState.motion;   // reuse the base idle / stance blend

        BuildUpperBodyLayer(controller, punch, mask, guardMotion);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log($"Boxer animator wired: punch layer rebuilt on '{controller.name}' from the assigned clips.", punch);
    }

    private static Motion EnsureBaseGuard(AnimatorController controller, AnimationClip guardClip)
    {
        AnimatorStateMachine baseMachine = controller.layers[0].stateMachine;

        if (guardClip != null)
        {
            if (baseMachine.defaultState == null)
            {
                AnimatorState guardState = baseMachine.AddState("Guard");
                guardState.motion = guardClip;
                baseMachine.defaultState = guardState;
            }
            else if (baseMachine.defaultState.motion == null)
            {
                baseMachine.defaultState.motion = guardClip;
            }
            return guardClip;
        }

        return baseMachine.defaultState != null ? baseMachine.defaultState.motion : null;
    }

    private static void BuildUpperBodyLayer(AnimatorController controller, BoxerPunchController punch, AvatarMask mask, Motion guardMotion)
    {
        string layerName = string.IsNullOrEmpty(punch.upperBodyLayerName) ? "Upper Body" : punch.upperBodyLayerName;

        // Rebuild from scratch so re-running after changing clips stays in sync.
        AnimatorControllerLayer[] layers = controller.layers;
        for (int i = layers.Length - 1; i >= 1; i--)
        {
            if (layers[i].name != layerName) continue;
            if (layers[i].stateMachine != null) Object.DestroyImmediate(layers[i].stateMachine, true);
            controller.RemoveLayer(i);
        }

        AnimatorStateMachine machine = new AnimatorStateMachine { name = layerName, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(machine, controller);

        controller.AddLayer(new AnimatorControllerLayer
        {
            name = layerName,
            stateMachine = machine,
            avatarMask = mask,
            blendingMode = AnimatorLayerBlendingMode.Override,
            defaultWeight = 1f,
        });

        AnimatorState guard = machine.AddState("Guard", new Vector3(300f, 200f, 0f));
        guard.motion = guardMotion;
        machine.defaultState = guard;

        float y = -100f;
        foreach (var type in AllTypes)
        {
            AddPunchStates(machine, guard, punch.leftClips.For(type), "PunchLeft", type, "L", ref y);
            AddPunchStates(machine, guard, punch.rightClips.For(type), "PunchRight", type, "R", ref y);
        }
    }

    private static void AddPunchStates(AnimatorStateMachine machine, AnimatorState guard, AnimationClip[] clips, string trigger,
        BoxerPunchController.PunchType type, string prefix, ref float y)
    {
        if (clips == null) return;

        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i] == null) continue;

            AnimatorState state = machine.AddState($"{prefix} {type} {i}", new Vector3(650f, y, 0f));
            y += 60f;
            state.motion = clips[i];
            state.speedParameterActive = true;      // BoxerPunchController sets PunchSpeed to match its IK timing
            state.speedParameter = "PunchSpeed";

            AnimatorStateTransition enter = machine.AddAnyStateTransition(state);
            enter.hasExitTime = false;
            enter.duration = 0.06f;
            enter.canTransitionToSelf = false;
            enter.AddCondition(AnimatorConditionMode.If, 0f, trigger);
            enter.AddCondition(AnimatorConditionMode.Equals, (int)type, "PunchType");
            enter.AddCondition(AnimatorConditionMode.Equals, i, "PunchIndex");

            AnimatorStateTransition exit = state.AddTransition(guard);
            exit.hasExitTime = true;
            exit.exitTime = 0.85f;
            exit.duration = 0.2f;

            EnsureLooping(clips[i], false);
        }
    }

    // ---------------------------------------------------------------- Character components

    private static void SetupCharacterComponents(GameObject boxer, Animator animator, BoxerPunchController punch)
    {
        FullBodyBipedIK bodyIK = boxer.GetComponent<FullBodyBipedIK>();
        if (bodyIK == null) bodyIK = Undo.AddComponent<FullBodyBipedIK>(boxer);
        if (bodyIK.references == null || bodyIK.references.pelvis == null)
        {
            BipedReferences references = new BipedReferences();
            if (BipedReferences.AutoDetectReferences(ref references, boxer.transform, BipedReferences.AutoDetectParams.Default))
                bodyIK.SetReferences(references, null);
            else
                Debug.LogWarning("Boxer setup: Final IK could not auto-detect the biped; assign the references on FullBodyBipedIK.", boxer);
        }
        BoxerPunchController.ConfigureFullBody(bodyIK.solver);

        LookAtIK lookAtIK = boxer.GetComponent<LookAtIK>();
        if (lookAtIK == null) lookAtIK = Undo.AddComponent<LookAtIK>(boxer);
        if (lookAtIK.solver.head == null || lookAtIK.solver.head.transform == null)
        {
            List<Transform> spine = new List<Transform>();
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
            {
                Transform t = animator.GetBoneTransform(bone);
                if (t != null) spine.Add(t);
            }
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null) lookAtIK.solver.SetChain(spine.ToArray(), head, new Transform[0], boxer.transform);
        }
        lookAtIK.solver.IKPositionWeight = 0f;
        lookAtIK.solver.bodyWeight = 0.4f;
        lookAtIK.solver.headWeight = 0.7f;
        lookAtIK.solver.eyesWeight = 0f;
        lookAtIK.solver.clampWeight = 0.5f;

        IKExecutionOrder order = boxer.GetComponent<IKExecutionOrder>();
        if (order == null) order = Undo.AddComponent<IKExecutionOrder>(boxer);
        order.IKComponents = new IK[] { lookAtIK, bodyIK };

        if (boxer.GetComponent<ImpactFeedback>() == null) Undo.AddComponent<ImpactFeedback>(boxer);
        if (boxer.GetComponent<BoxerPhysicsBody>() == null) Undo.AddComponent<BoxerPhysicsBody>(boxer);

        // The active ragdoll replaces any other physics-pose system on the character.
        foreach (MonoBehaviour mb in boxer.GetComponents<MonoBehaviour>())
        {
            if (mb == null || !mb.enabled || mb.GetType().Name != "RagdollAnimator2") continue;
            Undo.RecordObject(mb, "Disable Ragdoll Animator 2");
            mb.enabled = false;
            Debug.Log("Boxer setup: disabled Ragdoll Animator 2 — BoxerPhysicsBody now drives the physical body.", boxer);
        }

        BoxerHitboxes hitboxes = boxer.GetComponent<BoxerHitboxes>();
        if (hitboxes == null) hitboxes = Undo.AddComponent<BoxerHitboxes>(boxer);
        SerializedObject so = new SerializedObject(hitboxes);
        SerializedProperty requireArmed = so.FindProperty("requireArmed");
        if (requireArmed != null) { requireArmed.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); }

        SerializedObject punchSo = new SerializedObject(punch);
        punchSo.FindProperty("bodyIK").objectReferenceValue = bodyIK;
        punchSo.FindProperty("lookAtIK").objectReferenceValue = lookAtIK;
        punchSo.FindProperty("hitboxes").objectReferenceValue = hitboxes;
        punchSo.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(boxer);
    }
}
