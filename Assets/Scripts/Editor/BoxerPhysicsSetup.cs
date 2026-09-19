using System.Collections.Generic;
using System.Text;
using RootMotion.Dynamics;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click bring-up for the physics boxer (req.md). Turning <see cref="BoxerPhysics"/> on is only half the
/// job — PuppetMaster has to be in Active mode, the Animator has to be on Animate Physics so the puppet owns
/// Animator → IK → Read as one step, the old self-built puppet and Ragdoll Animator have to stand down, and the
/// physics rate has to come up. This does all of it, and the validator explains anything it cannot fix itself.
///
/// Tools ▸ Boxer ▸ Physics ▸ …
/// </summary>
public static class BoxerPhysicsSetup
{
    private const string SetupMenu = "Tools/Boxer/Physics/Setup Physics Boxer";
    private const string ValidateMenu = "Tools/Boxer/Physics/Validate Physics Boxer";
    private const string OffMenu = "Tools/Boxer/Physics/Turn Physics Off (back to IK)";

    /// <summary>Physics steps per second the setup asks for — boxing wants 100+ (req.md §17).</summary>
    private const int PhysicsRate = 100;

    [MenuItem(SetupMenu, true)]
    [MenuItem(ValidateMenu, true)]
    [MenuItem(OffMenu, true)]
    private static bool Validate() => FindBoxer() != null;

    // ------------------------------------------------------------------ Setup

    [MenuItem(SetupMenu)]
    private static void Setup()
    {
        BoxerPunchController boxer = FindBoxer();
        if (boxer == null) return;

        List<string> did = new List<string>();
        Undo.SetCurrentGroupName("Setup Physics Boxer");
        int group = Undo.GetCurrentGroup();

        // --- the body itself ---
        BoxerPhysics physics = boxer.GetComponent<BoxerPhysics>();
        if (physics == null)
        {
            physics = Undo.AddComponent<BoxerPhysics>(boxer.gameObject);
            did.Add("added BoxerPhysics");
        }

        // Re-tune the BoxerPhysics component itself. Changing a C# default does NOTHING to a component already
        // in the scene — its old values are serialised — so the new tuning has to be written in explicitly.
        TuneBody(physics, boxer.transform.lossyScale.y, did);

        SerializedObject so = new SerializedObject(boxer);
        SerializedProperty use = so.FindProperty("usePhysicsBody");
        SerializedProperty backend = so.FindProperty("physicsBackend");
        SerializedProperty puppetRef = so.FindProperty("puppetPhysics");
        if (use != null && !use.boolValue) { use.boolValue = true; did.Add("Use Physics Body = on"); }
        if (backend != null && backend.enumValueIndex != (int)BoxerPunchController.PhysicsBackend.PuppetMaster)
        {
            backend.enumValueIndex = (int)BoxerPunchController.PhysicsBackend.PuppetMaster;
            did.Add("backend = Puppet Master");
        }
        if (puppetRef != null && puppetRef.objectReferenceValue != physics) puppetRef.objectReferenceValue = physics;
        so.ApplyModifiedProperties();

        // --- PuppetMaster: Active, and strong enough for this character's scale ---
        PuppetMaster puppet = FindPuppet(boxer.transform);
        if (puppet != null)
        {
            Undo.RecordObject(puppet, "Setup Physics Boxer");
            if (puppet.mode != PuppetMaster.Mode.Active) { puppet.mode = PuppetMaster.Mode.Active; did.Add("PuppetMaster mode = Active"); }
            if (puppet.muscleSpring < 300f) { puppet.muscleSpring = 400f; did.Add("muscle spring → 400 (100 sags at this scale)"); }
            if (puppet.muscleDamper < 1f) { puppet.muscleDamper = 4f; did.Add("muscle damper → 4"); }
            if (puppet.pinDistanceFalloff > 1f) { puppet.pinDistanceFalloff = 0.5f; did.Add("pin distance falloff → 0.5 (punches stay on target)"); }
            if (puppet.solverIterationCount < 12) { puppet.solverIterationCount = 12; did.Add("solver iterations → 12"); }
            if (!puppet.angularPinning) { puppet.angularPinning = true; did.Add("angular pinning on"); }
            if (!puppet.updateJointAnchors) { puppet.updateJointAnchors = true; did.Add("update joint anchors on (animated bones between muscles)"); }
            if (puppet.angularLimits) { puppet.angularLimits = false; did.Add("angular limits OFF — this rig's authored limits jam the punch poses (uppercut stuck sideways)"); }
            EditorUtility.SetDirty(puppet);
        }

        // --- the Animator has to run in FixedUpdate so PuppetMaster can own the whole chain ---
        Animator animator = boxer.GetComponent<Animator>();
        if (animator != null && animator.updateMode != AnimatorUpdateMode.Fixed)
        {
            Undo.RecordObject(animator, "Setup Physics Boxer");
            animator.updateMode = AnimatorUpdateMode.Fixed;
            did.Add("Animator update mode = Animate Physics");
            EditorUtility.SetDirty(animator);
        }

        // --- nothing else may drive the same bones ---
        BoxerPhysicsBody legacy = boxer.GetComponent<BoxerPhysicsBody>();
        if (legacy != null && legacy.enabled)
        {
            Undo.RecordObject(legacy, "Setup Physics Boxer");
            legacy.enabled = false;
            did.Add("disabled the old self-built BoxerPhysicsBody (two puppets would fight)");
            EditorUtility.SetDirty(legacy);
        }
        foreach (MonoBehaviour mb in boxer.GetComponents<MonoBehaviour>())
        {
            if (mb == null || !mb.enabled) continue;
            string type = mb.GetType().Name;
            if (type != "RagdollAnimator2" && type != "RagdollAnimator") continue;
            Undo.RecordObject(mb, "Setup Physics Boxer");
            mb.enabled = false;
            did.Add($"disabled {type} — PuppetMaster owns the physical body now");
            EditorUtility.SetDirty(mb);
        }

        // --- physics rate (req.md §17) ---
        if (Time.fixedDeltaTime > 1f / PhysicsRate + 0.0001f)
        {
            Time.fixedDeltaTime = 1f / PhysicsRate;
            did.Add($"fixed timestep → {1f / PhysicsRate:0.###} s ({PhysicsRate} Hz)");
        }

        Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(boxer);

        string done = did.Count > 0 ? "  • " + string.Join("\n  • ", did) : "  (already set up)";
        Debug.Log($"Physics boxer setup on '{boxer.name}':\n{done}\n\n{Describe(boxer)}", boxer);
    }

    /// <summary>
    /// Write the tuning this character actually needs onto an existing BoxerPhysics. The values that matter
    /// most are the ones that are wildly nonlinear or scale-dependent: Pin Pow (a pin of 0.35 at pow 4 is 0.015,
    /// i.e. unpinned — which is what let the head drift and stretch the neck), the head's pin, and the spring,
    /// which has to grow with a character that is not 1:1 scale.
    /// </summary>
    private static void TuneBody(BoxerPhysics physics, float scale, List<string> did)
    {
        SerializedObject so = new SerializedObject(physics);
        Set(so, "pinPow", 2f, did, "pin pow 4 → 2 (at 4, mid pin values are effectively zero)");
        SetMin(so, "muscleSpring", 500f, did, "muscle spring → 500 (auto-scaled by character size)");
        SetMin(so, "muscleDamper", 10f, did, "muscle damper → 10");
        SetBool(so, "autoScaleToCharacter", true, did, $"spring auto-scaled for this {scale:0.##}x character");
        SetVector2(so, "headControl", new Vector2(0.85f, 0.65f), did, "head pinned hard, driven soft (stops the neck stretching)");
        SetVector2(so, "spineControl", new Vector2(0.9f, 0.95f), did, "spine control raised");
        SetVector2(so, "armControl", new Vector2(0.8f, 0.9f), did, "arm control raised (punches were outrunning the puppet)");
        SetVector2(so, "handControl", new Vector2(0.7f, 0.8f), did, "hand control raised");
        Set(so, "headMapping", 0.65f, did, "head mapping 0.65 — this ragdoll has no neck muscle to bridge the gap");
        SetMin(so, "stretchLimit", 0.32f, did, "stretch tolerance raised (a fast punch legitimately lags)");
        SetMin(so, "stretchPanic", 1.2f, did, "panic threshold raised");
        so.ApplyModifiedProperties();
    }

    private static void Set(SerializedObject so, string name, float value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || Mathf.Approximately(p.floatValue, value)) return;
        p.floatValue = value;
        did.Add(note);
    }

    /// <summary>Only raises — never argues with a value the user has already pushed higher.</summary>
    private static void SetMin(SerializedObject so, string name, float value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || p.floatValue >= value) return;
        p.floatValue = value;
        did.Add(note);
    }

    private static void SetBool(SerializedObject so, string name, bool value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || p.boolValue == value) return;
        p.boolValue = value;
        did.Add(note);
    }

    private static void SetVector2(SerializedObject so, string name, Vector2 value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || p.vector2Value == value) return;
        p.vector2Value = value;
        did.Add(note);
    }

    // ------------------------------------------------------------------ Validate

    [MenuItem(ValidateMenu)]
    private static void ValidateSetup()
    {
        BoxerPunchController boxer = FindBoxer();
        if (boxer == null) return;
        Debug.Log(Describe(boxer), boxer);
    }

    /// <summary>Everything that decides whether the physics body will actually work, checked one at a time.</summary>
    private static string Describe(BoxerPunchController boxer)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"PHYSICS BOXER — '{boxer.name}'");

        SerializedObject so = new SerializedObject(boxer);
        bool use = so.FindProperty("usePhysicsBody")?.boolValue ?? false;
        Line(sb, use, "Use Physics Body is on", "Use Physics Body is OFF — the boxer is still IK-only");

        BoxerPhysics physics = boxer.GetComponent<BoxerPhysics>();
        Line(sb, physics != null, "BoxerPhysics present", "no BoxerPhysics component");

        PuppetMaster puppet = FindPuppet(boxer.transform);
        Line(sb, puppet != null, "PuppetMaster found", "no PuppetMaster targets this character — build the ragdoll first");
        if (puppet != null)
        {
            Line(sb, puppet.mode == PuppetMaster.Mode.Active, "mode = Active", $"mode = {puppet.mode} — the body will not simulate");
            Line(sb, puppet.muscles != null && puppet.muscles.Length >= 10,
                 $"{puppet.muscles?.Length ?? 0} muscles", $"only {puppet.muscles?.Length ?? 0} muscles — the arms may be missing");
            Line(sb, puppet.muscleSpring >= 300f, $"muscle spring {puppet.muscleSpring:0}",
                 $"muscle spring {puppet.muscleSpring:0} is low for this character — he will sag");
            Line(sb, puppet.pinDistanceFalloff <= 1f, $"pin falloff {puppet.pinDistanceFalloff:0.##}",
                 $"pin falloff {puppet.pinDistanceFalloff:0.##} is loose — punches will drift off target");

            int hands = 0;
            if (puppet.muscles != null)
                foreach (Muscle m in puppet.muscles) if (m != null && m.props.group == Muscle.Group.Hand) hands++;
            Line(sb, hands >= 2, $"{hands} hand muscles (the gloves)", $"{hands} hand muscles — punches cannot be sensed physically");

            // The gloves are on the puppet's layer, not the character's. If that layer cannot collide with the
            // bag, every punch silently passes straight through and nothing in the code will complain.
            PunchingBag bag = Object.FindAnyObjectByType<PunchingBag>();
            if (bag != null && puppet.muscles != null && puppet.muscles.Length > 0 && puppet.muscles[0].joint != null)
            {
                int puppetLayer = puppet.muscles[0].joint.gameObject.layer;
                int bagLayer = bag.gameObject.layer;
                Line(sb, !Physics.GetIgnoreLayerCollision(puppetLayer, bagLayer),
                     $"puppet layer '{LayerMask.LayerToName(puppetLayer)}' collides with bag layer '{LayerMask.LayerToName(bagLayer)}'",
                     $"layer '{LayerMask.LayerToName(puppetLayer)}' (puppet) and '{LayerMask.LayerToName(bagLayer)}' (bag) " +
                     "are set to IGNORE each other in Physics settings — every punch will pass straight through");
            }
        }

        Animator animator = boxer.GetComponent<Animator>();
        Line(sb, animator != null && animator.updateMode == AnimatorUpdateMode.Fixed,
             "Animator = Animate Physics (PuppetMaster owns Animator → IK → Read)",
             "Animator is NOT on Animate Physics — the puppet will read a pose the IK has not touched yet");
        Line(sb, animator != null && animator.isHuman, "avatar is Humanoid", "avatar is not Humanoid");

        FullBodyBipedIK fbbik = boxer.GetComponent<FullBodyBipedIK>();
        LookAtIK look = boxer.GetComponent<LookAtIK>();
        Line(sb, fbbik != null, "FullBodyBipedIK present", "no FullBodyBipedIK");
        Line(sb, look != null, "LookAtIK present", "no LookAtIK");

        IKExecutionOrder order = boxer.GetComponent<IKExecutionOrder>();
        if (order != null)
            sb.AppendLine("  ·  IKExecutionOrder is present — BoxerPhysics stands it down at runtime and lets " +
                          "PuppetMaster solve the IK in the right order. This is expected.");

        BoxerPhysicsBody legacy = boxer.GetComponent<BoxerPhysicsBody>();
        Line(sb, legacy == null || !legacy.enabled, "the old self-built puppet is off",
             "BoxerPhysicsBody is still enabled — two puppets will fight over the same bones");

        float scale = boxer.transform.lossyScale.y;
        if (Mathf.Abs(scale - 1f) > 0.01f)
            sb.AppendLine($"  ·  character scale is {scale:0.##}× — muscle spring and every distance in " +
                          "BoxerPhysics are tuned per-scale; if he sags, raise Muscle Spring first.");

        sb.AppendLine($"  ·  fixed timestep {Time.fixedDeltaTime * 1000f:0.#} ms ({1f / Time.fixedDeltaTime:0} Hz)" +
                      (Time.fixedDeltaTime > 0.0101f ? "  ← req.md §17 wants 100 Hz+" : ""));
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, bool ok, string good, string bad)
        => sb.AppendLine(ok ? $"  ✓  {good}" : $"  ✗  {bad}");

    // ------------------------------------------------------------------ Off

    [MenuItem(OffMenu)]
    private static void TurnOff()
    {
        BoxerPunchController boxer = FindBoxer();
        if (boxer == null) return;

        Undo.SetCurrentGroupName("Turn Physics Off");
        SerializedObject so = new SerializedObject(boxer);
        SerializedProperty use = so.FindProperty("usePhysicsBody");
        if (use != null) use.boolValue = false;
        so.ApplyModifiedProperties();

        PuppetMaster puppet = FindPuppet(boxer.transform);
        if (puppet != null)
        {
            Undo.RecordObject(puppet, "Turn Physics Off");
            puppet.mode = PuppetMaster.Mode.Kinematic;
            EditorUtility.SetDirty(puppet);
        }

        Animator animator = boxer.GetComponent<Animator>();
        if (animator != null)
        {
            Undo.RecordObject(animator, "Turn Physics Off");
            animator.updateMode = AnimatorUpdateMode.Normal;
            EditorUtility.SetDirty(animator);
        }

        EditorUtility.SetDirty(boxer);
        Debug.Log($"'{boxer.name}' is back on IK-driven punches: Use Physics Body off, PuppetMaster Kinematic, " +
                  "Animator on Normal update. Nothing else was changed.", boxer);
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>The selected boxer, or the only one in the scene.</summary>
    private static BoxerPunchController FindBoxer()
    {
        if (Selection.activeGameObject != null)
        {
            BoxerPunchController selected = Selection.activeGameObject.GetComponentInParent<BoxerPunchController>();
            if (selected != null) return selected;
        }
        return Object.FindAnyObjectByType<BoxerPunchController>();
    }

    private static PuppetMaster FindPuppet(Transform target)
    {
        foreach (PuppetMaster pm in Object.FindObjectsByType<PuppetMaster>(FindObjectsSortMode.None))
            if (pm.targetRoot == target) return pm;
        return null;
    }
}
