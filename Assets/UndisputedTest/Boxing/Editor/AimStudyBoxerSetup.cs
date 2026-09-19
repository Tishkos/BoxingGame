using System.Collections.Generic;
using System.Linq;
using Animancer;
using UnityEngine.Animations;
using UnityEngine.Playables;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the aim-study sandbox: Bennett, three stances, a jab per hand, and the two solvers
/// per arm that actually make a punch land where you aim it.
/// </summary>
public static class AimStudyBoxerSetup
{
    private const string BennettPath = "Assets/Character/Bennett.fbx";
    private const string RigName = "AimStudy_Boxer";
    // Idles from the SAME capture as the jabs. The Refrences idles are a different shoot
    // entirely, so their guard does not line up with an Undisputed punch and the upper body
    // visibly jumps when the punch layer blends in. These three are the same performer, and
    // "Gassed" is a real low-energy capture rather than a slowed-down copy of the other one.
    private const string IdleFolder = "Assets/Animations/Undisputed/Orthodox/Idle";

    private const string HighIdle = "SunnyEdwards_Idle_Traditional_01_Orthodox";
    private const string MidIdle = "SunnyEdwards_Idle_Traditional_02_Orthodox";
    private const string LowIdle = "SunnyEdwards_Idle_Traditional_Gassed_01_Orthodox";

    private const string FallbackIdleFolder = "Assets/Refrences/Idles";
    private const string PunchFolder = "Assets/Animations/Undisputed/Orthodox/Punches";

    private const string RightJab =
        "SunnyEdwards_Head_Straight_R2_TRAD_INT5_OptimizedForHead_RJab_Power_Orthodox_MxM_MOD";
    private const string LeftJab =
        "SunnyEdwards_Head_Jab_R2_TRAD_INT5_B_OptimizedForHead_LJab_Power_Orthodox_MxM_MOD";

    [MenuItem("Tools/Undisputed/Boxing/Aim Study Boxer")]
    public static void Build()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(BennettPath);
        if (model == null)
        {
            Debug.LogError("Aim study boxer: could not find " + BennettPath);
            return;
        }

        // Keep whatever was tuned last time. Rebuilding used to throw it all away, which is
        // no good when the settings ARE the work.
        AimStudyBoxer previous = null;
        GameObject existing = GameObject.Find(RigName);
        if (existing != null)
        {
            previous = existing.GetComponentInChildren<AimStudyBoxer>();
            if (previous != null)
                previous = Object.Instantiate(previous);   // detached copy to read from
            Object.DestroyImmediate(existing);
        }

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        instance.name = RigName;
        instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogError("Aim study boxer: Bennett needs a Humanoid Animator.", instance);
            return;
        }

        animator.runtimeAnimatorController = null;   // Animancer owns the playback
        animator.applyRootMotion = false;            // the component moves the transform
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        GameObject host = animator.gameObject;

        AnimancerComponent animancer = host.GetComponent<AnimancerComponent>()
            ?? host.AddComponent<AnimancerComponent>();
        animancer.Animator = animator;

        AimStudyBoxer study = host.GetComponent<AimStudyBoxer>()
            ?? host.AddComponent<AimStudyBoxer>();

        study.HighEnergy = Idle(HighIdle, "OrthoHighEnergy");
        study.MidEnergy = Idle(MidIdle, "OrthoMidEnergy");
        study.LowEnergy = Idle(LowIdle, "OrthoLowEnergy");

        // By name first, then by pattern. Hardcoding exact filenames meant that deleting or
        // renaming one clip left the slot silently empty and that hand simply never punched.
        study.LeftJab = Jab(LeftJab, "LJab");
        study.RightJab = Jab(RightJab, "RJab");

        // Three fixed masks. Only the weights change per punch: the punching arm runs at full,
        // the torso and the off hand at their sliders.
        study.TorsoMask = RegionMask("Torso", AvatarMaskBodyPart.Body);
        study.LeftArmMask = RegionMask("LeftArm", AvatarMaskBodyPart.LeftArm,
                                                  AvatarMaskBodyPart.LeftFingers);
        study.RightArmMask = RegionMask("RightArm", AvatarMaskBodyPart.RightArm,
                                                    AvatarMaskBodyPart.RightFingers);
        study.Target = FindOrCreateTarget(instance.transform, study);

        if (previous != null)
        {
            Restore(previous, study);
            Object.DestroyImmediate(previous.gameObject);
            Debug.Log("Aim study boxer: carried the previous tuning over.", study);
        }

        // AFTER the restore, so the strike times belong to the clips actually assigned. Measuring
        // first meant a hand-swapped clip kept the old clip's strike, and the mismatch showed up
        // as one hand punching faster than the other.
        if (study.LeftJab != null)
            study.LeftStrike = MeasureStrike(animator, study.LeftJab, true);
        if (study.RightJab != null)
            study.RightStrike = MeasureStrike(animator, study.RightJab, false);

        study.BuildChains();

        EditorUtility.SetDirty(study);
        EditorSceneManager.MarkSceneDirty(instance.scene);
        Selection.activeGameObject = instance;

        Debug.Log($"Aim study boxer: built '{RigName}'.\n" +
            $"   stances : {Name(study.HighEnergy)} / {Name(study.MidEnergy)} / {Name(study.LowEnergy)}\n" +
            $"   left jab : {Name(study.LeftJab)}  strike {study.LeftStrike:0.000}\n" +
            $"   right jab: {Name(study.RightJab)}  strike {study.RightStrike:0.000}\n" +
            "   L2/LMB and R2/RMB punch, left stick or WASD moves, RIGHT STICK aims:\n" +
            "   up = head raised, down = BODY, left/right = across. 1/2/3 switch stance.",
            instance);

        Warn(study);
    }

    [MenuItem("Tools/Undisputed/Boxing/Upgrade Selected Aim Study Boxer")]
    public static void UpgradeSelected()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("Stop Play mode before upgrading the Aim Study rig.");
            return;
        }
        AimStudyBoxer study = SelectedStudy();
        if (study == null) return;
        Animator animator = study.GetComponent<AnimancerComponent>()?.Animator;
        if (animator == null || !animator.isHuman || animator.gameObject != study.gameObject)
        {
            Debug.LogError("The study needs a Humanoid Animator on the same object.", study);
            return;
        }

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Upgrade Aim Study Punches");
        Undo.RecordObject(study, "Upgrade Aim Study Punches");
        if (study.TorsoMask == null) study.TorsoMask = ExistingRegionMask("Torso", AvatarMaskBodyPart.Body);
        if (study.LeftArmMask == null) study.LeftArmMask = ExistingRegionMask("LeftArm", AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.LeftFingers);
        if (study.RightArmMask == null) study.RightArmMask = ExistingRegionMask("RightArm", AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.RightFingers);

        if (study.LeftJab == null)
            study.LeftJab = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{PunchFolder}/Jab/Jab_Left.anim") ?? Jab(LeftJab, "LJab");
        if (study.RightJab == null)
            study.RightJab = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{PunchFolder}/Jab/Jab_Right.anim") ?? Jab(RightJab, "RJab");
        study.UseStamina = true;
        study.MatchImpactTiming = true;
        study.UsePerMotionTiming = false;
        study.ComboAcceleration = 0f;
        study.TiredSpeed = Mathf.Min(study.TiredSpeed, 0.22f);
        study.SideLeanDegrees = Mathf.Max(study.SideLeanDegrees, 28f);
        study.BackLeanDegrees = Mathf.Max(study.BackLeanDegrees, 30f);
        study.ForwardLeanDegrees = Mathf.Max(study.ForwardLeanDegrees, 18f);
        study.LeanResponse = Mathf.Min(study.LeanResponse, 0.1f);
        AddPunchVocabulary(study, animator);
        study.HeadDefense = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Refrences/Guard/HeadGuard.anim");
        study.BodyDefense = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Refrences/Guard/BodyGuard.anim");
        WireFootsteps(study);
        if (study.Hitboxes == null)
            study.Hitboxes = study.GetComponent<BoxerHitboxes>() ?? Undo.AddComponent<BoxerHitboxes>(study.gameObject);
        var sensors = new SerializedObject(study.Hitboxes);
        sensors.FindProperty("debugPunchKey").boolValue = false;
        sensors.ApplyModifiedProperties();
        if (study.FeedbackCamera == null && Camera.main != null)
            study.FeedbackCamera = Camera.main.GetComponent<CameraFollow>();

        PrefabUtility.RecordPrefabInstancePropertyModifications(study);
        EditorUtility.SetDirty(study);
        EditorSceneManager.MarkSceneDirty(study.gameObject.scene);
        Undo.CollapseUndoOperations(group);
        Debug.Log($"Upgraded '{study.name}' in place: {study.Punches.Length} additional punch motions. " +
            "Existing jab clips, pose alignment, target and scene objects were preserved. " +
            "Applied matched timing and strong exhaustion slowdown; guard clips and footstep recordings assigned. " +
            "Optional Left/Right Trigger Jab slots accept your own neutral-trigger clips. Save the scene when satisfied. " +
            "R1 = head defense, L1 = body defense; right stick leans while idle or guarding. " +
            "L2/R2: tap for quick punches, hold to prepare, release to throw; holding drains stamina. " +
            "Stick up selects hooks, down selects uppercuts; steer during preparation. " +
            "F8 hides the tuning panel.", study);
        ValidateStudy(study);
    }

    private static void WireFootsteps(AimStudyBoxer study)
    {
        FootstepAudio audio = study.GetComponent<FootstepAudio>();
        bool added = audio == null;
        if (added) audio = Undo.AddComponent<FootstepAudio>(study.gameObject);
        Undo.RecordObject(audio, "Wire Aim Study Footsteps");
        audio.enabled = true;
        var settings = new SerializedObject(audio);
        AssignMissingAudio(settings.FindProperty("stepClips"), "Assets/Sounds/Normal Step");
        AssignMissingAudio(settings.FindProperty("squeakClips"), "Assets/Sounds/Squeak");
        if (added) settings.FindProperty("masterVolume").floatValue = 0.65f;
        settings.ApplyModifiedProperties();
        PrefabUtility.RecordPrefabInstancePropertyModifications(audio);
        EditorUtility.SetDirty(audio);
    }

    private static void AssignMissingAudio(SerializedProperty property, string folder)
    {
        for (int i = 0; i < property.arraySize; i++)
            if (property.GetArrayElementAtIndex(i).objectReferenceValue != null) return;
        if (!AssetDatabase.IsValidFolder(folder)) return;
        AudioClip[] clips = AssetDatabase.FindAssets("t:AudioClip", new[] { folder })
            .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, System.StringComparer.Ordinal)
            .Select(AssetDatabase.LoadAssetAtPath<AudioClip>).Where(c => c != null).ToArray();
        property.arraySize = clips.Length;
        for (int i = 0; i < clips.Length; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = clips[i];
    }

    private static AimStudyBoxer SelectedStudy()
    {
        GameObject selected = Selection.activeGameObject;
        AimStudyBoxer study = selected != null ? selected.GetComponentInParent<AimStudyBoxer>() : null;
        if (study == null && selected != null) study = selected.GetComponentInChildren<AimStudyBoxer>(true);
        if (study != null)
        {
            if (!EditorUtility.IsPersistent(study)) return study;
            Debug.LogWarning("Select the boxer in the scene hierarchy, not a prefab asset.", study);
            return null;
        }
        AimStudyBoxer[] all = Object.FindObjectsByType<AimStudyBoxer>();
        if (all.Length == 1) return all[0];
        Debug.LogWarning("Select the Aim Study boxer to upgrade or validate; no unambiguous rig was found.");
        return null;
    }

    private static AvatarMask ExistingRegionMask(string name, params AvatarMaskBodyPart[] parts)
    {
        string path = $"Assets/UndisputedTest/Boxing/Punch{name}.mask";
        AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
        if (mask != null) return mask;
        mask = new AvatarMask { name = "Punch" + name };
        for (AvatarMaskBodyPart part = 0; part < AvatarMaskBodyPart.LastBodyPart; part++)
            mask.SetHumanoidBodyPartActive(part, false);
        foreach (AvatarMaskBodyPart part in parts) mask.SetHumanoidBodyPartActive(part, true);
        AssetDatabase.CreateAsset(mask, path);
        AssetDatabase.SaveAssetIfDirty(mask);
        return mask;
    }

    private static void AddPunchVocabulary(AimStudyBoxer study, Animator animator)
    {
        AnimationClip[] candidates = AssetDatabase.FindAssets("t:AnimationClip", new[] { PunchFolder })
            .Select(g => AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(c => c != null && (c.name.StartsWith("SunnyEdwards_")
                || (c.name.StartsWith("TeriHarper_") && c.name.Contains("Hook")))
                && !c.name.Contains("Lunge")
                && !c.name.Contains("Check") && !c.name.Contains("LowBlow") && !c.name.Contains("Dash"))
            .OrderBy(c => c.name.Contains("INT5") ? 1 : 0).ThenBy(c => c.name, System.StringComparer.Ordinal).ToArray();
        var motions = new List<AimStudyBoxer.PunchMotion>(study.Punches ?? new AimStudyBoxer.PunchMotion[0]);
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(animator.avatar));
        if (model == null)
        {
            Debug.LogWarning("Cannot calibrate punch orientation: the avatar's source model was not found. Existing motions are unchanged.", study);
            return;
        }

        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
            Animator preview = instance.GetComponentInChildren<Animator>();
            if (preview == null || !preview.isHuman) return;
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = animator.transform.lossyScale;
            preview.runtimeAnimatorController = null;
            preview.applyRootMotion = false;
            preview.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            for (int hand = 0; hand < 2; hand++)
            foreach (Boxing.PunchType type in new[] { Boxing.PunchType.Jab, Boxing.PunchType.Hook, Boxing.PunchType.Uppercut })
            foreach (Boxing.PunchTarget target in new[] { Boxing.PunchTarget.Head, Boxing.PunchTarget.Body })
            {
                if (type == Boxing.PunchType.Jab && target == Boxing.PunchTarget.Head) continue;
                int desired = type == Boxing.PunchType.Hook ? 3 : 2;
                int existing = motions.Count(m => m != null && m.Clip != null
                    && (int)m.Hand == hand && m.Type == type && m.Target == target);
                if (existing >= desired) continue;
                AnimationClip[] clips = candidates.Where(c => Matches(c.name, hand, type, target)
                    && motions.All(m => m == null || m.Clip != c)).Take(desired - existing).ToArray();
                foreach (AnimationClip clip in clips)
                {
                    float fallback = 0.45f;
                    PunchPlayback.Timing timing = AimStudyBoxer.ReadTiming(clip, fallback);
                    float orientation = MeasureOrientation(preview, study, clip, hand, timing.Strike);
                    motions.Add(new AimStudyBoxer.PunchMotion
                    {
                        Clip = clip, Hand = (Boxing.Hand)hand, Type = type, Target = target,
                        Strike = timing.Strike, Orientation = orientation,
                        ImpactSeconds = type == Boxing.PunchType.Jab ? 0.26f : type == Boxing.PunchType.Hook ? 0.31f : 0.34f,
                    });
                }
            }
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(scene);
        }
        study.Punches = motions.ToArray();
    }

    private static bool Matches(string name, int hand, Boxing.PunchType type, Boxing.PunchTarget target)
    {
        if (!name.Contains(target == Boxing.PunchTarget.Body ? "_Body_" : "_Head_")) return false;
        string side = hand == 0 ? "L" : "R";
        if (type == Boxing.PunchType.Jab) return name.Contains(side + "Jab");
        return name.Contains("_" + side + "_" + type + "_") || name.Contains("_" + side + type + "_");
    }

    private static float MeasureOrientation(Animator animator, AimStudyBoxer study, AnimationClip clip, int hand, float strike)
    {
        PlayableGraph graph = PlayableGraph.Create("Punch orientation calibration");
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(graph, "Pose", animator);
            var mixer = AnimationLayerMixerPlayable.Create(graph, 4);
            output.SetSourcePlayable(mixer);
            AnimationClip idle = study.MidEnergy ?? study.HighEnergy ?? study.LowEnergy;
            if (idle != null)
            {
                var basePose = AnimationClipPlayable.Create(graph, idle);
                basePose.SetApplyFootIK(false);
                graph.Connect(basePose, 0, mixer, 0);
                mixer.SetInputWeight(0, 1f);
            }
            AvatarMask[] masks = { study.TorsoMask, study.LeftArmMask, study.RightArmMask };
            for (int part = 0; part < 3; part++)
            {
                var state = AnimationClipPlayable.Create(graph, clip);
                state.SetApplyFootIK(false);
                state.SetTime(clip.length * strike);
                state.SetSpeed(0f);
                graph.Connect(state, 0, mixer, part + 1);
                mixer.SetLayerMaskFromAvatarMask((uint)(part + 1), masks[part]);
                mixer.SetInputWeight(part + 1, part == 0 ? study.BodyFollow : part == hand + 1 ? 1f : study.OffHandFollow);
            }
            graph.Play();
            graph.Evaluate(0f);
            Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Spine);
            Transform fist = animator.GetBoneTransform(hand == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (chest == null || fist == null) return 0f;
            Vector3 line = Vector3.ProjectOnPlane(fist.position - chest.position, animator.transform.up);
            float yaw = line.sqrMagnitude > 0.0001f ? Vector3.SignedAngle(line, animator.transform.forward, animator.transform.up) : 0f;
            if (Mathf.Abs(yaw) > 90f)
                Debug.LogWarning($"'{clip.name}' requires {yaw:0} degrees of correction. Preview this motion on the rig before keeping it.", clip);
            return Mathf.Clamp(yaw, -90f, 90f);
        }
        finally
        {
            if (graph.IsValid()) graph.Destroy();
        }
    }

    [MenuItem("Tools/Undisputed/Boxing/Validate Selected Aim Study Boxer")]
    public static void ValidateSelected()
    {
        AimStudyBoxer study = SelectedStudy();
        if (study != null) ValidateStudy(study);
    }

    private static void ValidateStudy(AimStudyBoxer study)
    {
        int issues = 0;
        if (study.JabClip(0) == null || study.JabClip(1) == null)
        {
            issues++;
            Debug.LogWarning("Assign Left/Right Trigger Jab overrides or the legacy Left/Right Jab clips.", study);
        }
        if (!study.UseStamina || !study.MatchImpactTiming || study.UsePerMotionTiming || study.TiredSpeed > 0.3f)
        {
            issues++;
            Debug.LogWarning("For matched, strongly exhausted punches, enable stamina and matching, disable per-motion timing, and set Tired Speed to 0.22. Upgrade applies this preset.", study);
        }
        FootstepAudio footsteps = study.GetComponent<FootstepAudio>();
        if (footsteps == null || !footsteps.enabled || !footsteps.HasStepClips)
        {
            issues++;
            Debug.LogWarning("Footstep audio or its recordings are missing/disabled. Re-run Upgrade Selected Aim Study Boxer.", study);
        }
        if (study.HeadDefense == null || study.BodyDefense == null)
        {
            issues++;
            Debug.LogWarning("Head/body defense clips are missing. Re-run Upgrade Selected Aim Study Boxer.", study);
        }
        AvatarMask[] masks = { study.TorsoMask, study.LeftArmMask, study.RightArmMask };
        for (int region = 0; region < masks.Length; region++)
        {
            AvatarMask mask = masks[region];
            AvatarMask expected = AimStudyBoxer.CreateRegionMask(region);
            bool valid = mask != null;
            if (valid)
                for (AvatarMaskBodyPart part = 0; part < AvatarMaskBodyPart.LastBodyPart; part++)
                    valid &= mask.GetHumanoidBodyPartActive(part) == expected.GetHumanoidBodyPartActive(part);
            Object.DestroyImmediate(expected);
            if (!valid) { issues++; Debug.LogWarning($"Punch region {region} has a missing or overlapping mask.", study); }
        }
        for (int hand = 0; hand < 2; hand++)
        foreach (Boxing.PunchType type in new[] { Boxing.PunchType.Jab, Boxing.PunchType.Hook, Boxing.PunchType.Uppercut })
        foreach (Boxing.PunchTarget target in new[] { Boxing.PunchTarget.Head, Boxing.PunchTarget.Body })
        {
            if (type == Boxing.PunchType.Jab && target == Boxing.PunchTarget.Head) continue;
            if ((study.Punches ?? new AimStudyBoxer.PunchMotion[0]).Any(m => m != null && m.Clip != null && (int)m.Hand == hand && m.Type == type && m.Target == target)) continue;
            issues++;
            Debug.LogWarning($"Missing punch: {(Boxing.Hand)hand} {type} {target}.", study);
        }
        if (issues == 0) Debug.Log("Aim Study validation passed: effective jab clips, punch coverage, both guards, matched/exhausted timing, footstep wiring, and three non-overlapping region masks.", study);
        else Debug.LogWarning($"Aim Study validation found {issues} issue(s).", study);
    }

    private static void Warn(AimStudyBoxer s)
    {
        if (s.JabClip(0) == null || s.JabClip(1) == null)
            Debug.LogWarning("Aim study boxer: a jab clip was not found — check the Punches/Jab folder.", s);

        if (s.HighEnergy == null || s.MidEnergy == null || s.LowEnergy == null)
            Debug.LogWarning("Aim study boxer: a stance idle was not found in " + IdleFolder + ".", s);
    }

    private static string Name(Object o) => o != null ? o.name : "<missing>";

    /// <summary>
    /// The named jab if it is still there, otherwise the best clip carrying the right tag.
    /// </summary>
    private static AnimationClip Jab(string exact, string tag)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{PunchFolder}/Jab/{exact}.anim");
        if (clip != null)
            return clip;

        // Straight head shots only — a lunge or a body jab is a different move.
        AnimationClip best = null;
        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { PunchFolder }))
        {
            var c = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(guid));
            if (c == null || !c.name.Contains(tag))
                continue;

            string low = c.name.ToLowerInvariant();
            if (low.Contains("body") || low.Contains("lunge") || low.Contains("block")
                || low.Contains("lowblow") || low.Contains("dash"))
                continue;

            if (best == null || c.name.Contains("Power"))
                best = c;
        }

        if (best != null)
        {
            Debug.LogWarning($"Aim study boxer: '{exact}' is gone, so '{best.name}' was used for " +
                $"the {tag} slot instead. Assign the one you want and re-run — your choice is kept.");
        }
        else
        {
            Debug.LogError($"Aim study boxer: no {tag} clip found at all. That hand will not punch.");
        }

        return best;
    }

    /// <summary>The matching Undisputed idle, falling back to the old Refrences one.</summary>
    private static AnimationClip Idle(string undisputed, string fallback)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{IdleFolder}/{undisputed}.anim");
        if (clip != null)
            return clip;

        Debug.LogWarning($"Aim study boxer: '{undisputed}' not found — falling back to " +
            $"{fallback}, which is from a different capture and will not match the jabs.");
        return FindClip(FallbackIdleFolder, fallback);
    }

    /************************************************************************************/

    /// <summary>
    /// A visible thing straight in front to aim at, at head height.
    /// </summary>
    /// <remarks>
    /// With no target the aim falls back to a computed point ahead, which is fine — but having
    /// a real object means a centred stick aims at something you can see and move, instead of
    /// the aim quietly drifting to wherever the mouse was parked.
    /// </remarks>
    private static Transform FindOrCreateTarget(Transform boxer, AimStudyBoxer study)
    {
        GameObject existing = GameObject.Find("AimStudy Target");
        if (existing != null)
            return existing.transform;

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "AimStudy Target";
        go.transform.localScale = Vector3.one * 0.14f;
        go.transform.position = boxer.position
                              + boxer.forward * study.AimDistance
                              + Vector3.up * study.HeadHeight;

        Collider col = go.GetComponent<Collider>();
        if (col != null)
            Object.DestroyImmediate(col);

        return go.transform;
    }

    /// <summary>Copies the tuned values off the old rig onto the new one.</summary>
    private static void Restore(AimStudyBoxer from, AimStudyBoxer to)
    {
        to.Stance = from.Stance;
        to.PunchSpeed = from.PunchSpeed;
        to.PunchFade = from.PunchFade;
        to.RecoverFade = from.RecoverFade;

        to.AimDistance = from.AimDistance;
        to.AimRadius = from.AimRadius;
        to.HeadHeight = from.HeadHeight;
        to.BodyDrop = from.BodyDrop;
        to.AimSmoothing = from.AimSmoothing;

        // Your clip choices win over the defaults — swapping in a mirrored jab is the whole
        // point, and a rebuild used to quietly put the original back.
        if (from.LeftJab != null) to.LeftJab = from.LeftJab;
        if (from.RightJab != null) to.RightJab = from.RightJab;
        to.LeftTriggerJab = from.LeftTriggerJab;
        to.RightTriggerJab = from.RightTriggerJab;

        to.BodyFollow = from.BodyFollow;
        to.OffHandFollow = from.OffHandFollow;
        to.LeftJabHeight = from.LeftJabHeight;
        to.RightJabHeight = from.RightJabHeight;
        to.ShoulderShare = from.ShoulderShare;

        to.Target = from.Target;
        to.UseMouseAim = from.UseMouseAim;
        to.ForeArmWeight = from.ForeArmWeight;
        to.UpperArmWeight = from.UpperArmWeight;
        to.IncludeSpine = from.IncludeSpine;
        to.IncludeChest = from.IncludeChest;
        to.IncludeUpperChest = from.IncludeUpperChest;
        to.IncludeUpperArm = from.IncludeUpperArm;
        to.IncludeForeArm = from.IncludeForeArm;
        to.TorsoWeight = from.TorsoWeight;

        to.AimWeight = from.AimWeight;
        to.Iterations = from.Iterations;
        to.Tolerance = from.Tolerance;
        to.ClampWeight = from.ClampWeight;
        to.DriveSolverSettings = from.DriveSolverSettings;
        to.AimWindow = from.AimWindow;
        to.HoldAfterStrike = from.HoldAfterStrike;

        to.MoveSpeed = from.MoveSpeed;
        to.Acceleration = from.Acceleration;
        to.TurnSpeed = from.TurnSpeed;

        to.BuildChains();   // the chain toggles may have changed
    }

    /************************************************************************************/

    /// <summary>Sweeps the clip for peak arm extension — that frame is the strike.</summary>
    private static float MeasureStrike(Animator animator, AnimationClip clip, bool left)
    {
        Transform upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        Transform fore = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        Transform hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);

        if (upper == null || fore == null || hand == null)
            return 0.4f;

        float length = Mathf.Max(0.01f, clip.length);
        int steps = Mathf.Clamp(Mathf.RoundToInt(length * 60f), 12, 240);
        float bestT = 0.4f, bestExt = -1f;

        AnimationMode.StartAnimationMode();
        try
        {
            for (int i = 0; i <= steps; i++)
            {
                float t01 = i / (float)steps;

                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(animator.gameObject, clip, t01 * length);
                AnimationMode.EndSampling();

                float arm = Vector3.Distance(upper.position, fore.position)
                          + Vector3.Distance(fore.position, hand.position);
                float ext = arm > 0.01f
                    ? Mathf.Clamp01(Vector3.Distance(upper.position, hand.position) / arm)
                    : 0f;

                if (ext > bestExt)
                {
                    bestExt = ext;
                    bestT = t01;
                }
            }
        }
        finally
        {
            AnimationMode.StopAnimationMode();
        }

        return bestT;
    }

    private static AnimationClip FindClip(string folder, string nameContains)
    {
        if (!AssetDatabase.IsValidFolder(folder))
            return null;

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.IndexOf(nameContains, System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip != null && !clip.name.StartsWith("__preview"))
                return clip;
        }

        return null;
    }

    /// <summary>
    /// A mask with exactly one region enabled, so that region can be given its own layer weight.
    /// </summary>
    /// <remarks>
    /// Root is off on every one of them. It is the bit that would let a punch layer move the
    /// character, and the stance owns position.
    /// </remarks>
    private static AvatarMask RegionMask(string name, params AvatarMaskBodyPart[] parts)
    {
        string path = $"Assets/UndisputedTest/Boxing/Punch{name}.mask";

        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
        if (mask == null)
        {
            mask = new AvatarMask();
            AssetDatabase.CreateAsset(mask, path);
        }

        for (AvatarMaskBodyPart bp = 0; bp < AvatarMaskBodyPart.LastBodyPart; bp++)
            mask.SetHumanoidBodyPartActive(bp, false);

        foreach (AvatarMaskBodyPart bp in parts)
            mask.SetHumanoidBodyPartActive(bp, true);

        EditorUtility.SetDirty(mask);
        AssetDatabase.SaveAssets();
        return mask;
    }

}
