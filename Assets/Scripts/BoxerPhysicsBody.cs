using System;
using System.Collections.Generic;
using RootMotion.FinalIK;
using UnityEngine;

/// <summary>
/// The active ragdoll from req.md — a physical puppet of the boxer built at runtime from the humanoid bones:
///
///   Animator + Final IK pose the REFERENCE skeleton (guard, footwork, lean, the reference arm reaching for the
///   virtual fist)  →  ConfigurableJoint PD muscles pull the puppet's rigidbodies toward that pose  →  PhysX
///   simulates momentum, joint limits and collisions (fists into the bag, the bag into the boxer)  →  the visible
///   skeleton copies the puppet, so what you see is physics.
///
/// Control per part follows the doc's table: pelvis 90 % (kinematic, legs stay animated), legs 85 %, chest 70 %,
/// head 50 %, arms 60 %, fists physics-heavy. Overdrive raises the punching arm's muscle output and assist force
/// (physical capability, not damage). Blocking raises guard strength. A heavy shot to the head weakens every
/// muscle for a moment (stun); a huge one, or the upper body being shoved off balance, is a knockdown.
///
/// Safety: the puppet stays frozen (kinematic, no collisions) until it has been synced to the first animated
/// pose; joints are (re)built at that pose; elbows are hinges with an anti-hyperextension torque; velocities are
/// clamped; a part that runs away from its bone is teleported back.
///
/// Created and driven by <see cref="BoxerPunchController"/> (Use Physics Body).
/// </summary>
[DefaultExecutionOrder(-5)]
[DisallowMultipleComponent]
public class BoxerPhysicsBody : MonoBehaviour, IBoxerBody
{
    private enum Kind { Hips, Spine, Head, Shoulder, UpperArm, Forearm, Hand, UpperLeg, LowerLeg, Foot }

    [Header("Simulation")]
    [Tooltip("Physics steps per second. Boxing wants 100+ (sets Time.fixedDeltaTime). 0 = keep the project setting.")]
    [SerializeField] private int physicsRate = 100;

    [Tooltip("Give the puppet legs (only shown when knocked down; the legs stay animated while standing).")]
    [SerializeField] private bool simulateLegs = true;

    [Header("Control — how hard each part follows the animation (0-1, req.md §16)")]
    [Range(0f, 1f)] [SerializeField] private float legsControl = 0.85f;
    [Range(0f, 1f)] [SerializeField] private float spineControl = 0.7f;
    [Range(0f, 1f)] [SerializeField] private float headControl = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float armsControl = 0.7f;
    [Range(0f, 1f)] [SerializeField] private float fistsControl = 0.6f;

    [Header("Muscles (joint drives)")]
    [Tooltip("Spring torque per radian at control 1 for an 8 kg part.")]
    [Min(0f)] [SerializeField] private float baseSpring = 3000f;
    [Min(0f)] [SerializeField] private float baseDamper = 110f;
    [Tooltip("Punching-arm muscle multiplier at full overdrive (req.md §13).")]
    [Min(1f)] [SerializeField] private float overdriveMuscle = 1.45f;
    [Tooltip("Arm muscle multiplier while blocking — guard strength (req.md §26).")]
    [Min(1f)] [SerializeField] private float guardMuscle = 1.5f;

    [Header("Elbows")]
    [Tooltip("Sideways play allowed at the elbow (degrees). The elbow is a hinge; this keeps it from breaking sideways.")]
    [Range(2f, 45f)] [SerializeField] private float elbowSideplay = 12f;
    [Tooltip("Corrective torque (rad/s² per degree) when an elbow bends backwards past straight.")]
    [Min(0f)] [SerializeField] private float hyperextensionGain = 2.5f;

    [Header("Fist assist — a helping force toward the intent, not the engine (req.md §42)")]
    [Min(0f)] [SerializeField] private float assistPositionGain = 600f;
    [Min(0f)] [SerializeField] private float assistVelocityGain = 40f;
    [Min(0f)] [SerializeField] private float assistMaxForce = 900f;
    [Min(1f)] [SerializeField] private float overdriveAssist = 1.5f;

    [Header("Effective mass behind a punch (req.md §7, §19)")]
    [SerializeField] private float forearmMass = 1.5f;
    [SerializeField] private float upperArmMass = 2f;
    [SerializeField] private float chestMassFactor = 5f;
    [SerializeField] private float hipsMassFactor = 7f;

    [Header("Stun & knockdown (req.md §31, §33)")]
    [SerializeField] private bool allowKnockdown = true;
    [Min(0.1f)] [SerializeField] private float stunSpeed = 2.5f;
    [Range(0.05f, 1f)] [SerializeField] private float stunStrength = 0.45f;
    [Min(0.05f)] [SerializeField] private float stunDuration = 0.8f;
    [Min(0.1f)] [SerializeField] private float knockdownSpeed = 4.5f;
    [Min(0.05f)] [SerializeField] private float balanceLimit = 0.35f;
    [Range(0.02f, 0.5f)] [SerializeField] private float knockdownMuscle = 0.15f;
    [Min(0.5f)] [SerializeField] private float knockdownTime = 2.5f;
    [Min(0.1f)] [SerializeField] private float getUpTime = 0.8f;

    [Header("Safety")]
    [Tooltip("A part further than this from its bone (metres) is teleported back to the reference.")]
    [Min(0.2f)] [SerializeField] private float runawayDistance = 0.8f;
    [Tooltip("Speed cap for every puppet part (m/s).")]
    [Min(1f)] [SerializeField] private float maxPartSpeed = 25f;

    [Header("Debug")]
    [SerializeField] private bool showPuppet = true;

    // ------------------------------------------------------------------ Public state

    public bool IsBuilt { get; private set; }
    public bool IsSynced { get; private set; }
    public bool IsKnockedDown { get; private set; }
    public float MuscleStrength { get; private set; } = 1f;
    public float BalanceOffset { get; private set; }

    public PhysicalFist LeftFist { get; private set; }
    public PhysicalFist RightFist { get; private set; }
    public Rigidbody Hips => hipsPart?.rb;
    public Rigidbody ChestBody => chestPart?.rb;
    public Rigidbody HeadBody => headPart?.rb;

    public event Action<bool> KnockdownChanged;

    // ------------------------------------------------------------------ Private state

    private class Part
    {
        public HumanBodyBones id;
        public Kind kind;
        public int side;
        public Transform bone;
        public Transform puppet;
        public Rigidbody rb;
        public Collider collider;
        public ConfigurableJoint joint;
        public Part parent;
        public Quaternion startRelative;
        public Quaternion targetRelative;
        public float control;
        public float radius;

        public bool IsLeg => kind == Kind.UpperLeg || kind == Kind.LowerLeg || kind == Kind.Foot;
        public bool IsArm => kind == Kind.Shoulder || kind == Kind.UpperArm || kind == Kind.Forearm || kind == Kind.Hand;
    }

    private struct HandCommand
    {
        public bool active;
        public Vector3 target;
        public Vector3 velocity;
        public float drive;
    }

    private readonly List<Part> parts = new List<Part>();
    private readonly HandCommand[] hands = new HandCommand[2];
    private readonly Part[] handParts = new Part[2];
    private readonly Part[] forearmParts = new Part[2];
    private readonly Part[] upperArmParts = new Part[2];
    private readonly Vector3[] refBendAxis = new Vector3[2];     // reference elbow bend direction, world
    private readonly bool[] refBendValid = new bool[2];

    private BoxerPunchController owner;
    private Animator animator;
    private FullBodyBipedIK ik;
    private Transform puppetRoot;
    private Part hipsPart, chestPart, headPart;

    private bool hasReference;
    private Vector3 refHipsPos;
    private Quaternion refHipsRot;
    private Vector3 rootVelocity;
    private Vector3 lastRefHipsPos;

    private bool guard;
    private float externalStrength = 1f;
    private float stunUntil = -10f;
    private float knockdownUntil;
    private bool gettingUp;
    private float getUpStart;
    private float legMap;
    private float offBalanceSince = -1f;
    private float lastRunawayLog = -10f;

    // ------------------------------------------------------------------ Setup

    public void Initialize(BoxerPunchController owner, Animator animator, FullBodyBipedIK ik)
    {
        if (IsBuilt) return;
        this.owner = owner;
        this.animator = animator;
        this.ik = ik;

        if (animator == null || !animator.isHuman)
        {
            Debug.LogError("BoxerPhysicsBody: needs a Humanoid Animator.", this);
            enabled = false;
            return;
        }

        if (physicsRate > 0) Time.fixedDeltaTime = 1f / physicsRate;

        Build();
        if (ik != null) ik.solver.OnPostUpdate += OnAfterIK;
        IsBuilt = true;
    }

    private void OnDestroy()
    {
        if (ik != null) ik.solver.OnPostUpdate -= OnAfterIK;
        if (puppetRoot != null) Destroy(puppetRoot.gameObject);
    }

    private void Build()
    {
        puppetRoot = new GameObject("Puppet (" + name + ")").transform;

        hipsPart = Add(HumanBodyBones.Hips, null, Kind.Hips, 12f, 0.15f, 1f, 0);
        if (hipsPart == null) { Debug.LogError("BoxerPhysicsBody: no Hips bone.", this); return; }

        Part spine = Add(HumanBodyBones.Spine, hipsPart, Kind.Spine, 8f, 0.13f, spineControl, 0);
        Part chest = Add(HumanBodyBones.Chest, spine ?? hipsPart, Kind.Spine, 10f, 0.15f, spineControl, 0);
        Part upperChest = Add(HumanBodyBones.UpperChest, chest ?? spine ?? hipsPart, Kind.Spine, 6f, 0.14f, spineControl, 0);
        Part torsoTop = upperChest ?? chest ?? spine ?? hipsPart;
        chestPart = chest ?? spine ?? hipsPart;

        Part neck = Add(HumanBodyBones.Neck, torsoTop, Kind.Head, 1.5f, 0.06f, headControl, 0);
        headPart = Add(HumanBodyBones.Head, neck ?? torsoTop, Kind.Head, 5f, 0.11f, headControl, 0);

        for (int side = -1; side <= 1; side += 2)
        {
            bool left = side < 0;
            int h = left ? 0 : 1;
            Part shoulder = Add(left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder, torsoTop, Kind.Shoulder, 2f, 0.06f, armsControl, side);
            Part upper = Add(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm, shoulder ?? torsoTop, Kind.UpperArm, 2.5f, 0.055f, armsControl, side);
            Part fore = upper != null ? Add(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm, upper, Kind.Forearm, 1.6f, 0.045f, armsControl, side) : null;
            Part hand = fore != null ? Add(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, fore, Kind.Hand, 1.2f, 0.08f, fistsControl, side) : null;
            upperArmParts[h] = upper;
            forearmParts[h] = fore;
            handParts[h] = hand;

            if (simulateLegs)
            {
                Part upLeg = Add(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg, hipsPart, Kind.UpperLeg, 8f, 0.09f, legsControl, side);
                Part lowLeg = upLeg != null ? Add(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg, upLeg, Kind.LowerLeg, 4f, 0.065f, legsControl, side) : null;
                if (lowLeg != null) Add(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot, lowLeg, Kind.Foot, 1f, 0.06f, legsControl, side);
            }
        }

        AddColliders();
        RebuildJoints();
        IgnoreSelfCollisions();

        for (int h = 0; h < 2; h++)
        {
            if (handParts[h] == null) continue;
            PhysicalFist fist = handParts[h].puppet.gameObject.AddComponent<PhysicalFist>();
            fist.Hand = h;
            fist.Body = this;
            handParts[h].puppet.GetComponent<PhysicsPuppetPart>().Fist = fist;
            if (h == 0) LeftFist = fist; else RightFist = fist;
        }

        SetLegCollidersEnabled(false);
    }

    private Part Add(HumanBodyBones id, Part parent, Kind kind, float mass, float radius, float control, int side)
    {
        Transform bone = animator.GetBoneTransform(id);
        if (bone == null) return null;
        if (parent == null && kind != Kind.Hips) return null;

        GameObject go = new GameObject("Puppet " + id);
        go.transform.SetParent(puppetRoot, false);
        go.transform.SetPositionAndRotation(bone.position, bone.rotation);
        go.layer = gameObject.layer;

        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = kind == Kind.Hand ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.Discrete;
        rb.solverIterations = 10;
        rb.solverVelocityIterations = 4;
        rb.maxAngularVelocity = 30f;
        rb.maxLinearVelocity = maxPartSpeed;
        rb.linearDamping = 0.05f;
        rb.angularDamping = 0.5f;
        rb.useGravity = kind != Kind.Hips;

        // Frozen until the puppet has been synced to the first animated pose (never simulate the T-pose).
        rb.isKinematic = true;
        rb.detectCollisions = false;

        PhysicsPuppetPart tag = go.AddComponent<PhysicsPuppetPart>();
        tag.Owner = owner;
        tag.Body = this;
        tag.Bone = id;
        tag.IsHand = kind == Kind.Hand;
        tag.IsHead = kind == Kind.Head;

        Part part = new Part
        {
            id = id, kind = kind, side = side, bone = bone, puppet = go.transform, rb = rb, parent = parent, control = control, radius = radius,
            startRelative = parent != null ? Quaternion.Inverse(parent.bone.rotation) * bone.rotation : Quaternion.identity,
            targetRelative = parent != null ? Quaternion.Inverse(parent.bone.rotation) * bone.rotation : Quaternion.identity,
        };
        parts.Add(part);
        return part;
    }

    private void AddColliders()
    {
        foreach (Part part in parts)
        {
            float radius = part.radius;
            Part child = null;
            foreach (Part p in parts) if (p.parent == part && p.kind != Kind.Shoulder && !(part.kind == Kind.Hips && p.IsLeg)) { child = p; break; }
            if (child == null) foreach (Part p in parts) if (p.parent == part) { child = p; break; }

            Vector3 a = part.bone.position;
            switch (part.kind)
            {
                case Kind.Hips:
                {
                    SphereCollider s = part.puppet.gameObject.AddComponent<SphereCollider>();
                    s.radius = radius;
                    part.collider = s;
                    break;
                }
                case Kind.Head:
                {
                    SphereCollider s = part.puppet.gameObject.AddComponent<SphereCollider>();
                    s.radius = radius;
                    s.center = part.puppet.InverseTransformPoint(a + Vector3.up * (part.id == HumanBodyBones.Head ? 0.08f : 0.02f));
                    part.collider = s;
                    break;
                }
                case Kind.Hand:
                {
                    Vector3 along = part.parent != null ? (a - part.parent.bone.position).normalized : part.bone.forward;
                    SphereCollider s = part.puppet.gameObject.AddComponent<SphereCollider>();
                    s.radius = radius;
                    s.center = part.puppet.InverseTransformPoint(a + along * 0.05f);
                    part.collider = s;
                    break;
                }
                case Kind.Foot:
                {
                    SphereCollider s = part.puppet.gameObject.AddComponent<SphereCollider>();
                    s.radius = radius;
                    s.center = part.puppet.InverseTransformPoint(a + Vector3.down * 0.02f);
                    part.collider = s;
                    break;
                }
                default:
                {
                    if (child == null)
                    {
                        SphereCollider s = part.puppet.gameObject.AddComponent<SphereCollider>();
                        s.radius = radius;
                        part.collider = s;
                        break;
                    }
                    Vector3 b = child.bone.position;
                    Vector3 localDir = part.puppet.InverseTransformDirection(b - a);
                    int axis = Mathf.Abs(localDir.x) >= Mathf.Abs(localDir.y) && Mathf.Abs(localDir.x) >= Mathf.Abs(localDir.z) ? 0
                             : Mathf.Abs(localDir.y) >= Mathf.Abs(localDir.z) ? 1 : 2;
                    CapsuleCollider c = part.puppet.gameObject.AddComponent<CapsuleCollider>();
                    c.direction = axis;
                    c.radius = radius;
                    c.height = Mathf.Max(Vector3.Distance(a, b) + radius, radius * 2f);
                    c.center = part.puppet.InverseTransformPoint((a + b) * 0.5f);
                    part.collider = c;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// (Re)create every joint with the CURRENT puppet pose as its zero. Called at build (T-pose) and again at the
    /// first sync (guard pose), where the elbow hinge axes can be measured from the bent reference arm.
    /// </summary>
    private void RebuildJoints()
    {
        foreach (Part part in parts)
        {
            if (part.parent == null) continue;
            if (part.joint != null) DestroyImmediate(part.joint);

            ConfigurableJoint j = part.puppet.gameObject.AddComponent<ConfigurableJoint>();

            float twist, swing;
            switch (part.kind)
            {
                case Kind.Spine:    twist = 30f; swing = 35f; break;
                case Kind.Head:     twist = 60f; swing = 45f; break;
                case Kind.Shoulder: twist = 20f; swing = 25f; break;
                case Kind.UpperArm: twist = 90f; swing = 110f; break;
                case Kind.Forearm:  twist = 130f; swing = elbowSideplay; break;   // hinge: X = flexion axis
                case Kind.Hand:     twist = 45f; swing = 45f; break;
                case Kind.UpperLeg: twist = 40f; swing = 90f; break;
                case Kind.LowerLeg: twist = 20f; swing = 120f; break;
                default:            twist = 30f; swing = 40f; break;
            }

            // Axes must be set before the joint connects, so the joint frame is built around them.
            if (part.kind == Kind.Forearm && TryElbowAxes(part, out Vector3 axisLocal, out Vector3 secondaryLocal))
            {
                j.axis = axisLocal;
                j.secondaryAxis = secondaryLocal;
            }

            j.autoConfigureConnectedAnchor = true;
            j.anchor = Vector3.zero;
            j.connectedBody = part.parent.rb;

            j.xMotion = ConfigurableJointMotion.Locked;
            j.yMotion = ConfigurableJointMotion.Locked;
            j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.angularYMotion = ConfigurableJointMotion.Limited;
            j.angularZMotion = ConfigurableJointMotion.Limited;
            j.lowAngularXLimit = new SoftJointLimit { limit = -twist, bounciness = 0f, contactDistance = 0f };
            j.highAngularXLimit = new SoftJointLimit { limit = twist, bounciness = 0f, contactDistance = 0f };
            j.angularYLimit = new SoftJointLimit { limit = swing, bounciness = 0f, contactDistance = 0f };
            j.angularZLimit = new SoftJointLimit { limit = swing, bounciness = 0f, contactDistance = 0f };
            SoftJointLimitSpring limitSpring = new SoftJointLimitSpring { spring = 600f, damper = 60f };
            j.angularXLimitSpring = limitSpring;
            j.angularYZLimitSpring = limitSpring;

            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.enablePreprocessing = false;
            j.enableCollision = false;
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.projectionDistance = 0.02f;
            j.projectionAngle = 5f;

            part.joint = j;
            part.startRelative = Quaternion.Inverse(part.parent.puppet.rotation) * part.puppet.rotation;
        }
    }

    /// <summary>Elbow flexion axis from the bent arm (shoulder→elbow × elbow→hand), in the forearm puppet's local space.</summary>
    private bool TryElbowAxes(Part forearm, out Vector3 axisLocal, out Vector3 secondaryLocal)
    {
        axisLocal = Vector3.right;
        secondaryLocal = Vector3.up;
        Part upper = forearm.parent;
        Part hand = null;
        foreach (Part p in parts) if (p.parent == forearm && p.kind == Kind.Hand) { hand = p; break; }
        if (upper == null || hand == null) return false;

        Vector3 a = (forearm.puppet.position - upper.puppet.position).normalized;
        Vector3 b = (hand.puppet.position - forearm.puppet.position).normalized;
        Vector3 axis = Vector3.Cross(a, b);
        if (axis.sqrMagnitude < 0.01f) return false;                 // arm is straight: keep the default frame
        axis.Normalize();

        axisLocal = forearm.puppet.InverseTransformDirection(axis);
        secondaryLocal = forearm.puppet.InverseTransformDirection(Vector3.Cross(axis, b).normalized);
        return true;
    }

    private void IgnoreSelfCollisions()
    {
        for (int i = 0; i < parts.Count; i++)
            for (int k = i + 1; k < parts.Count; k++)
                if (parts[i].collider != null && parts[k].collider != null)
                    Physics.IgnoreCollision(parts[i].collider, parts[k].collider, true);

        foreach (Collider own in GetComponentsInChildren<Collider>(true))
            foreach (Part part in parts)
                if (part.collider != null) Physics.IgnoreCollision(part.collider, own, true);
    }

    private void SetLegCollidersEnabled(bool on)
    {
        foreach (Part part in parts)
            if (part.IsLeg && part.collider != null) part.collider.enabled = on;
    }

    // ------------------------------------------------------------------ Per-frame: reference capture & visual mapping

    private void OnAfterIK()
    {
        if (!IsBuilt || hipsPart == null) return;

        refHipsPos = hipsPart.bone.position;
        refHipsRot = hipsPart.bone.rotation;
        foreach (Part part in parts)
            if (part.parent != null)
                part.targetRelative = Quaternion.Inverse(part.parent.bone.rotation) * part.bone.rotation;

        for (int h = 0; h < 2; h++)
        {
            refBendValid[h] = false;
            if (upperArmParts[h] == null || forearmParts[h] == null || handParts[h] == null) continue;
            Vector3 a = forearmParts[h].bone.position - upperArmParts[h].bone.position;
            Vector3 b = handParts[h].bone.position - forearmParts[h].bone.position;
            Vector3 axis = Vector3.Cross(a.normalized, b.normalized);
            if (axis.sqrMagnitude < 0.01f) continue;
            refBendAxis[h] = axis.normalized;
            refBendValid[h] = true;
        }

        if (!IsSynced)
        {
            SyncPuppetToReference();
            lastRefHipsPos = refHipsPos;
        }
        hasReference = true;

        // Visible skeleton = puppet (upper body always; legs and hips only when down or getting up).
        foreach (Part part in parts)
        {
            if (part == hipsPart)
            {
                if (legMap > 0f)
                {
                    part.bone.position = Vector3.Lerp(part.bone.position, part.puppet.position, legMap);
                    part.bone.rotation = Quaternion.Slerp(part.bone.rotation, part.puppet.rotation, legMap);
                }
                continue;
            }
            float w = part.IsLeg ? legMap : 1f;
            if (w <= 0f) continue;
            part.bone.rotation = w >= 1f ? part.puppet.rotation : Quaternion.Slerp(part.bone.rotation, part.puppet.rotation, w);
        }
    }

    /// <summary>Teleport the puppet onto the reference pose, rebuild the joints there and wake it up.</summary>
    private void SyncPuppetToReference()
    {
        foreach (Part part in parts)
        {
            part.puppet.SetPositionAndRotation(part.bone.position, part.bone.rotation);
            part.rb.position = part.bone.position;
            part.rb.rotation = part.bone.rotation;
        }
        RebuildJoints();
        foreach (Part part in parts)
        {
            bool kinematic = part == hipsPart && !IsKnockedDown;
            part.rb.isKinematic = kinematic;
            part.rb.detectCollisions = true;
            if (!kinematic)
            {
                part.rb.linearVelocity = Vector3.zero;
                part.rb.angularVelocity = Vector3.zero;
            }
        }
        IsSynced = true;
    }

    // ------------------------------------------------------------------ Physics step

    private void FixedUpdate()
    {
        if (!IsBuilt || !hasReference || !IsSynced) return;
        float dt = Time.fixedDeltaTime;

        UpdateStates(dt);

        if (hipsPart.rb.isKinematic)
        {
            hipsPart.rb.MovePosition(refHipsPos);
            hipsPart.rb.MoveRotation(refHipsRot);
        }
        rootVelocity = (refHipsPos - lastRefHipsPos) / Mathf.Max(dt, 0.0001f);
        lastRefHipsPos = refHipsPos;

        // Runaway guard: anything that got flung away from its bone is put back before it can drag the rest.
        foreach (Part part in parts)
        {
            if (part.IsLeg || part == hipsPart) continue;
            if ((part.rb.position - part.bone.position).sqrMagnitude > runawayDistance * runawayDistance)
            {
                if (Time.time - lastRunawayLog > 2f) { Debug.LogWarning($"BoxerPhysicsBody: {part.id} ran away — resynced.", this); lastRunawayLog = Time.time; }
                SyncPuppetToReference();
                break;
            }
        }

        foreach (Part part in parts)
        {
            if (part.joint == null) continue;

            float strength = part.control * MuscleStrength * externalStrength;
            if (part.IsArm) strength *= ArmBoost(part.side);
            float massScale = 0.5f + 0.5f * (part.rb.mass / 8f);

            part.joint.slerpDrive = new JointDrive
            {
                positionSpring = baseSpring * strength * massScale,
                positionDamper = baseDamper * Mathf.Sqrt(Mathf.Max(0.05f, strength)) * massScale,
                maximumForce = float.MaxValue,
            };
            SetTargetRotationLocal(part.joint, part.targetRelative, part.startRelative);
        }

        for (int h = 0; h < 2; h++)
        {
            Part hand = handParts[h];
            if (hand == null || !hands[h].active) continue;
            float gain = (1f + (overdriveAssist - 1f) * hands[h].drive) * MuscleStrength * externalStrength;
            Vector3 positionError = hands[h].target - hand.rb.position;
            Vector3 velocityError = hands[h].velocity - hand.rb.linearVelocity;
            Vector3 force = (positionError * assistPositionGain + velocityError * assistVelocityGain) * gain;
            force = Vector3.ClampMagnitude(force, assistMaxForce * gain);
            hand.rb.AddForce(force, ForceMode.Force);
        }

        GuardElbows();
        UpdateBalance(dt);
    }

    /// <summary>An elbow bent backwards past straight (the wrong side of the reference bend) is torqued back.</summary>
    private void GuardElbows()
    {
        for (int h = 0; h < 2; h++)
        {
            if (!refBendValid[h] || upperArmParts[h] == null || forearmParts[h] == null || handParts[h] == null) continue;
            Vector3 a = (forearmParts[h].rb.position - upperArmParts[h].rb.position).normalized;
            Vector3 b = (handParts[h].rb.position - forearmParts[h].rb.position).normalized;
            Vector3 axis = Vector3.Cross(a, b);
            float bend = Vector3.Angle(a, b);                          // 0 = straight
            if (bend < 6f || Vector3.Dot(axis, refBendAxis[h]) >= 0f) continue;
            forearmParts[h].rb.AddTorque(refBendAxis[h] * (bend * hyperextensionGain), ForceMode.Acceleration);
        }
    }

    private float ArmBoost(int side)
    {
        int h = side < 0 ? 0 : 1;
        float boost = 1f + (overdriveMuscle - 1f) * hands[h].drive;
        if (guard) boost *= guardMuscle;
        return boost;
    }

    private static void SetTargetRotationLocal(ConfigurableJoint joint, Quaternion targetLocalRotation, Quaternion startLocalRotation)
    {
        Vector3 right = joint.axis;
        Vector3 forward = Vector3.Cross(joint.axis, joint.secondaryAxis).normalized;
        Vector3 up = Vector3.Cross(forward, right).normalized;
        Quaternion worldToJointSpace = Quaternion.LookRotation(forward, up);

        Quaternion result = Quaternion.Inverse(worldToJointSpace);
        result *= Quaternion.Inverse(targetLocalRotation) * startLocalRotation;
        result *= worldToJointSpace;
        joint.targetRotation = result;
    }

    private void UpdateStates(float dt)
    {
        float now = Time.time;

        if (IsKnockedDown)
        {
            MuscleStrength = knockdownMuscle;
            if (now >= knockdownUntil) GetUp();
            return;
        }

        if (gettingUp)
        {
            float t = Mathf.Clamp01((now - getUpStart) / getUpTime);
            MuscleStrength = Mathf.Lerp(0.3f, 1f, t);
            legMap = 1f - t;
            if (t >= 1f) { gettingUp = false; legMap = 0f; }
            return;
        }

        if (now < stunUntil) MuscleStrength = Mathf.MoveTowards(MuscleStrength, stunStrength, dt / 0.08f);
        else MuscleStrength = Mathf.MoveTowards(MuscleStrength, 1f, dt / 0.3f);
    }

    private void UpdateBalance(float dt)
    {
        if (chestPart == null || chestPart == hipsPart) return;

        Vector3 com = Vector3.zero;
        float total = 0f;
        Vector3 refCom = Vector3.zero;
        foreach (Part part in parts)
        {
            if (part.IsLeg || part == hipsPart) continue;
            com += part.rb.worldCenterOfMass * part.rb.mass;
            refCom += part.bone.position * part.rb.mass;
            total += part.rb.mass;
        }
        if (total <= 0f) return;
        com /= total;
        refCom /= total;

        BalanceOffset = Vector3.ProjectOnPlane(com - refCom, Vector3.up).magnitude;

        if (IsKnockedDown || gettingUp || !allowKnockdown) return;
        if (BalanceOffset > balanceLimit)
        {
            if (offBalanceSince < 0f) offBalanceSince = Time.time;
            else if (Time.time - offBalanceSince > 0.15f) KnockDown();
        }
        else offBalanceSince = -1f;
    }

    // ------------------------------------------------------------------ Commands from the punch controller

    public void SetHand(int hand, bool active, Vector3 target, Vector3 velocity, float drive)
    {
        hands[hand] = new HandCommand { active = active, target = target, velocity = velocity, drive = Mathf.Clamp01(drive) };
    }

    /// <summary>
    /// This puppet reads everything it needs from the hand command's drive value; the punch phase and clock are
    /// only used by the PuppetMaster backend (<see cref="BoxerPhysics"/>) to stagger the kinetic chain and to
    /// drop the arm's pin on contact. Accepted so the two bodies are interchangeable.
    /// </summary>
    public void SetPunch(int hand, BoxerPunchController.Phase phase, float x, float overdrive) { }

    public void SetGuard(bool blocking) => guard = blocking;

    public void SetStrength(float strength) => externalStrength = Mathf.Clamp(strength, 0.05f, 2f);

    public void NotifyHeadHit(float speed)
    {
        if (IsKnockedDown || gettingUp) return;
        if (allowKnockdown && speed >= knockdownSpeed) { KnockDown(); return; }
        if (speed >= stunSpeed)
        {
            float scale = Mathf.Clamp(speed / stunSpeed, 1f, 2f);
            stunUntil = Mathf.Max(stunUntil, Time.time + stunDuration * scale);
        }
    }

    public float EffectiveMass(int hand, Vector3 direction, float impactSpeed)
    {
        Part fist = handParts[hand];
        float mass = fist != null ? fist.rb.mass : 1f;
        float norm = Mathf.Max(impactSpeed, 0.5f);

        mass += forearmMass * Contribution(forearmParts[hand], direction, norm);
        mass += upperArmMass * Contribution(upperArmParts[hand], direction, norm);
        mass += chestMassFactor * Contribution(chestPart, direction, norm);
        mass += hipsMassFactor * Mathf.Clamp01(Vector3.Dot(rootVelocity, direction) / norm);
        return mass;
    }

    private static float Contribution(Part part, Vector3 direction, float norm)
    {
        if (part == null || part.rb.isKinematic) return 0f;
        return Mathf.Clamp01(Vector3.Dot(part.rb.linearVelocity, direction) / norm);
    }

    public void KnockDown()
    {
        if (IsKnockedDown || !IsBuilt || !IsSynced) return;
        IsKnockedDown = true;
        gettingUp = false;
        knockdownUntil = Time.time + knockdownTime;
        legMap = 1f;
        offBalanceSince = -1f;

        hipsPart.rb.isKinematic = false;
        hipsPart.rb.useGravity = true;
        SetLegCollidersEnabled(true);
        KnockdownChanged?.Invoke(true);
    }

    private void GetUp()
    {
        IsKnockedDown = false;
        gettingUp = true;
        getUpStart = Time.time;

        hipsPart.rb.useGravity = false;
        SetLegCollidersEnabled(false);
        SyncPuppetToReference();
        KnockdownChanged?.Invoke(false);
    }

    private void OnDrawGizmos()
    {
        if (!showPuppet || !IsBuilt) return;
        foreach (Part part in parts)
        {
            if (part.puppet == null) continue;
            Gizmos.color = part.kind == Kind.Hand ? new Color(1f, 0.4f, 0.2f, 0.9f) : new Color(0.3f, 0.8f, 1f, 0.6f);
            Gizmos.DrawWireSphere(part.puppet.position, 0.03f);
            if (part.parent != null) Gizmos.DrawLine(part.puppet.position, part.parent.puppet.position);
        }
    }
}
