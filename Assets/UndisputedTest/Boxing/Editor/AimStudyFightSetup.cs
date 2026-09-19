using System.Collections.Generic;
using System.Linq;
using Animancer;
using FIMSpace.FProceduralAnimation;
using RootMotion.Dynamics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Turns the selected aim-study boxer into a two-man bout: clones him into an AI opponent, gives
/// both a PuppetMaster physics skeleton, a Legs Animator for feet, health zones, hit reactions
/// and the combat glue — all Undo-able, nothing saved.
///
/// The puppet is a TRANSFORM-ONLY copy of the skeleton: no components, no renderers, no global
/// collision or timestep changes. Standing, it maps only head/torso/arms and pins the legs under
/// the animation; on a knockdown the mapping goes full and the ragdoll takes over.
/// </summary>
public static class AimStudyFightSetup
{
    private const string ReactionFolder = "Assets/Animations/Undisputed/Orthodox/HitReaction";
    private const string DazedClip = "Assets/Animations/Undisputed/Orthodox/Stunned/SunnyEdwards_Idle_Traditional_Dazed_Orthodox.anim";

    [MenuItem("Tools/Undisputed/Boxing/Setup Fight With Selected Aim Study Boxer")]
    public static void SetupFight()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("Stop Play mode before setting up the fight.");
            return;
        }

        AimStudyBoxer player = SelectedStudy();
        if (player == null) return;
        Animator playerAnimator = player.GetComponent<AnimancerComponent>()?.Animator
            ?? player.GetComponent<Animator>();
        if (playerAnimator == null || !playerAnimator.isHuman || playerAnimator.gameObject != player.gameObject)
        {
            Debug.LogError("Fight setup: the study needs a Humanoid Animator on the same object.", player);
            return;
        }
        Vector3 playerScale = player.transform.lossyScale;
        if (playerScale.x <= 0f || playerScale.y <= 0f || playerScale.z <= 0f
            || Mathf.Abs(playerScale.x - playerScale.y) > 0.001f
            || Mathf.Abs(playerScale.y - playerScale.z) > 0.001f)
        {
            Debug.LogError("Fight setup: the boxer needs a positive uniform scale — the ragdoll " +
                "mirror cannot reproduce a stretched or negative skeleton.", player);
            return;
        }
        if (!RejectLegacyDrivers(player)) return;
        if (!HasFloor(player.gameObject.scene, player.transform.position.y))
        {
            Debug.LogError("Fight setup: no floor-like collider found in this scene — the ragdoll " +
                "would fall forever. Add a ground collider first.", player);
            return;
        }

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Setup Aim Study Fight");

        AimStudyBoxerSetup.UpgradeSelected();

        AimStudyBoxer opponent = player.Opponent;
        if (opponent != null && (opponent == player || opponent.GetComponent<Animator>() == null
                || !opponent.GetComponent<Animator>().isHuman))
            opponent = null;
        if (opponent != null && EditorUtility.IsPersistent(opponent)) opponent = null;

        if (opponent == null)
        {
            GameObject clone = Object.Instantiate(player.gameObject);
            Undo.RegisterCreatedObjectUndo(clone, "Setup Aim Study Fight");
            clone.name = "AimStudy Opponent";
            float scale = Mathf.Max(0.1f, player.transform.lossyScale.y);
            clone.transform.SetPositionAndRotation(
                player.transform.position + player.transform.forward * (2.1f * scale),
                Quaternion.LookRotation(-player.transform.forward, Vector3.up));
            if (clone.scene != player.gameObject.scene)
                SceneManager.MoveGameObjectToScene(clone, player.gameObject.scene);
            opponent = clone.GetComponent<AimStudyBoxer>();
            DisableClonedSpectators(clone);
        }
        else
        {
            Debug.Log($"Fight setup: reusing assigned opponent '{opponent.name}'.", opponent);
        }

        Undo.RecordObject(player, "Setup Aim Study Fight");
        Undo.RecordObject(opponent, "Setup Aim Study Fight");
        player.Opponent = opponent;
        opponent.Opponent = player;

        ConfigureFighter(player, false);
        ConfigureFighter(opponent, true);

        if (Camera.main != null)
        {
            CameraFollow follow = Camera.main.GetComponent<CameraFollow>();
            if (follow == null) follow = Undo.AddComponent<CameraFollow>(Camera.main.gameObject);
            var settings = new SerializedObject(follow);
            settings.FindProperty("target").objectReferenceValue = player.transform;
            settings.FindProperty("opponent").objectReferenceValue = opponent.transform;
            settings.ApplyModifiedProperties();
            EditorUtility.SetDirty(follow);
        }
        else Debug.LogWarning("Fight setup: no Main Camera found — the fight camera was not wired.");

        EditorUtility.SetDirty(player);
        EditorUtility.SetDirty(opponent);
        EditorSceneManager.MarkSceneDirty(player.gameObject.scene);
        EditorSceneManager.MarkSceneDirty(opponent.gameObject.scene);
        Undo.CollapseUndoOperations(group);

        Debug.Log($"Fight ready: '{player.name}' vs '{opponent.name}'.\n" +
            "   Both got health zones, glove sensors, hit reactions, a Legs Animator on idle glue, " +
            "and a PuppetMaster ragdoll that only owns the body fully on a knockdown.\n" +
            "   The scene is dirty but NOT saved — press Play to try the bout, and run " +
            "'Validate Selected Aim Study Fight' if anything is off.", player);
        ValidateFight(player, opponent);
    }

    /// <summary>Cameras and listeners cloned along for the ride must not double up.</summary>
    private static void DisableClonedSpectators(GameObject clone)
    {
        foreach (Component component in clone.GetComponentsInChildren<Component>(true))
        {
            if (component == null) continue;
            bool spectator = component is Camera || component is AudioListener || component is CameraFollow
                || component is ImpactFeedback || component is OpponentSpawner;
            if (!spectator || !(component is Behaviour behaviour) || !behaviour.enabled) continue;
            Undo.RecordObject(behaviour, "Setup Aim Study Fight");
            behaviour.enabled = false;
        }
    }

    /// <summary>One animation/physics owner per fighter — refuse to stack on the old systems.</summary>
    private static bool RejectLegacyDrivers(AimStudyBoxer study)
    {
        foreach (Component component in study.GetComponentsInChildren<Component>(true))
        {
            if (component == null || !(component is Behaviour behaviour) || !behaviour.isActiveAndEnabled)
                continue;
            string type = component.GetType().Name;
            bool legacy = component is Controller || component is BoxerPunchController
                || component is BoxerPhysics || component is BoxerPhysicsBody || component is BoxerPuppet
                || type.Contains("RagdollAnimator") || type.Contains("RagdollHandler");
            if (!legacy) continue;
            Debug.LogError($"Fight setup: '{study.name}' still has an active {type} — the study rig " +
                "needs exactly one animation/physics owner. Remove or disable it yourself; setup " +
                "will not touch existing systems.", study);
            return false;
        }
        return true;
    }

    /// <summary>A wide, low, non-trigger collider the ragdolls can actually land on.</summary>
    private static bool HasFloor(Scene scene, float feetY)
    {
        foreach (Collider c in Object.FindObjectsByType<Collider>())
        {
            if (c == null || !c.enabled || c.isTrigger || c.gameObject.scene != scene) continue;
            if (c.GetComponentInParent<AimStudyBoxer>() != null
                || c.GetComponentInParent<PunchHitbox>() != null
                || c.GetComponentInParent<BoxerHitZone>() != null
                || c.GetComponentInParent<PuppetMaster>() != null) continue;
            Bounds b = c.bounds;
            if (b.max.y <= feetY + 0.5f && Mathf.Max(b.extents.x, b.extents.z) >= 0.5f) return true;
        }
        return false;
    }

    private static void ConfigureFighter(AimStudyBoxer study, bool ai)
    {
        GameObject host = study.gameObject;
        Animator animator = study.GetComponent<Animator>();
        float scale = Mathf.Max(0.1f, study.transform.lossyScale.y);

        Undo.RecordObject(animator, "Setup Aim Study Fight");
        animator.applyRootMotion = false;
        animator.updateMode = AnimatorUpdateMode.Normal;

        BoxerHealth health = EnsureComponent<BoxerHealth>(host);
        var healthSettings = new SerializedObject(health);
        healthSettings.FindProperty("knockdownsToLose").intValue = 1;
        healthSettings.ApplyModifiedProperties();
        EditorUtility.SetDirty(health);

        CharacterController capsule = host.GetComponent<CharacterController>();
        if (capsule == null)
        {
            capsule = Undo.AddComponent<CharacterController>(host);
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            float height = head != null
                ? (head.position.y - study.transform.position.y) / scale + 0.2f : 1.8f;
            capsule.height = height;
            capsule.radius = Mathf.Min(0.25f, height * 0.16f);
            capsule.center = Vector3.up * (height * 0.5f);
            capsule.stepOffset = 0.2f;
            capsule.skinWidth = 0.025f;
        }

        AimStudyCombat combat = EnsureComponent<AimStudyCombat>(host);
        Undo.RecordObject(combat, "Setup Aim Study Fight");
        if (Empty(combat.HeadReactions))
            combat.HeadReactions = new[]
            {
                Clip($"{ReactionFolder}/Andy_HitReaction_Head_Center_INT_01_Orthodox.anim"),
                Clip($"{ReactionFolder}/Andy_HitReaction_Head_Left_INT_01_Orthodox.anim"),
                Clip($"{ReactionFolder}/Andy_HitReaction_Head_Right_INT_01_Orthodox.anim"),
            };
        if (Empty(combat.BodyReactions))
            combat.BodyReactions = new[]
            {
                Clip($"{ReactionFolder}/Andy_HitReaction_Body_Center_INT_01_Orthodox.anim"),
                Clip($"{ReactionFolder}/Andy_HitReaction_Body_Left_INT_01_Orthodox.anim"),
                Clip($"{ReactionFolder}/Andy_HitReaction_Body_Right_INT_01_Orthodox.anim"),
            };
        if (Empty(combat.BlockReactions))
            combat.BlockReactions = new[]
            {
                Clip($"{ReactionFolder}/Animated_Block_Center_OptimizedForBlock_Reaction_Orthodox_MxM_MOD.anim"),
            };
        if (combat.DazedIdle == null) combat.DazedIdle = Clip(DazedClip);
        EditorUtility.SetDirty(combat);

        LegsAnimator legs = study.GetComponentInChildren<LegsAnimator>(true);
        if (legs == null) legs = Undo.AddComponent<LegsAnimator>(host);
        WireLegs(study, animator, legs);

        AimStudyPhysics physics = EnsureComponent<AimStudyPhysics>(host);
        Undo.RecordObject(physics, "Setup Aim Study Fight");
        physics.Legs = legs;
        if (ai) physics.Puppet = null;   // a cloned reference would point at the OTHER fighter's puppet
        physics.Puppet = EnsurePuppet(study, animator, scale) ?? physics.Puppet;
        EditorUtility.SetDirty(physics);

        if (ai)
        {
            EnsureComponent<AimStudyAI>(host);
            study.ShowPanel = false;
            study.DrawGizmos = false;
            study.FeedbackCamera = null;
        }
        else if (study.GetComponent<AimStudyAI>() != null)
        {
            Debug.LogWarning($"Fight setup: '{study.name}' has an AimStudyAI — the human's boxer " +
                "should not be AI-driven. Remove it if this fighter is meant to be the player.", study);
        }
    }

    private static void WireLegs(AimStudyBoxer study, Animator animator, LegsAnimator legs)
    {
        Undo.RecordObject(legs, "Setup Aim Study Fight");
        if (legs.Mecanim == null) legs.Mecanim = animator;
        if (legs.Hips == null) legs.Hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (legs.SpineBone == null) legs.SpineBone = animator.GetBoneTransform(HumanBodyBones.Spine);
        if (legs.Legs == null || legs.Legs.Count == 0)
        {
            legs.Initialize_BaseTransform(study.transform);
            legs.Finder_AutoFindLegsIfHuman(animator);
            legs.Finder_AutoDefineOppositeLegs();
        }
        legs.GlueMode = LegsAnimator.EGlueMode.Idle;
        legs.UseGluing = true;
        legs.GlueOnlyOnIdle = false;
        legs.MainGlueBlend = 1f;
        legs.MovingParameter = "";
        legs.GroundedParameter = "";
        legs.RagdolledParameter = "";
        legs.UseRigidbodyVelocityForIsMoving = false;
        EditorUtility.SetDirty(legs);
    }

    /// <summary>
    /// Adopts a scene puppet already following this fighter, or builds a new transform-only
    /// physics skeleton and wires a PuppetMaster on it. The ORIGINAL hierarchy is never touched.
    /// </summary>
    private static PuppetMaster EnsurePuppet(AimStudyBoxer study, Animator animator, float scale)
    {
        foreach (PuppetMaster pm in Object.FindObjectsByType<PuppetMaster>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (pm == null || pm.targetRoot != study.transform) continue;
            bool driven = pm.behaviours != null && pm.behaviours.Any(b => b != null && b.isActiveAndEnabled);
            if (driven)
            {
                Debug.LogError($"Fight setup: '{study.name}' already has a PuppetMaster driven by " +
                    "behaviours — refusing to stack another muscle driver on it.", pm);
                return null;
            }
            Undo.RecordObject(pm, "Setup Aim Study Fight");
            TunePuppet(pm, scale);
            EditorUtility.SetDirty(pm);
            return pm;
        }

        var map = new Dictionary<Transform, Transform>();
        var physicsRoot = new GameObject(study.name + " Physics");
        Undo.RegisterCreatedObjectUndo(physicsRoot, "Setup Aim Study Fight");
        physicsRoot.transform.SetPositionAndRotation(study.transform.position, study.transform.rotation);
        physicsRoot.transform.localScale = study.transform.lossyScale;
        physicsRoot.layer = 2;
        if (physicsRoot.scene != study.gameObject.scene)
            SceneManager.MoveGameObjectToScene(physicsRoot, study.gameObject.scene);
        map.Add(study.transform, physicsRoot.transform);
        foreach (Transform child in study.transform) CopySkeleton(child, physicsRoot.transform, map);

        Transform Bone(HumanBodyBones bone)
        {
            Transform t = animator.GetBoneTransform(bone);
            return t != null && map.TryGetValue(t, out Transform copy) ? copy : null;
        }
        var r = new BipedRagdollReferences
        {
            root = physicsRoot.transform,
            hips = Bone(HumanBodyBones.Hips),
            spine = Bone(HumanBodyBones.Spine),
            chest = Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest),
            head = Bone(HumanBodyBones.Head),
            leftUpperArm = Bone(HumanBodyBones.LeftUpperArm),
            leftLowerArm = Bone(HumanBodyBones.LeftLowerArm),
            leftHand = Bone(HumanBodyBones.LeftHand),
            rightUpperArm = Bone(HumanBodyBones.RightUpperArm),
            rightLowerArm = Bone(HumanBodyBones.RightLowerArm),
            rightHand = Bone(HumanBodyBones.RightHand),
            leftUpperLeg = Bone(HumanBodyBones.LeftUpperLeg),
            leftLowerLeg = Bone(HumanBodyBones.LeftLowerLeg),
            leftFoot = Bone(HumanBodyBones.LeftFoot),
            rightUpperLeg = Bone(HumanBodyBones.RightUpperLeg),
            rightLowerLeg = Bone(HumanBodyBones.RightLowerLeg),
            rightFoot = Bone(HumanBodyBones.RightFoot),
        };
        string msg = null;
        if (!r.IsValid(ref msg))
        {
            Debug.LogError($"Fight setup: cannot build a ragdoll for '{study.name}' — {msg}", study);
            Undo.DestroyObjectImmediate(physicsRoot);
            return null;
        }

        var options = BipedRagdollCreator.AutodetectOptions(r);
        options.joints = RagdollCreator.JointType.Configurable;
        options.hands = true;
        options.feet = true;
        options.weight = 75f;
        options.spine = r.spine != null;
        options.chest = r.chest != null;
        BipedRagdollCreator.Create(r, options);

        if (r.hips.GetComponent<ConfigurableJoint>() == null)
            Undo.AddComponent<ConfigurableJoint>(r.hips.gameObject);

        foreach (Transform t in physicsRoot.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = 2;
        foreach (Rigidbody body in physicsRoot.GetComponentsInChildren<Rigidbody>(true))
        {
            Undo.RecordObject(body, "Setup Aim Study Fight");
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.maxAngularVelocity = 20f;
        }

        var reverse = new Dictionary<Transform, Transform>();
        foreach (KeyValuePair<Transform, Transform> pair in map) reverse[pair.Value] = pair.Key;
        ConfigurableJoint[] joints = physicsRoot.GetComponentsInChildren<ConfigurableJoint>(true)
            .OrderBy(j => j.transform == r.hips ? 0 : 1).ToArray();
        var muscles = new List<Muscle>();
        foreach (ConfigurableJoint joint in joints)
        {
            if (joint == null || !reverse.TryGetValue(joint.transform, out Transform target) || target == null)
                continue;
            muscles.Add(new Muscle
            {
                name = joint.name,
                joint = joint,
                target = target,
                props = new Muscle.Props(1f, 1f, 1f, 1f, GroupOf(joint.transform, r)),
            });
        }
        if (muscles.Count < 10)
        {
            Debug.LogError($"Fight setup: only {muscles.Count} muscles mapped for '{study.name}' — " +
                "the ragdoll needs at least the 15 biped bones.", study);
            Undo.DestroyObjectImmediate(physicsRoot);
            return null;
        }

        PuppetMaster puppet = Undo.AddComponent<PuppetMaster>(physicsRoot);
        puppet.targetRoot = study.transform;
        puppet.muscles = muscles.ToArray();
        TunePuppet(puppet, scale);
        EditorUtility.SetDirty(puppet);
        EditorSceneManager.MarkSceneDirty(physicsRoot.scene);
        return puppet;
    }

    private static Transform CopySkeleton(Transform source, Transform parent, Dictionary<Transform, Transform> map)
    {
        var copy = new GameObject(source.name).transform;
        copy.SetParent(parent, false);
        copy.localPosition = source.localPosition;
        copy.localRotation = source.localRotation;
        copy.localScale = source.localScale;
        copy.gameObject.layer = 2;
        map.Add(source, copy);
        foreach (Transform child in source) CopySkeleton(child, copy, map);
        return copy;
    }

    private static Muscle.Group GroupOf(Transform t, BipedRagdollReferences r)
    {
        if (t == r.hips) return Muscle.Group.Hips;
        if (t == r.head) return Muscle.Group.Head;
        if (t == r.spine || t == r.chest) return Muscle.Group.Spine;
        if (t == r.leftHand || t == r.rightHand) return Muscle.Group.Hand;
        if (t == r.leftUpperLeg || t == r.leftLowerLeg || t == r.rightUpperLeg || t == r.rightLowerLeg)
            return Muscle.Group.Leg;
        if (t == r.leftFoot || t == r.rightFoot) return Muscle.Group.Foot;
        return Muscle.Group.Arm;
    }

    private static void TunePuppet(PuppetMaster pm, float scale)
    {
        pm.mode = PuppetMaster.Mode.Active;
        pm.pinWeight = 1f;
        pm.muscleWeight = 1f;
        pm.mappingWeight = 1f;
        pm.muscleSpring = 500f * scale * scale;
        pm.muscleDamper = 15f * scale;
        pm.pinPow = 2f;
        pm.pinDistanceFalloff = 0.5f;
        pm.solverIterationCount = 12;
        pm.angularPinning = true;
        pm.angularLimits = true;
        pm.internalCollisions = false;
        pm.fixTargetTransforms = true;
        pm.updateJointAnchors = true;
    }

    private static bool Empty(AnimationClip[] clips) => clips == null || clips.Length == 0 || clips.All(c => c == null);
    private static AnimationClip Clip(string path) => AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
    private static T EnsureComponent<T>(GameObject host) where T : Component
        => host.GetComponent<T>() ?? Undo.AddComponent<T>(host);

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
        Debug.LogWarning("Select the Aim Study boxer to build a fight around; no unambiguous rig was found.");
        return null;
    }

    /************************************************************************************/

    [MenuItem("Tools/Undisputed/Boxing/Validate Selected Aim Study Fight")]
    public static void ValidateSelectedFight()
    {
        AimStudyBoxer player = SelectedStudy();
        if (player != null) ValidateFight(player, player.Opponent);
    }

    private static void ValidateFight(AimStudyBoxer player, AimStudyBoxer opponent)
    {
        int issues = 0;
        void Issue(string message, Object context)
        {
            issues++;
            Debug.LogWarning("Fight validation: " + message, context);
        }

        if (opponent == null) { Issue("no opponent assigned — run the fight setup menu.", player); return; }
        if (opponent.Opponent != player) Issue("the opponent does not point back at the player.", opponent);
        if (player.GetComponent<AimStudyAI>() != null) Issue("the player has an AimStudyAI — AI belongs on the opponent.", player);
        if (opponent.GetComponent<AimStudyAI>() == null) Issue("the opponent has no AimStudyAI.", opponent);
        if (!RejectLegacyDrivers(player)) issues++;
        if (!RejectLegacyDrivers(opponent)) issues++;

        foreach (AimStudyBoxer fighter in new[] { player, opponent })
        {
            string who = fighter.name;
            Animator animator = fighter.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) { Issue($"{who}: no Humanoid Animator.", fighter); continue; }
            if (animator.updateMode != AnimatorUpdateMode.Normal)
                Issue($"{who}: Animator update mode must stay Normal for PuppetMaster.", fighter);
            if (animator.applyRootMotion)
                Issue($"{who}: applyRootMotion must stay off — the script moves the transform.", fighter);
            if (fighter.JabClip(0) == null || fighter.JabClip(1) == null)
                Issue($"{who}: a jab clip is missing.", fighter);
            if (fighter.HeadDefense == null || fighter.BodyDefense == null)
                Issue($"{who}: head/body guard clips are missing.", fighter);
            if (fighter.TorsoMask == null || fighter.LeftArmMask == null || fighter.RightArmMask == null)
                Issue($"{who}: a punch region mask is missing.", fighter);

            BoxerHealth health = fighter.GetComponent<BoxerHealth>();
            if (health == null) Issue($"{who}: no BoxerHealth — glove sensors have nothing to damage.", fighter);
            CharacterController capsule = fighter.GetComponent<CharacterController>();
            if (capsule == null || capsule.height <= 0.01f)
                Issue($"{who}: no usable CharacterController capsule.", fighter);
            if (fighter.GetComponent<AimStudyCombat>() == null)
                Issue($"{who}: no AimStudyCombat — no reactions, stun or reset.", fighter);

            LegsAnimator legs = fighter.GetComponentInChildren<LegsAnimator>(true);
            if (legs == null || legs.Hips == null || legs.Legs == null || legs.Legs.Count < 2)
                Issue($"{who}: Legs Animator is missing or has no legs — feet will not plant.", fighter);

            AimStudyPhysics physics = fighter.GetComponent<AimStudyPhysics>();
            PuppetMaster puppet = physics != null ? physics.Puppet : null;
            if (physics == null || puppet == null)
            {
                Issue($"{who}: no physics bridge / puppet — knockdowns cannot happen.", fighter);
                continue;
            }
            if (puppet.targetRoot != fighter.transform)
                Issue($"{who}: puppet follows a different target root.", puppet);
            if (puppet.muscles == null || puppet.muscles.Length < 10)
                Issue($"{who}: puppet has {puppet.muscles?.Length ?? 0} muscles, expected at least 10.", puppet);
            else
            {
                int hands = puppet.muscles.Count(m => m != null && m.props != null && m.props.group == Muscle.Group.Hand);
                if (hands < 2) Issue($"{who}: puppet needs a muscle on each hand.", puppet);
                foreach (Muscle m in puppet.muscles)
                {
                    if (m == null || m.joint == null || m.target == null)
                    { Issue($"{who}: a muscle has a null joint or target.", puppet); break; }
                    if (!m.target.IsChildOf(fighter.transform))
                    { Issue($"{who}: muscle '{m.name}' targets a bone outside this fighter.", puppet); break; }
                }
            }
        }

        AimStudyPhysics playerPhysics = player.GetComponent<AimStudyPhysics>();
        AimStudyPhysics opponentPhysics = opponent.GetComponent<AimStudyPhysics>();
        if (playerPhysics != null && opponentPhysics != null
            && playerPhysics.Puppet != null && playerPhysics.Puppet == opponentPhysics.Puppet)
            Issue("both fighters share one PuppetMaster — each needs his own.", player);

        if (!HasFloor(player.gameObject.scene, player.transform.position.y))
            Issue("no floor collider found for the ragdolls to land on.", player);
        if (Physics.GetIgnoreLayerCollision(2, 0))
            Issue("layer 2 (the ragdoll) is set to ignore the default layer — the puppet would fall through the floor.", player);

        if (issues == 0)
            Debug.Log($"Fight validation passed for '{player.name}' vs '{opponent.name}': reciprocal " +
                "opponents, AI on the opponent only, health/reactions/legs/puppets wired on both, " +
                "and a floor the ragdolls can land on. Configuration only — actual feel still needs a Play test.", player);
        else
            Debug.LogWarning($"Fight validation found {issues} issue(s).", player);
    }
}
