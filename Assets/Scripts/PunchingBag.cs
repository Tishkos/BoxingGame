using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A heavy bag that behaves like 40 kilograms of sand on a chain — and can PROVE it.
///
/// The old bag trusted its callers: whatever impulse a sensor computed, it applied. One glitchy frame (an IK
/// snap read as a 40 m/s fist, a kinematic glove depenetrating, a guard pose swept through the leather) and the
/// bag left the building. This bag trusts nobody. Every way energy can enter is metered:
///
///   • <see cref="Hit"/> — a punch. The impulse is clamped by the LAWS OF THE BAG: the exact rigid-body
///     effective mass at the contact point (translation + rotation) is computed, and the impulse is cut so the
///     surface there can never exceed <see cref="maxSurfaceSpeed"/>. However wrong the number a sensor sends,
///     the bag's answer is bounded by what a real bag could do.
///   • <see cref="Lean"/> — a glove resting / pressed into the bag (guard up against it, walking into it).
///     A capped spring force, never an impulse. This is why raising L1/R1 with the gloves in the leather now
///     eases the bag away instead of launching it.
///   • <see cref="FollowThrough"/> — the fist driving through after contact. Drains a reservoir charged by the
///     hit itself (a fraction of the landed impulse), so follow-through can deepen a punch but never out-punch it.
///   • <see cref="Bounce"/> — the bag swinging into a raised guard. Reflects the surface velocity with a
///     restitution, capped like everything else.
///   • PhysX itself — the rigidbody carries hard ceilings (max linear/angular velocity, max depenetration
///     velocity), so even a contact the scripts never saw cannot explode it.
///
/// The rig is built on Awake (or from the context menu): kinematic mount → chain links on swing joints → the
/// bag as a bottom-heavy rigidbody with a capsule fitted to the mesh. Gravity does the pendulum; the joints add
/// the strap stiffness and chain friction that let it settle. Punch response comes back through <see cref="OnHit"/>
/// (VFX, feedback), the mesh dents via <see cref="BagDeformer"/>, and when the bag swings into a
/// <see cref="BoxerPunchController"/> it reports the contact so the boxer staggers or blocks it.
/// </summary>
[DisallowMultipleComponent]
public class PunchingBag : MonoBehaviour
{
    /// <summary>Details of one punch, passed to <see cref="OnHit"/>.</summary>
    public struct HitInfo
    {
        public Vector3 point;        // world-space contact point
        public Vector3 direction;    // world-space direction of the punch (unit length)
        public float impulse;        // N·s actually applied (after the bag's own clamps)
        public float strength;       // impulse / Full Strength Impulse, clamped to 0..1
        public float cleanliness;    // 1 = straight through the bag's centre, 0 = glancing blow
        public float speed;          // relative impact speed (fist minus bag), m/s
        public int hand;             // 0 left · 1 right · -1 unknown (debug hits)
        public float overdrive;      // how loaded/flicked the punch was, 0-1 (0 when unknown)
        public float closing;        // how fast the bag surface was coming AT the fist (m/s, pre-impulse) — the counter signal
        public float surfaceSpeed;   // bag surface speed at the contact, pre-impulse (m/s) — 0 ≈ a dead-still bag
    }

    [Serializable] public class HitEvent : UnityEvent<PunchingBag, HitInfo> { }

    [Header("Suspension")]
    [Tooltip("Ceiling mount point. If empty, one is created straight above the bag, Chain Length above its top.")]
    [SerializeField] private Transform mount;

    [Tooltip("Distance from the top of the bag up to the mount (metres). Longer chain = slower, wider swing.")]
    [Min(0.05f)]
    [SerializeField] private float chainLength = 0.6f;

    [Tooltip("Rigidbodies in the chain. 2-4; more = floppier chain, slightly more CPU.")]
    [Range(1, 4)]
    [SerializeField] private int chainSegments = 3;

    [Tooltip("How far each chain link may swing from vertical (degrees).")]
    [Range(5f, 89f)]
    [SerializeField] private float maxSwingAngle = 60f;

    [Tooltip("How far the bag may tilt relative to the chain (degrees). Real bags fold only a little at the strap.")]
    [Range(0f, 60f)]
    [SerializeField] private float maxBagTilt = 15f;

    [Tooltip("Twist allowed around the chain (degrees).")]
    [Range(0f, 90f)]
    [SerializeField] private float maxTwist = 20f;

    [Header("Weight & settling")]
    [Tooltip("Bag mass in kg. Heavy bags are 30-70 kg; lighter swings further per punch.")]
    [Min(1f)]
    [SerializeField] private float bagMass = 40f;

    [Tooltip("Spring pulling the chain back to hanging straight. Gravity already does most of this; keep small — " +
             "a real chain has no spring at all, only friction.")]
    [Min(0f)]
    [SerializeField] private float swingSpring = 3f;

    [Tooltip("Friction in the chain/straps. Higher = swing dies out sooner.")]
    [Min(0f)]
    [SerializeField] private float swingDamper = 14f;

    [Tooltip("Air resistance on the bag body.")]
    [Min(0f)]
    [SerializeField] private float airDrag = 0.3f;

    [Tooltip("Spin friction — sand grinding in the shell as the bag twists. Keeps a spun bag from turning forever.")]
    [Min(0f)]
    [SerializeField] private float spinDrag = 1.4f;

    [Tooltip("How far below the geometric centre the mass sits (fraction of bag height). Sand settles: a real " +
             "bag is bottom-heavy, which is why the bottom kicks out and the top stays with the chain.")]
    [Range(0f, 0.25f)]
    [SerializeField] private float bottomHeaviness = 0.08f;

    [Header("Energy budget — what makes 'the bag flies' physically impossible")]
    [Tooltip("The surface of the bag can never be accelerated past this speed by a punch (m/s). The bag computes " +
             "its exact effective mass at the contact point (including rotation) and cuts any impulse that would " +
             "exceed this. A monster cross on a 40 kg bag legitimately reaches ~4-5 m/s at the point of impact.")]
    [Min(0.5f)]
    [SerializeField] private float maxSurfaceSpeed = 5f;

    [Tooltip("Hard ceiling on any single impulse (N·s), after the surface-speed clamp. A clean rear-hand cross " +
             "is ~70 N·s, an overdriven one ~110.")]
    [Min(1f)]
    [SerializeField] private float maxImpulse = 140f;

    [Tooltip("Absolute ceilings PhysX itself enforces on the bag body, whatever the scripts do: linear m/s, " +
             "angular rad/s, and how fast a penetrating contact may be pushed out (m/s). The last one is what " +
             "used to detonate the bag when a glove collider woke up inside it.")]
    [SerializeField] private Vector3 hardVelocityCaps = new Vector3(6f, 8f, 2f);

    [Tooltip("Fraction of a landed impulse banked for follow-through: the fist keeps shoving the bag while it " +
             "is planted, but only with energy the punch actually earned.")]
    [Range(0f, 1f)]
    [SerializeField] private float followThroughFraction = 0.35f;

    [Header("Leaning gloves (guard against the bag, walking into it)")]
    [Tooltip("Spring pushing the bag away from a glove pressed into it (N per metre of penetration).")]
    [Min(0f)]
    [SerializeField] private float leanSpring = 2600f;

    [Tooltip("Damping against the closing speed while pressed (N·s/m).")]
    [Min(0f)]
    [SerializeField] private float leanDamper = 90f;

    [Tooltip("A pressed glove can never push the bag harder than this (N). ~300 N is a firm two-arm shove.")]
    [Min(0f)]
    [SerializeField] private float leanMaxForce = 300f;

    [Header("Collider")]
    [Tooltip("Fit a capsule to the mesh bounds automatically. Untick if you add your own collider to the root.")]
    [SerializeField] private bool autoFitCollider = true;

    [Tooltip("Optional physics material. If empty a grippy, non-bouncy one is created.")]
    [SerializeField] private PhysicsMaterial bagMaterial;

    [Tooltip("Scale on the auto-fitted capsule radius. Match the yellow gizmo to the visible bag (mesh bounds are often a bit large).")]
    [Range(0.5f, 1.5f)]
    [SerializeField] private float colliderScale = 0.9f;

    [Header("Mesh squish")]
    [Tooltip("Dent the bag mesh where gloves hit it and while they are pressed into it (adds a BagDeformer).")]
    [SerializeField] private bool deformMesh = true;
    [Tooltip("Dent radius on the surface (metres).")]
    [Min(0.02f)] [SerializeField] private float dentRadius = 0.22f;
    [Tooltip("Dent depth of a full-strength clean hit (metres).")]
    [Min(0f)] [SerializeField] private float dentDepth = 0.09f;
    [Tooltip("Send a ripple across the leather on hard hits (needs the deformer).")]
    [SerializeField] private bool rippleOnHit = true;

    [Header("Impact feel")]
    [Tooltip("Transform to squash on impact. If empty, the first child with a renderer is used. Never the root itself.")]
    [SerializeField] private Transform visual;

    [Tooltip("How much the bag compresses on a full-strength clean hit (fraction of its width).")]
    [Range(0f, 0.5f)]
    [SerializeField] private float squashAmount = 0.1f;

    [SerializeField] private float squashSpring = 260f;
    [SerializeField] private float squashDamping = 16f;

    [Tooltip("Impulse (N·s) that counts as a full-strength hit for the squash, dents, audio volume, camera and " +
             "OnHit strength. A clean rear-hand cross is ~70 N·s, a jab ~50, an overdriven cross ~110.")]
    [Min(1f)]
    [SerializeField] private float fullPunchImpulse = 80f;

    [Tooltip("Chain links get a physical shake on hard hits — the rattle you see run up the chain.")]
    [Range(0f, 1f)]
    [SerializeField] private float chainRattle = 0.6f;

    [Header("Hitting back")]
    [Tooltip("Bag speed (m/s) below which touching the boxer is just leaning on it, not a hit.")]
    [Min(0f)]
    [SerializeField] private float minHitBackSpeed = 0.8f;

    [Header("Feedback")]
    [Tooltip("Raised for every punch. Hook up VFX, scoring, etc. (BagAudio is the bag's one voice — the old " +
             "built-in AudioSource fallback is gone so a wired inspector can never double-play hits.)")]
    public HitEvent OnHit = new HitEvent();

    [Header("Chain visual")]
    [Tooltip("Draw the simulated chain (mount → links → bag) so the bag stops floating in the air.")]
    [SerializeField] private bool renderChain = true;
    [Range(0.004f, 0.05f)] [SerializeField] private float chainWidth = 0.014f;
    [SerializeField] private Color chainColor = new Color(0.28f, 0.28f, 0.3f, 1f);

    // ------------------------------------------------------------------ Public state

    /// <summary>The bag's rigidbody once the rig is built (null before).</summary>
    public Rigidbody Body { get; private set; }

    /// <summary>The bag's collider once the rig is built (null before).</summary>
    public Collider BagCollider { get; private set; }

    public bool IsBuilt => Body != null;

    /// <summary>Impulse (N·s) that counts as a full-strength hit — sensors normalise their HitInfo.strength with this.</summary>
    public float FullStrengthImpulse => Mathf.Max(1f, fullPunchImpulse);

    /// <summary>World-space velocity of the bag body (zero before the rig is built).</summary>
    public Vector3 Velocity => Body != null ? Body.linearVelocity : Vector3.zero;

    /// <summary>The last punch that landed — VFX and audio read this instead of re-deriving it.</summary>
    public HitInfo LastHit { get; private set; }

    private readonly List<Rigidbody> chainLinks = new List<Rigidbody>();
    private BagDeformer deformer;
    private Vector3 visualBaseScale = Vector3.one;
    private float squash;
    private float squashVelocity;
    private LineRenderer chainLine;
    private readonly float[] followBudget = new float[2];       // N·s left to spend on follow-through, per hand
    private readonly Vector3[] leanForce = new Vector3[2];      // smoothed lean force per glove, so contact never steps
    private readonly float[] leanTouch = { -10f, -10f };        // when each glove last reported a lean

    // ---------------------------------------------------------------- Lifecycle

    private void Awake()
    {
        BuildRig();
        if (deformMesh)
        {
            deformer = GetComponent<BagDeformer>();
            if (deformer == null) deformer = gameObject.AddComponent<BagDeformer>();
        }
    }

    private void Update()
    {
        UpdateChainRenderer();
        if (visual == null) return;

        // Damped spring: a hit kicks 'squash' positive (compressed), it overshoots and rings back to rest.
        float dt = Time.deltaTime;
        squashVelocity += (-squashSpring * squash - squashDamping * squashVelocity) * dt;
        squash = Mathf.Clamp(squash + squashVelocity * dt, -0.5f, 0.5f);

        visual.localScale = new Vector3(
            visualBaseScale.x * (1f - squash),
            visualBaseScale.y * (1f + squash * 0.5f),
            visualBaseScale.z * (1f - squash));
    }

    private void FixedUpdate()
    {
        if (!IsBuilt) return;

        // Spin friction: sand grinding inside the shell. Plain angular damping also brakes the swing, which is
        // the chain's job — this only brakes rotation AROUND the chain, so a spun bag settles without deadening
        // the pendulum.
        if (spinDrag > 0f)
        {
            Vector3 up = transform.up;
            float twist = Vector3.Dot(Body.angularVelocity, up);
            Body.AddTorque(-up * (twist * spinDrag), ForceMode.Acceleration);
        }

        // Lean forces fade out on their own once a glove stops reporting (no OnTriggerExit races) — while it IS
        // reporting, Lean() owns the value and this must not fight the ramp-in.
        for (int i = 0; i < 2; i++)
            if (Time.time - leanTouch[i] > 0.05f)
                leanForce[i] = Vector3.MoveTowards(leanForce[i], Vector3.zero, leanMaxForce * 4f * Time.fixedDeltaTime);
    }

    /// <summary>The physics chain, made visible: a whipping line from the mount through the links to the bag's top.</summary>
    private void UpdateChainRenderer()
    {
        if (!renderChain || !IsBuilt || mount == null) return;

        if (chainLine == null)
        {
            GameObject go = new GameObject(name + " Chain Visual");
            go.transform.SetParent(transform, false);
            chainLine = go.AddComponent<LineRenderer>();
            chainLine.useWorldSpace = true;
            chainLine.startWidth = chainWidth;
            chainLine.endWidth = chainWidth;
            chainLine.numCapVertices = 2;
            chainLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            Material m = new Material(shader) { name = "Bag Chain (runtime)" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", chainColor);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", chainColor);
            chainLine.material = m;
        }

        int points = chainLinks.Count + 2;
        chainLine.positionCount = points;
        chainLine.SetPosition(0, mount.position);
        for (int i = 0; i < chainLinks.Count; i++)
            chainLine.SetPosition(i + 1, chainLinks[i] != null ? chainLinks[i].position : mount.position);
        Vector3 bagTop = BagCollider != null
            ? new Vector3(BagCollider.bounds.center.x, BagCollider.bounds.max.y - 0.03f, BagCollider.bounds.center.z)
            : transform.position;
        chainLine.SetPosition(points - 1, bagTop);
    }

    // ---------------------------------------------------------------- The energy budget

    /// <summary>
    /// The exact mass a punch "feels" at a contact point: translation plus the rotation the off-centre hit buys.
    /// m_p = 1 / (1/m + ((I⁻¹ (r × n)) × r) · n). Hitting the middle feels like the whole bag; hitting the
    /// bottom feels lighter because the bag can rotate away. This is what makes the surface-speed clamp honest —
    /// and it is also why the same clamp works unchanged on a 1x or a 2x-scale bag.
    /// </summary>
    public float PointEffectiveMass(Vector3 point, Vector3 direction)
    {
        if (Body == null) return bagMass;
        Vector3 n = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
        Vector3 r = point - Body.worldCenterOfMass;
        Vector3 rxn = Vector3.Cross(r, n);

        Quaternion toWorld = Body.rotation * Body.inertiaTensorRotation;
        Vector3 tensor = Body.inertiaTensor;
        Vector3 local = Quaternion.Inverse(toWorld) * rxn;
        Vector3 invLocal = new Vector3(
            tensor.x > 1e-6f ? local.x / tensor.x : 0f,
            tensor.y > 1e-6f ? local.y / tensor.y : 0f,
            tensor.z > 1e-6f ? local.z / tensor.z : 0f);
        Vector3 angular = toWorld * invLocal;

        float denom = 1f / Mathf.Max(0.01f, Body.mass) + Vector3.Dot(Vector3.Cross(angular, r), n);
        return denom > 1e-5f ? 1f / denom : Body.mass;
    }

    /// <summary>
    /// The most impulse a hit at this point, in this direction, is allowed to deliver right now: enough to bring
    /// the surface up to <see cref="maxSurfaceSpeed"/> and not one newton-second more, and never past
    /// <see cref="maxImpulse"/>. A bag already swinging away absorbs less (it is running from the punch); a bag
    /// swinging INTO the punch may take more, because arresting it eats impulse before any speed is added.
    /// </summary>
    public float ImpulseHeadroom(Vector3 point, Vector3 direction, float overdrive = 0f)
    {
        if (!IsBuilt) return 0f;
        Vector3 n = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
        float pointMass = PointEffectiveMass(point, n);
        float current = Vector3.Dot(Body.GetPointVelocity(point), n);
        // Overdrive BUYS surface speed: a fully loaded cross may push the leather 25% past a jab's ceiling,
        // so heaviness keeps differentiating at the top end. The hard caps still bound everything after.
        float ceiling = maxSurfaceSpeed * (1f + 0.25f * Mathf.Clamp01(overdrive));
        float headroom = Mathf.Max(0f, ceiling - current) * pointMass;
        return Mathf.Min(headroom, maxImpulse);
    }

    // ---------------------------------------------------------------- Public API

    private bool warnedDeformer;

    /// <summary>
    /// Penetration into the LEATHER THE PLAYER SEES — the collider depth, minus the pocket currently carved at
    /// that point, plus however far the squash ring-back is bulging the skin OUT past the collider. Glove
    /// containment measures against THIS; hit detection stays on the plain collider <see cref="Penetration"/>.
    /// </summary>
    public float VisualPenetration(Vector3 point, out Vector3 surface, out Vector3 inward)
    {
        float depth = Penetration(point, out surface, out inward);
        if (deformer != null && deformer.IsReady) depth -= deformer.CarveDepthAt(point);
        if (visual != null && squash < 0f)
        {
            // The squash spring's overshoot scales the skin outward past the collider along the radial axes —
            // exactly while a fist is planted. Without this term the bulge silently swallowed the glove.
            float radial = Vector3.ProjectOnPlane(point - visual.position, Vector3.up).magnitude;
            depth += -squash * radial;
        }
        return depth;
    }

    /// <summary>
    /// Punch the bag. <paramref name="impulse"/> is the momentum the sensor computed in N·s — the bag clamps it
    /// through its own energy budget before applying it, so a bad frame upstream can bend the number but never
    /// break the bag. Returns the impulse actually applied.
    /// </summary>
    public float Hit(Vector3 point, Vector3 direction, float impulse, float cleanliness = 1f, float speed = 0f,
                     int hand = -1, float overdrive = 0f)
    {
        if (!IsBuilt) return 0f;
        if (direction.sqrMagnitude < 0.0001f) direction = transform.forward;
        direction.Normalize();
        cleanliness = Mathf.Clamp01(cleanliness);

        // Sampled BEFORE the impulse goes in: a bag closing on the fist is a COUNTER, a bag standing dead
        // still is the canvas for a pure hit — after AddForce these numbers would contain the punch itself.
        Vector3 preVelocity = Body.GetPointVelocity(point);
        float closing = Mathf.Max(0f, -Vector3.Dot(preVelocity, direction));
        float surfaceSpeed = preVelocity.magnitude;

        // THE CLAMP. Everything above this line is a request; below it, the MOTION is physics — but FEEDBACK
        // is not motion. An armed fist that reached the leather always sounds and shows: when the bag is
        // already fleeing at its speed ceiling and can absorb no more momentum, the hit lands SOFT — never
        // silent. (A visibly planted glove with zero sound, zero VFX and zero dent was the game's worst
        // intermittent feel bug, and it fired on every second punch of a rhythmic flurry.)
        float requested = Mathf.Min(Mathf.Max(0f, impulse), maxImpulse);
        impulse = Mathf.Clamp(requested, 0f, ImpulseHeadroom(point, direction, overdrive));

        if (impulse > 0.01f)
        {
            Body.AddForceAtPosition(direction * impulse, point, ForceMode.Impulse);

            // A glancing blow SHEARS the bag around the strap instead of swinging it — the fold you see in
            // real gyms. The hard velocity caps bound it like every other energy input.
            float glance = 1f - cleanliness;
            if (glance > 0.15f)
            {
                Vector3 axis = Vector3.Cross(point - Body.worldCenterOfMass, direction);
                if (axis.sqrMagnitude > 0.0001f)
                    Body.AddTorque(axis.normalized * (impulse * 0.12f * glance), ForceMode.Impulse);
            }

            // Bank the follow-through: the shove after contact spends what the punch earned, nothing more.
            if (hand == 0 || hand == 1)
                followBudget[hand] = impulse * followThroughFraction;
        }

        // Feedback strength: what was DELIVERED, floored by what the punch honestly WAS — a hard shot into a
        // receding bag still cracks (quieter, softer), it never mutes.
        float strength = Mathf.Clamp01(Mathf.Max(impulse, 0.35f * requested) / fullPunchImpulse);

        // The chain takes the hit as a SHOCK climbing link by link to the mount — causal, not noise glued on.
        if (chainRattle > 0f && chainLinks.Count > 0)
            StartCoroutine(ChainShock(direction, strength));

        bool dents = deformer != null && deformer.IsReady;
        if (!dents && !warnedDeformer)
        {
            warnedDeformer = true;
            Debug.LogWarning($"PunchingBag '{name}': hits land but the mesh cannot dent " +
                             $"({(deformer == null ? "no BagDeformer" : "deformer not ready — see its console line from scene load")}).", this);
        }
        // The whole-bag squash is the deformation that reads from ANY angle on any shader — barely muted now
        // even when dents work (the old 0.4x cut left a ~2% scale pop nobody could see).
        squash += squashAmount * strength * Mathf.Lerp(0.6f, 1f, cleanliness) * (dents ? 0.75f : 1f);
        if (dents)
        {
            deformer.Dent(point, direction, dentDepth * Mathf.Lerp(0.35f, 1f, strength) * Mathf.Lerp(0.6f, 1f, cleanliness), dentRadius);
            if (rippleOnHit && strength > 0.2f) deformer.Ripple(point, direction, strength);
        }

        HitInfo info = new HitInfo
        {
            point = point, direction = direction, impulse = impulse, strength = strength,
            cleanliness = cleanliness, speed = speed, hand = hand, overdrive = Mathf.Clamp01(overdrive),
            closing = closing, surfaceSpeed = surfaceSpeed,
        };
        LastHit = info;
        OnHit.Invoke(this, info);
        return impulse;
    }

    /// <summary>
    /// The fist keeps pushing while it is planted in the bag — but only with energy banked by the hit itself.
    /// Call every FixedUpdate during the plant; it drains the budget and stops on its own.
    /// </summary>
    public void FollowThrough(int hand, Vector3 point, Vector3 direction, float dt)
    {
        if (!IsBuilt || hand < 0 || hand > 1 || dt <= 0f) return;
        float budget = followBudget[hand];
        if (budget <= 0.01f) return;
        if (direction.sqrMagnitude < 0.0001f) return;
        direction.Normalize();

        // Spend the reservoir over ~0.12 s of plant, respecting 90% of the surface-speed ceiling: the last
        // 10% stays in RESERVE so a follow-up punch always finds headroom for a real, audible impulse —
        // follow-through topping the surface up to exactly the ceiling was what muted rhythmic flurries.
        float spend = Mathf.Min(budget, budget * dt / 0.12f + 0.5f * dt);
        spend = Mathf.Min(spend, ReserveHeadroom(point, direction));
        if (spend <= 0f) { followBudget[hand] = 0f; return; }

        Body.AddForceAtPosition(direction * (spend / dt), point, ForceMode.Force);
        followBudget[hand] = budget - spend;
    }

    /// <summary>Shake the chain from outside (the pure-hit ceremony) — routed through the same capped shock.</summary>
    public void RattleChain(Vector3 direction, float strength)
    {
        if (!IsBuilt || chainRattle <= 0f || chainLinks.Count == 0) return;
        StartCoroutine(ChainShock(direction, Mathf.Clamp01(strength)));
    }

    /// <summary>Headroom against a reduced (90%) speed ceiling — the most follow-through may spend.</summary>
    private float ReserveHeadroom(Vector3 point, Vector3 direction)
    {
        if (!IsBuilt) return 0f;
        Vector3 n = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
        float current = Vector3.Dot(Body.GetPointVelocity(point), n);
        return Mathf.Min(Mathf.Max(0f, maxSurfaceSpeed * 0.9f - current) * PointEffectiveMass(point, n), maxImpulse);
    }

    /// <summary>The hit's jolt runs UP the chain one link per physics step — a visible wave to the mount.</summary>
    private System.Collections.IEnumerator ChainShock(Vector3 direction, float strength)
    {
        for (int i = chainLinks.Count - 1; i >= 0; i--)
        {
            Rigidbody link = chainLinks[i];
            if (link != null)
            {
                link.AddForce(direction * (chainRattle * 2f * strength), ForceMode.VelocityChange);
                link.AddTorque(UnityEngine.Random.insideUnitSphere * (chainRattle * 0.8f * strength), ForceMode.VelocityChange);
            }
            yield return new WaitForFixedUpdate();
        }
    }

    /// <summary>
    /// A glove pressed into the bag WITHOUT a punch behind it: guard raised against the leather, walking into
    /// it, a hand parked after a strike. A smooth, capped spring force eases the bag away — never an impulse,
    /// which is the whole reason L1/R1 no longer send the bag across the gym. Call every physics step while
    /// touching (id = 0 left glove, 1 right); it keeps the dent under the glove alive too.
    /// </summary>
    public void Lean(int id, Vector3 point, Vector3 inward, float depth, Vector3 gloveVelocity)
    {
        if (!IsBuilt || depth <= 0f) return;
        if (inward.sqrMagnitude < 0.0001f) return;
        inward.Normalize();

        float closing = Mathf.Max(0f, Vector3.Dot(gloveVelocity - Body.GetPointVelocity(point), inward));
        float magnitude = Mathf.Min(leanSpring * Mathf.Min(depth, 0.15f) + leanDamper * closing, leanMaxForce);

        // Smoothed so a glove that appears suddenly inside the bag ramps its push in over a few steps.
        int slot = Mathf.Clamp(id, 0, 1);
        Vector3 target = inward * magnitude;
        leanForce[slot] = Vector3.MoveTowards(leanForce[slot], target, leanMaxForce * 8f * Time.fixedDeltaTime);
        leanTouch[slot] = Time.time;
        Body.AddForceAtPosition(leanForce[slot], point, ForceMode.Force);

        SetContact(id, point, inward, Mathf.Min(depth, 0.06f));
    }

    /// <summary>
    /// The bag met a raised guard: reflect the surface velocity off the gloves. Restitution ~0.35 is leather on
    /// leather; a perfect block can use more and the bag visibly jumps back off the boxer. Capped like a hit.
    /// </summary>
    public void Bounce(Vector3 point, Vector3 outDirection, float restitution)
    {
        if (!IsBuilt) return;
        if (outDirection.sqrMagnitude < 0.0001f) return;
        outDirection.Normalize();

        float into = Mathf.Max(0f, Vector3.Dot(Body.GetPointVelocity(point), -outDirection));
        if (into < 0.15f) return;

        float pointMass = PointEffectiveMass(point, outDirection);
        float impulse = Mathf.Min(into * (1f + Mathf.Clamp01(restitution)) * pointMass, maxImpulse);
        Body.AddForceAtPosition(outDirection * impulse, point, ForceMode.Impulse);
    }

    private Coroutine flashRoutine;
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    /// <summary>Flash the bag's material emission (perfect-hit ceremony). Safe on any shader with _EmissionColor.</summary>
    public void Flash(Color color, float seconds)
    {
        if (visual == null) return;
        Renderer flashRenderer = visual.GetComponent<Renderer>();
        if (flashRenderer == null || !flashRenderer.material.HasProperty(EmissionColorId)) return;
        if (flashRoutine != null) StopCoroutine(flashRoutine);
        flashRoutine = StartCoroutine(FlashRoutine(flashRenderer, color, seconds));
    }

    private System.Collections.IEnumerator FlashRoutine(Renderer flashRenderer, Color color, float seconds)
    {
        Material m = flashRenderer.material;
        m.EnableKeyword("_EMISSION");
        Color original = m.GetColor(EmissionColorId);
        float end = Time.unscaledTime + seconds;
        while (Time.unscaledTime < end)
        {
            float k = Mathf.InverseLerp(end, end - seconds, Time.unscaledTime);   // 1 at the start, fading to 0
            m.SetColor(EmissionColorId, Color.Lerp(original, color, k));
            yield return null;
        }
        m.SetColor(EmissionColorId, original);
        flashRoutine = null;
    }

    /// <summary>A glove pressed into the bag: keep a live dent under it (id = 0 left, 1 right). Call every frame while touching.</summary>
    public void SetContact(int id, Vector3 point, Vector3 inward, float depth)
    {
        if (deformer != null && deformer.IsReady) deformer.SetContact(id, point, inward, depth, dentRadius * 0.9f);
    }

    public void ClearContact(int id)
    {
        if (deformer != null && deformer.IsReady) deformer.ClearContact(id);
    }

    /// <summary>
    /// How deep a world point is inside the bag (metres, ≤ 0 = outside), with the nearest surface point and the
    /// inward direction. Exact for the fitted capsule, approximate for other colliders.
    /// </summary>
    public float Penetration(Vector3 point, out Vector3 surfacePoint, out Vector3 inward)
    {
        if (BagCollider is CapsuleCollider c)
        {
            Transform ct = c.transform;
            Vector3 s = ct.lossyScale;
            Vector3 axisLocal = c.direction == 0 ? Vector3.right : (c.direction == 1 ? Vector3.up : Vector3.forward);
            float axisScale = c.direction == 0 ? Mathf.Abs(s.x) : (c.direction == 1 ? Mathf.Abs(s.y) : Mathf.Abs(s.z));
            float radialScale = c.direction == 0 ? Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)) : (c.direction == 1 ? Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z)) : Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y)));
            float radius = c.radius * radialScale;
            float half = Mathf.Max(0f, c.height * axisScale * 0.5f - radius);
            Vector3 center = ct.TransformPoint(c.center);
            Vector3 axis = ct.TransformDirection(axisLocal).normalized;
            Vector3 a = center - axis * half, b = center + axis * half;
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude) : 0f;
            Vector3 q = a + ab * t;
            Vector3 toPoint = point - q;
            float d = toPoint.magnitude;
            Vector3 outward = d > 1e-4f ? toPoint / d : -transform.forward;
            inward = -outward;
            surfacePoint = q + outward * radius;
            return radius - d;
        }

        surfacePoint = ClosestPoint(point);
        Vector3 centre = BagCollider != null ? BagCollider.bounds.center : transform.position;
        inward = (centre - point).sqrMagnitude > 1e-6f ? (centre - point).normalized : -transform.forward;
        float distance = Vector3.Distance(surfacePoint, point);
        return distance < 1e-4f ? 0.05f : -distance;
    }

    /// <summary>Closest point on the bag's surface to <paramref name="position"/> (falls back to the transform).</summary>
    public Vector3 ClosestPoint(Vector3 position)
    {
        return BagCollider != null ? BagCollider.ClosestPoint(position) : transform.position;
    }

    /// <summary>Velocity of the bag surface at a world point (accounts for spin).</summary>
    public Vector3 PointVelocity(Vector3 point)
    {
        return Body != null ? Body.GetPointVelocity(point) : Vector3.zero;
    }

    /// <summary>Stops all swinging instantly (e.g. round reset).</summary>
    public void Settle()
    {
        if (!IsBuilt) return;
        Body.linearVelocity = Vector3.zero;
        Body.angularVelocity = Vector3.zero;
        foreach (Rigidbody link in chainLinks)
        {
            if (link == null) continue;
            link.linearVelocity = Vector3.zero;
            link.angularVelocity = Vector3.zero;
        }
        squash = 0f;
        squashVelocity = 0f;
        followBudget[0] = followBudget[1] = 0f;
    }

    // ---------------------------------------------------------------- Hitting the boxer back

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsBuilt) return;

        // Active-ragdoll puppet parts are separate objects tagged with their owner; sensor gloves live under the
        // boxer. Searched up the parents because a PuppetMaster muscle can carry its collider on a child, and
        // the muscles are NOT under the character — falling back to the boxer's hierarchy would find nothing.
        PhysicsPuppetPart part = collision.collider.GetComponentInParent<PhysicsPuppetPart>();
        BoxerPunchController boxer = part != null ? part.Owner : collision.collider.GetComponentInParent<BoxerPunchController>();
        if (boxer == null) return;

        // A glove that is throwing a punch is hitting the bag, not being hit by it.
        if (part != null && part.IsHand && part.Fist != null && part.Fist.Armed) return;

        Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : collision.collider.bounds.center;
        Vector3 bagVelocity = Body.GetPointVelocity(point);
        if (bagVelocity.magnitude < minHitBackSpeed) return;

        bool glove = part != null ? part.IsHand : collision.collider.GetComponentInParent<PunchHitbox>() != null;
        bool head = part != null && part.IsHead;
        boxer.ReceiveBagContact(this, point, bagVelocity, collision.impulse.magnitude, glove, head);
    }

    // ---------------------------------------------------------------- Rig construction

    [ContextMenu("Build Rig")]
    public void BuildRig()
    {
        if (IsBuilt) return;

        // Already built in edit mode? Just pick up the pieces.
        ConfigurableJoint existingJoint = GetComponent<ConfigurableJoint>();
        if (existingJoint != null && existingJoint.connectedBody != null)
        {
            Body = GetComponent<Rigidbody>();
            BagCollider = GetComponent<Collider>();
            CollectExistingChain(existingJoint);
            ApplyBodyCaps(Body);
            ResolveVisual();
            return;
        }

        Bounds bounds = GetRenderBounds();
        Vector3 topCenter = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);

        // --- Mount (kinematic, never moves unless you move it) ---
        if (mount == null)
        {
            mount = new GameObject(name + " Mount").transform;
            mount.SetPositionAndRotation(topCenter + Vector3.up * chainLength, Quaternion.identity);
        }
        Rigidbody mountBody = mount.GetComponent<Rigidbody>();
        if (mountBody == null) mountBody = mount.gameObject.AddComponent<Rigidbody>();
        mountBody.isKinematic = true;
        mountBody.useGravity = false;

        // --- Chain: small bodies hanging from the mount, each pivoting at its top ---
        Vector3 chainDir = topCenter - mount.position;
        float totalLength = chainDir.magnitude;
        if (totalLength < 0.01f) { chainDir = Vector3.down; totalLength = chainLength; }
        chainDir /= totalLength;
        float segmentLength = totalLength / chainSegments;
        Quaternion linkRotation = Quaternion.FromToRotation(Vector3.down, chainDir);

        chainLinks.Clear();
        Rigidbody previous = mountBody;
        Vector3 previousBottom = Vector3.zero;                     // local to 'previous'
        for (int i = 0; i < chainSegments; i++)
        {
            GameObject link = new GameObject(name + " Chain " + (i + 1));
            link.transform.SetParent(mount, false);
            link.transform.SetPositionAndRotation(mount.position + chainDir * (segmentLength * i), linkRotation);

            Rigidbody linkBody = link.AddComponent<Rigidbody>();
            linkBody.mass = Mathf.Max(0.5f, bagMass * 0.02f);
            linkBody.linearDamping = 0.1f;
            linkBody.angularDamping = 0.6f;
            linkBody.interpolation = RigidbodyInterpolation.Interpolate;
            linkBody.solverIterations = 16;
            linkBody.solverVelocityIterations = 8;
            linkBody.maxDepenetrationVelocity = hardVelocityCaps.z;
            linkBody.maxLinearVelocity = hardVelocityCaps.x * 2f;

            ConfigurableJoint joint = link.AddComponent<ConfigurableJoint>();
            ConfigureSwingJoint(joint, previous, Vector3.zero, previousBottom, maxSwingAngle, maxTwist);

            chainLinks.Add(linkBody);
            previous = linkBody;
            previousBottom = new Vector3(0f, -segmentLength, 0f);
        }

        // --- Bag body ---
        Body = GetComponent<Rigidbody>();
        if (Body == null) Body = gameObject.AddComponent<Rigidbody>();
        Body.mass = bagMass;
        Body.linearDamping = airDrag;
        Body.angularDamping = 1.1f;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        Body.solverIterations = 16;
        Body.solverVelocityIterations = 8;
        ApplyBodyCaps(Body);

        BagCollider = GetComponent<Collider>();
        if (BagCollider == null && autoFitCollider) BagCollider = FitCapsule(GetBagBounds());   // the bag body only — never the chain links
        if (BagCollider == null)
        {
            Debug.LogWarning("PunchingBag: no collider on the bag root and Auto Fit Collider is off; punches cannot land.", this);
        }
        else
        {
            if (BagCollider is MeshCollider mesh && !mesh.convex)
            {
                mesh.convex = true; // non-convex mesh colliders cannot be on a dynamic rigidbody
            }
            if (bagMaterial == null)
            {
                bagMaterial = new PhysicsMaterial("Punching Bag")
                {
                    dynamicFriction = 0.7f,
                    staticFriction = 0.7f,
                    bounciness = 0f,
                    frictionCombine = PhysicsMaterialCombine.Average,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
            }
            BagCollider.material = bagMaterial;

            // Sand settles: shift the centre of mass below the middle. This is what gives a real bag its
            // signature motion — the bottom kicks out under a body shot while the top stays with the chain.
            if (bottomHeaviness > 0f && BagCollider is CapsuleCollider capsule)
            {
                float drop = capsule.height * bottomHeaviness;
                Body.centerOfMass = capsule.center + Vector3.down * drop;
            }
        }

        ConfigurableJoint bagJoint = gameObject.AddComponent<ConfigurableJoint>();
        ConfigureSwingJoint(bagJoint, previous, transform.InverseTransformPoint(topCenter), previousBottom, maxBagTilt, maxTwist);

        ResolveVisual();
    }

    /// <summary>The PhysX-level ceilings. These hold even for contacts no script ever sees.</summary>
    private void ApplyBodyCaps(Rigidbody body)
    {
        if (body == null) return;
        body.maxLinearVelocity = hardVelocityCaps.x;
        body.maxAngularVelocity = hardVelocityCaps.y;
        body.maxDepenetrationVelocity = hardVelocityCaps.z;
    }

    [ContextMenu("Remove Rig")]
    public void RemoveRig()
    {
        foreach (ConfigurableJoint joint in GetComponents<ConfigurableJoint>()) DestroyNow(joint);
        if (Body != null) DestroyNow(Body);
        else { Rigidbody rb = GetComponent<Rigidbody>(); if (rb != null) DestroyNow(rb); }
        if (autoFitCollider) { CapsuleCollider capsule = GetComponent<CapsuleCollider>(); if (capsule != null) DestroyNow(capsule); }

        if (mount != null)
        {
            for (int i = mount.childCount - 1; i >= 0; i--)
            {
                Transform child = mount.GetChild(i);
                if (child.name.StartsWith(name + " Chain")) DestroyNow(child.gameObject);
            }
            if (mount.name == name + " Mount") { DestroyNow(mount.gameObject); mount = null; }
            else { Rigidbody mountBody = mount.GetComponent<Rigidbody>(); if (mountBody != null) DestroyNow(mountBody); }
        }

        chainLinks.Clear();
        Body = null;
        BagCollider = null;
        if (visual != null) visual.localScale = visualBaseScale;
    }

    private void ConfigureSwingJoint(ConfigurableJoint joint, Rigidbody connected, Vector3 anchor, Vector3 connectedAnchor,
        float swingLimit, float twistLimit)
    {
        joint.connectedBody = connected;
        joint.autoConfigureConnectedAnchor = false;
        joint.anchor = anchor;
        joint.connectedAnchor = connectedAnchor;

        // Twist axis runs along the chain (local Y); the two swing axes are the horizontal ones.
        joint.axis = Vector3.up;
        joint.secondaryAxis = Vector3.right;

        joint.xMotion = ConfigurableJointMotion.Locked;
        joint.yMotion = ConfigurableJointMotion.Locked;
        joint.zMotion = ConfigurableJointMotion.Locked;

        joint.angularXMotion = twistLimit > 0f ? ConfigurableJointMotion.Limited : ConfigurableJointMotion.Locked;
        joint.angularYMotion = swingLimit > 0f ? ConfigurableJointMotion.Limited : ConfigurableJointMotion.Locked;
        joint.angularZMotion = swingLimit > 0f ? ConfigurableJointMotion.Limited : ConfigurableJointMotion.Locked;

        SoftJointLimit twist = new SoftJointLimit { limit = twistLimit, bounciness = 0f, contactDistance = 0f };
        joint.lowAngularXLimit = new SoftJointLimit { limit = -twistLimit, bounciness = 0f, contactDistance = 0f };
        joint.highAngularXLimit = twist;
        joint.angularYLimit = new SoftJointLimit { limit = swingLimit, bounciness = 0f, contactDistance = 0f };
        joint.angularZLimit = new SoftJointLimit { limit = swingLimit, bounciness = 0f, contactDistance = 0f };

        // Soft limits so the chain doesn't clack against a hard stop.
        SoftJointLimitSpring limitSpring = new SoftJointLimitSpring { spring = 400f, damper = 40f };
        joint.angularXLimitSpring = limitSpring;
        joint.angularYZLimitSpring = limitSpring;

        // Restoring spring + damper towards "hanging straight" (the rotation at build time). Gravity is the real
        // restorer; the damper is the strap friction that lets the bag settle instead of swinging for a minute.
        joint.rotationDriveMode = RotationDriveMode.Slerp;
        joint.slerpDrive = new JointDrive { positionSpring = swingSpring, positionDamper = swingDamper, maximumForce = 1000f };
        joint.targetRotation = Quaternion.identity;

        joint.enableCollision = false;
        joint.enablePreprocessing = false;
        // Projection snaps a drifted joint back into place. Kept loose: a tight projection under a hard hit
        // teleports the link, and a teleport is free energy — part of how the old bag got launched.
        joint.projectionMode = JointProjectionMode.PositionAndRotation;
        joint.projectionDistance = 0.08f;
        joint.projectionAngle = 15f;
    }

    private CapsuleCollider FitCapsule(Bounds worldBounds)
    {
        // Bounds are world-space; convert to this transform's local space (accounts for the ×30-style FBX scales).
        Vector3 scale = transform.lossyScale;
        Vector3 localSize = new Vector3(
            worldBounds.size.x / Mathf.Max(0.0001f, Mathf.Abs(scale.x)),
            worldBounds.size.y / Mathf.Max(0.0001f, Mathf.Abs(scale.y)),
            worldBounds.size.z / Mathf.Max(0.0001f, Mathf.Abs(scale.z)));

        CapsuleCollider capsule = gameObject.AddComponent<CapsuleCollider>();
        capsule.direction = 1; // Y
        capsule.center = transform.InverseTransformPoint(worldBounds.center);
        capsule.radius = Mathf.Max(localSize.x, localSize.z) * 0.5f * colliderScale;
        capsule.height = Mathf.Max(localSize.y, capsule.radius * 2f);
        return capsule;
    }

    private Bounds GetRenderBounds()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            Debug.LogWarning("PunchingBag: no renderer found under the bag; using a 1 m fallback size.", this);
            return new Bounds(transform.position, new Vector3(0.35f, 1f, 0.35f));
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    /// <summary>
    /// World bounds of the bag BODY: the biggest renderer under the bag. Bag models usually carry their chain links
    /// as extra meshes; fitting the collider (and aiming) around those put the capsule's centre up in the chain.
    /// </summary>
    private Bounds GetBagBounds()
    {
        Renderer body = LargestRenderer();
        return body != null ? body.bounds : GetRenderBounds();
    }

    private Renderer LargestRenderer()
    {
        Renderer best = null;
        float bestVolume = -1f;
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            Vector3 s = r.bounds.size;
            float volume = s.x * s.y * s.z;
            if (volume > bestVolume) { bestVolume = volume; best = r; }
        }
        return best;
    }

    private void CollectExistingChain(ConfigurableJoint bagJoint)
    {
        chainLinks.Clear();
        Rigidbody body = bagJoint.connectedBody;
        while (body != null && !body.isKinematic)
        {
            chainLinks.Add(body);
            ConfigurableJoint next = body.GetComponent<ConfigurableJoint>();
            body = next != null ? next.connectedBody : null;
        }
    }

    private void ResolveVisual()
    {
        if (visual == transform) visual = null;
        if (visual == null)
        {
            Renderer body = LargestRenderer();   // the bag itself, not the first chain link in the hierarchy
            if (body != null && body.transform != transform) visual = body.transform;
        }
        if (visual != null) visualBaseScale = visual.localScale;
    }

    private static void DestroyNow(UnityEngine.Object obj)
    {
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }

    private void OnDrawGizmosSelected()
    {
        Bounds all = GetRenderBounds();
        Vector3 topCenter = new Vector3(all.center.x, all.max.y, all.center.z);
        Vector3 mountPos = mount != null ? mount.position : topCenter + Vector3.up * chainLength;

        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.9f);
        Gizmos.DrawLine(mountPos, topCenter);
        Gizmos.DrawWireSphere(mountPos, 0.05f);

        // The physics bag (body only): match this to the visible bag with Collider Scale (before building the rig).
        Bounds bounds = GetBagBounds();
        Vector3 scale = transform.lossyScale;
        float radius = Mathf.Max(bounds.size.x, bounds.size.z) * 0.5f * colliderScale;
        if (BagCollider is CapsuleCollider c) radius = c.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        Gizmos.color = new Color(1f, 0.9f, 0.1f, 1f);
        Gizmos.DrawWireSphere(new Vector3(bounds.center.x, bounds.max.y - radius, bounds.center.z), radius);
        Gizmos.DrawWireSphere(bounds.center, radius);
        Gizmos.DrawWireSphere(new Vector3(bounds.center.x, bounds.min.y + radius, bounds.center.z), radius);
    }
}
