using System;
using System.Collections.Generic;
using RootMotion;
using RootMotion.Dynamics;
using RootMotion.FinalIK;
using UnityEngine;

/// <summary>
/// THE PHYSICS BOXER (req.md). Bennett stops being an animation that happens to have colliders and becomes a
/// body: PuppetMaster's muscles chase the animated / IK'd reference pose, PhysX owns momentum, joint limits and
/// every contact, and what you see is the ragdoll mapped back onto the mesh.
///
///     stick / trigger        →  BoxerPunchController: intent (where this fist wants to be, how fast)
///     Animator + Final IK    →  the REFERENCE pose — the shape the body is trying to make
///     THIS component         →  the "Boxing Motor" (req.md §36): how hard each muscle group tries, when,
///                               and what the hips and chest contribute — the kinetic chain
///     PuppetMaster (Active)  →  ConfigurableJoint muscles pulling the ragdoll toward that pose
///     PhysX                  →  momentum · collisions · the bag stopping the fist · the guard being shoved
///     PhysicalFist           →  reads the REAL contact: relative velocity, squareness, effective mass
///
/// Nothing here computes damage from input. Input decides how hard the body *tries*; the collision decides what
/// actually happened (req.md §6, §16).
///
/// WHY ACTIVE MODE USED TO DEFORM THE BODY, and what fixes it:
///   <see cref="IKExecutionOrder"/> DISABLES the Final IK components in Start and solves them itself in
///   LateUpdate. But <see cref="SolverManager.UpdateSolverExternal"/> — the call PuppetMaster makes to solve IK
///   inside FixedUpdate, right before it reads the target pose — begins with "if (!enabled) return". So in
///   Active mode the puppet was pinned to a pose that had never had IK applied, and the IK then ran afterwards
///   on top of the mapped result: two different bodies fighting, every frame. This component takes the
///   conductor's baton off IKExecutionOrder, re-enables the solvers, and orders PuppetMaster's own solver list
///   (LookAt first, then FullBodyBiped), so the chain is animate → IK → read → simulate → map. Once.
///
/// Three watchdogs cover the failure modes we could not diagnose from memory (stretch, sag, wrong pose): see
/// <see cref="Stretch"/> and the Safety section. The character never gets to look broken — the worst case is
/// that he quietly falls back to looking exactly like the animation.
///
/// Driven by <see cref="BoxerPunchController"/> (Use Physics Body, Backend = Puppet Master).
/// Set up with Tools ▸ Boxer ▸ Physics ▸ Setup Physics Boxer.
/// </summary>
[DefaultExecutionOrder(-5)]
[DisallowMultipleComponent]
public class BoxerPhysics : MonoBehaviour, IBoxerBody
{
    private enum Role { Hips, Spine, Head, UpperArm, Forearm, Hand, Leg, Foot, Other }

    // ------------------------------------------------------------------ Inspector

    [Header("Puppet")]
    [Tooltip("The PuppetMaster driving this character. Auto-found by its Target Root when empty.")]
    [SerializeField] private PuppetMaster puppetMaster;

    [Tooltip("Physics steps per second. Boxing wants 100+ — a 9 m/s glove crosses 18 cm in a 50 Hz step and can " +
             "miss the bag entirely (req.md §17). 0 = leave the project setting alone.")]
    [SerializeField] private int physicsRate = 100;

    [Tooltip("Take the Final IK solvers off IKExecutionOrder and let PuppetMaster solve them in FixedUpdate, in " +
             "the right order, before it reads the pose. THIS is what stops Active mode deforming the body — " +
             "only turn it off if you know why you are doing it.")]
    [SerializeField] private bool puppetOwnsIK = true;

    [Header("Muscles — how hard the whole puppet holds its pose")]
    [Tooltip("Slerp drive spring. PuppetMaster's default 100 is for a 1:1-scale character standing around; a " +
             "boxer holding a guard against impacts needs far more or he sags.")]
    [Min(0f)] [SerializeField] private float muscleSpring = 500f;
    [Min(0f)] [SerializeField] private float muscleDamper = 10f;

    [Tooltip("How quickly pinning falls off with distance. Low = the puppet is dragged back to the animation " +
             "hard (crisp, game-y); high = loose and sloppy. 0.5 keeps punches on target.")]
    [Range(0f, 100f)] [SerializeField] private float pinDistanceFalloff = 0.5f;
    [Tooltip("Pin force is raised to this power, so it is FAR more nonlinear than it looks: at 4, a pin of 0.35 " +
             "is 0.35^4 = 0.015 — essentially unpinned. That is what let the head drift and stretch the neck. 2 " +
             "keeps mid values meaningful.")]
    [Range(1f, 8f)] [SerializeField] private float pinPow = 2f;

    [Tooltip("Scale the muscle spring by the character's own scale squared. Bennett is 2x, and a puppet tuned " +
             "for a 1x character simply cannot hold him up.")]
    [SerializeField] private bool autoScaleToCharacter = true;

    [Tooltip("Also pin rotation, not just position. Costs a little; keeps a fast glove square to its target.")]
    [SerializeField] private bool angularPinning = true;

    [Tooltip("Joint angular limits. On = elbows and knees cannot hyperextend. Off = PuppetMaster relies purely " +
             "on the muscle drives (looser, and a hard shot can bend an arm the wrong way).")]
    [SerializeField] private bool angularLimits = true;

    [Tooltip("Let the puppet's own body parts collide with each other — a glove into your own guard or chin " +
             "(req.md §25, §40). Realistic, but a resting guard can jitter against the chest. Off by default.")]
    [SerializeField] private bool internalCollisions = false;

    [Min(1)] [SerializeField] private int solverIterations = 12;

    [Header("Per body part — how much of the animation each part must obey (req.md §16)")]
    [Tooltip("x = Pin (pulled to the animated position), y = Muscle (strength holding the animated angle).")]
    [SerializeField] private Vector2 hipsControl = new Vector2(1f, 1f);
    [SerializeField] private Vector2 spineControl = new Vector2(0.9f, 0.95f);
    [Tooltip("The head is pinned HARD and driven softly: soft muscle still lets a jaw shot snap it, but a loose " +
             "PIN let it translate away from a ragdoll that has no neck muscle, and the neck bone stretched to " +
             "reach it — the 'neck gets bigger' bug.")]
    [SerializeField] private Vector2 headControl = new Vector2(0.85f, 0.65f);
    [SerializeField] private Vector2 armControl = new Vector2(0.8f, 0.9f);
    [SerializeField] private Vector2 handControl = new Vector2(0.7f, 0.8f);
    [SerializeField] private Vector2 legControl = new Vector2(1f, 1f);

    [Tooltip("How much the visible HEAD follows the puppet. This ragdoll has no neck muscle, so the neck bone " +
             "is stretched to bridge whatever gap opens up between the chest and the head — below 1 that " +
             "stretching is proportionally reduced. Build a ragdoll with a neck muscle and this can go to 1.")]
    [Range(0f, 1f)] [SerializeField] private float headMapping = 0.65f;

    [Tooltip("The visible legs stay pure animation — Legs Animator owns them. The leg muscles still simulate " +
             "(so they collide and can carry a knockdown), they just do not draw.")]
    [SerializeField] private bool legsFromAnimationOnly = true;

    [Tooltip("ONLY THE UPPER BODY IS PHYSICAL ON SCREEN. The PELVIS also renders from animation, so the stance " +
             "the animation set is exactly the stance you see: the legs hang off the pelvis, and a pelvis drawn " +
             "from a puppet that is lagging, leaning or slowly rotating is what makes the body look deformed " +
             "and the stance drift from orthodox to southpaw. The pelvis muscle is still pinned hard, so the " +
             "physical torso stays attached to the animated hips — spine, chest, head and arms remain fully " +
             "physical: they take the punch, the recoil and the bag. Untick for a whole-body puppet.")]
    [SerializeField] private bool upperBodyOnly = true;

    [Header("The punch (req.md §8, §13, §14)")]
    [Tooltip("Muscle multiplier on the punching arm at the peak of a fully loaded drive — physical capability, " +
             "never damage.")]
    [Range(1f, 4f)] [SerializeField] private float driveMuscle = 2.2f;

    [Tooltip("Extra pinning on the punching arm while it flies. This is the invisible assist that makes a punch " +
             "land where you aimed it (req.md §35); drop it toward 0 for a pure simulator.")]
    [Range(0f, 1f)] [SerializeField] private float driveAssist = 0.7f;

    [Tooltip("Seconds the punching arm goes SLACK on contact. The bag's real impulse then crumples the arm " +
             "instead of the muscle driving straight through it — this is most of what makes a hit feel physical.")]
    [Range(0f, 0.25f)] [SerializeField] private float impactSlack = 0.06f;

    [Range(0f, 1f)] [SerializeField] private float impactSlackPin = 0.25f;
    [Range(0f, 1f)] [SerializeField] private float impactSlackMuscle = 0.5f;

    [Tooltip("Hips / chest torque driving the rotation into a punch (req.md §8). Scaled by Assist — this is the " +
             "system doing the body mechanics a trained boxer would, so the player only has to aim.")]
    [Min(0f)] [SerializeField] private float chainTorque = 26f;

    [Header("Fist assist — a HELPING force, not the engine (req.md §42)")]
    [Tooltip("Too high and you get a magical rocket fist dragging the body around. The shoulder and chest are " +
             "supposed to do the work; this only closes the last of the gap.")]
    [Min(0f)] [SerializeField] private float assistPositionGain = 420f;
    [Min(0f)] [SerializeField] private float assistVelocityGain = 28f;
    [Min(0f)] [SerializeField] private float assistMaxForce = 700f;

    [Header("Guard (req.md §26)")]
    [Range(1f, 3f)] [SerializeField] private float guardMuscle = 1.6f;
    [Range(0f, 1f)] [SerializeField] private float guardPin = 0.9f;

    [Header("Effective mass behind a punch (req.md §7, §19)")]
    [SerializeField] private float forearmMass = 1.5f;
    [SerializeField] private float upperArmMass = 2f;
    [SerializeField] private float chestMassFactor = 5f;
    [SerializeField] private float hipsMassFactor = 7f;

    [Header("Stun & knockdown (req.md §31, §32, §33)")]
    [Tooltip("A head shot at this speed (m/s) starts weakening the muscles. The player KEEPS control — he is " +
             "wobbly, not disabled.")]
    [Min(0.1f)] [SerializeField] private float stunSpeed = 2.5f;
    [Range(0.05f, 1f)] [SerializeField] private float stunStrength = 0.45f;
    [Min(0.05f)] [SerializeField] private float stunDuration = 0.8f;

    [Tooltip("Off while you are training on the bag — nothing is less fun than your own balance putting you on " +
             "the floor. Turn it on for fights.")]
    [SerializeField] private bool allowKnockdown = false;
    [Min(0.1f)] [SerializeField] private float knockdownSpeed = 4.5f;
    [Tooltip("How far the centre of mass may leave the feet before he goes down (metres, req.md §15, §33).")]
    [Min(0.05f)] [SerializeField] private float balanceLimit = 0.45f;
    [Min(0.5f)] [SerializeField] private float knockdownTime = 2.5f;
    [Min(0.1f)] [SerializeField] private float getUpTime = 1f;

    [Header("Assist — req.md §35: 60 % believable physics, 25 % invisible help, 15 % game feel")]
    [Tooltip("Scales every hidden helping hand at once: fist assist, hip / chest drive, balance correction. " +
             "1 = a beginner throws decent punches. 0 = pure simulator, and it will feel awful.")]
    [Range(0f, 1f)] [SerializeField] private float assist = 0.75f;

    [Header("Safety — the character never gets to look broken")]
    [Tooltip("A muscle this far (metres, at the character's own scale) from the bone it belongs on counts as " +
             "the puppet coming apart.")]
    [Min(0.05f)] [SerializeField] private float stretchLimit = 0.32f;
    [Tooltip("Sustained stretch fades the MAPPING out: the mesh quietly goes back to being the animation while " +
             "the physics catches up, instead of showing you a stretched arm.")]
    [Range(0f, 1f)] [SerializeField] private float stretchSafeMapping = 0.15f;
    [Tooltip("Past this the puppet is not recoverable and drops to Kinematic mode with an explanation.")]
    [Min(0.2f)] [SerializeField] private float stretchPanic = 1.2f;

    [Header("Debug")]
    [SerializeField] private bool logBringUp = true;
    [SerializeField] private bool drawGizmos = true;

    // ------------------------------------------------------------------ Public state

    public bool IsBuilt { get; private set; }
    public bool IsKnockedDown { get; private set; }

    /// <summary>Global muscle strength: 1 = fresh, low = stunned or gassed (req.md §31).</summary>
    public float MuscleStrength { get; private set; } = 1f;

    /// <summary>Worst muscle-to-bone distance this step, in metres at character scale. The watchdog reads this.</summary>
    public float Stretch { get; private set; }

    /// <summary>How far the centre of mass has left the feet (metres). Past Balance Limit he goes down.</summary>
    public float BalanceOffset { get; private set; }

    public PhysicalFist LeftFist { get; private set; }
    public PhysicalFist RightFist { get; private set; }
    public PuppetMaster Puppet => puppetMaster;

    /// <summary>How much of the puppet the mesh is currently showing (1 = puppet, ~0.15 = faded to animation).
    /// PunchDebug watches this: a pumping fade is the "arm snaps sideways" artifact.</summary>
    public float MappingFade => mappingFade;

    // Live tuning, read by PunchDebug's overlay so "did the Rebuild tool actually run?" is visible on screen.
    public float MuscleSpringValue => muscleSpring;
    public float PinPowValue => pinPow;
    public float AssistVelocityGainValue => assistVelocityGain;
    public float AssistMaxForceValue => assistMaxForce;

    /// <summary>
    /// Hand this body a specific PuppetMaster BEFORE it initializes — used by <see cref="OpponentSpawner"/>,
    /// whose freshly cloned puppet is still inactive (and therefore invisible to the scene search) when the
    /// cloned fighter wakes up.
    /// </summary>
    public void SetPuppet(PuppetMaster puppet) => puppetMaster = puppet;

    /// <summary>Where one hand's muscle ACTUALLY is vs where its animation target is, plus its live pin.</summary>
    public bool TryGetHandState(int hand, out Vector3 musclePosition, out Vector3 targetPosition, out float pin)
    {
        musclePosition = targetPosition = Vector3.zero;
        pin = 0f;
        if (hand < 0 || hand > 1) return false;
        Part part = handParts[hand];
        if (part == null || part.rb == null || part.muscle.target == null) return false;
        musclePosition = part.rb.position;
        targetPosition = part.muscle.target.position;
        pin = part.muscle.state.pinWeightMlp;
        return true;
    }

    public event Action<bool> KnockdownChanged;

    // ------------------------------------------------------------------ Private state

    /// <summary>
    /// One muscle, plus the two numbers that make PuppetMaster's multiplier model behave.
    ///
    /// PuppetMaster computes pin as props.pinWeight × master × state.pinWeightMlp and CLAMPS the multiplier to
    /// 0-1 — so a multiplier can only ever weaken a muscle, never strengthen it. To let a punching arm pin
    /// HARDER than it does at rest, props.pinWeight is set to the arm's ceiling (the hardest it should ever pin)
    /// and <see cref="restPin"/> is the fraction of that it sits at while guarding. Same trick for the guard.
    /// (Mapping is the same story, which is why the legs are unmapped through the multiplier, not the props —
    /// otherwise a knockdown could never map them back and the legs would stay animated while he fell.)
    /// </summary>
    private class Part
    {
        public Muscle muscle;
        public Role role;
        public int side;              // -1 left, +1 right, 0 centre
        public Rigidbody rb;
        public float restPin = 1f;    // multiplier at rest, against the props ceiling
        public float guardPinMlp = 1f;
        public float restMapping = 1f;
    }

    private struct HandCommand
    {
        public bool active;
        public Vector3 target;
        public Vector3 velocity;
        public float drive;
        public BoxerPunchController.Phase phase;
        public float x;
        public float overdrive;
        public float impactAt;
    }

    private readonly List<Part> parts = new List<Part>();
    private readonly HandCommand[] hands = new HandCommand[2];
    private readonly Part[] handParts = new Part[2];
    private readonly Part[] forearmParts = new Part[2];
    private readonly Part[] upperArmParts = new Part[2];
    private Part hipsPart, chestPart;

    private BoxerPunchController owner;
    private Animator animator;
    private FullBodyBipedIK bodyIK;
    private LookAtIK lookAtIK;
    private ReferencePoseMixer poseMixer;
    private CharacterController characterController;
    private Transform leftFoot, rightFoot;

    private bool guard;
    private float externalStrength = 1f;
    private float stunUntil = -10f;
    private float knockdownUntil;
    private float getUpStart = -10f;
    private bool gettingUp;
    private float offBalanceSince = -1f;

    private bool solversOrdered;
    private float scale = 1f;
    private float stretchSince = -1f;
    private float mappingFade = 1f;
    private bool panicked;
    private int panicCount;
    private float retryAt;
    private const int MaxPanics = 3;
    private float lastWatchdogLog = -10f;

    // ------------------------------------------------------------------ Bring-up

    /// <summary>
    /// Wake the physical body. Safe to call twice. Everything that needs the puppet's rigidbodies waits for
    /// PuppetMaster to initiate; everything else (settings, muscle props, the IK hand-over) happens right now,
    /// because the IK hand-over has to beat IKExecutionOrder.Start().
    /// </summary>
    public void Initialize(BoxerPunchController controller, Animator characterAnimator, FullBodyBipedIK ik)
    {
        if (IsBuilt) return;
        owner = controller;
        animator = characterAnimator;
        bodyIK = ik;
        lookAtIK = GetComponent<LookAtIK>();
        characterController = GetComponent<CharacterController>();
        scale = Mathf.Max(0.01f, transform.lossyScale.y);

        if (animator == null || !animator.isHuman)
        {
            Debug.LogError("BoxerPhysics: needs a Humanoid Animator on the character.", this);
            enabled = false;
            return;
        }

        if (puppetMaster == null)
        {
            foreach (PuppetMaster pm in FindObjectsByType<PuppetMaster>(FindObjectsSortMode.None))
                if (pm.targetRoot == transform) { puppetMaster = pm; break; }
        }
        if (puppetMaster == null || puppetMaster.muscles == null || puppetMaster.muscles.Length == 0)
        {
            Debug.LogError("BoxerPhysics: no PuppetMaster with muscles found for this character. Create the " +
                           "ragdoll first (PuppetMaster ▸ BipedRagdollCreator), then run " +
                           "Tools ▸ Boxer ▸ Physics ▸ Setup Physics Boxer.", this);
            enabled = false;
            return;
        }

        leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);

        ApplyTuningFloors();

        if (physicsRate > 0) Time.fixedDeltaTime = 1f / physicsRate;
        if (puppetOwnsIK) HandOverIK();
        ConfigurePuppet();
        MapMuscles();

        puppetMaster.OnPostInitiate += OnPuppetInitiated;
        puppetMaster.OnRead += OnPuppetRead;
        puppetMaster.mode = PuppetMaster.Mode.Active;

        if (puppetMaster.initiated) OnPuppetInitiated();      // PuppetMaster woke up before we did
    }

    private void OnDestroy()
    {
        if (puppetMaster == null) return;
        puppetMaster.OnPostInitiate -= OnPuppetInitiated;
        puppetMaster.OnRead -= OnPuppetRead;
    }

    /// <summary>
    /// THE SCENE CANNOT BE TRUSTED TO CARRY THE TUNING. A serialized component keeps the values from the day it
    /// was added — changed C# defaults do nothing, and editor menu runs get skipped. The flight recorder proved
    /// it: the scene was still running pinPow 4 (a pin of 0.35 is 0.35⁴ ≈ 0.015 — unpinned) and spring 400, so
    /// the physical gloves floated HALF A METRE from the hands and punches sailed past the bag. These floors are
    /// enforced at runtime, every Play: a stale scene can bend the feel, it can never break the puppet again.
    /// </summary>
    private void ApplyTuningFloors()
    {
        string was = $"pinPow {pinPow:0.#}, spring {muscleSpring:0}, hand pin {handControl.x:0.00}, limits {(angularLimits ? "on" : "off")}";

        // The ragdoll's joint limits were authored on a coarse 15-muscle rig and JAM the punch poses: with the
        // puppet finally strong and fully mapped, an uppercut hit the shoulder/elbow stops and the arm stuck out
        // sideways instead of curling. The muscle drives are strong enough to hold anatomy on their own now.
        angularLimits = false;

        pinPow = Mathf.Min(pinPow, 2f);                                    // the exponent that silently unpinned everything
        muscleSpring = Mathf.Max(muscleSpring, 650f);
        muscleDamper = Mathf.Max(muscleDamper, 10f);
        pinDistanceFalloff = Mathf.Min(pinDistanceFalloff, 0.5f);
        spineControl = Vector2.Max(spineControl, new Vector2(0.9f, 0.95f));
        headControl = Vector2.Max(headControl, new Vector2(0.85f, 0.65f));
        armControl = Vector2.Max(armControl, new Vector2(0.8f, 0.9f));
        handControl = Vector2.Max(handControl, new Vector2(0.7f, 0.8f));
        headMapping = Mathf.Min(headMapping, 0.65f);                       // this ragdoll has no neck muscle to bridge
        stretchLimit = Mathf.Max(stretchLimit, 0.32f);
        stretchPanic = Mathf.Max(stretchPanic, 1.2f);
        assistVelocityGain = Mathf.Max(assistVelocityGain, 40f);
        assistMaxForce = Mathf.Max(assistMaxForce, 900f);

        string now = $"pinPow {pinPow:0.#}, spring {muscleSpring:0}, hand pin {handControl.x:0.00}, limits {(angularLimits ? "on" : "off")}";
        if (was != now)
            Debug.Log($"BoxerPhysics: stale scene tuning floored at runtime ({was} → {now}). " +
                      "Run Tools ▸ Boxer ▸ Rebuild Boxing System once to persist it.", this);
    }

    /// <summary>
    /// Give PuppetMaster the baton. IKExecutionOrder leaves the solvers disabled and runs them in LateUpdate,
    /// which is far too late — the puppet reads the pose in FixedUpdate. Re-enable them, switch the Animator to
    /// Animate Physics so PuppetMaster drives Animator → IK → Read as one step, and stand IKExecutionOrder down
    /// BEFORE its Start() can disable anything (this runs from the controller's Awake, so it wins).
    /// </summary>
    private void HandOverIK()
    {
        IKExecutionOrder order = GetComponent<IKExecutionOrder>();
        if (order != null)
        {
            if (order.IKComponents != null)
                foreach (IK component in order.IKComponents) if (component != null) component.enabled = true;
            order.enabled = false;
        }
        if (bodyIK != null) bodyIK.enabled = true;
        if (lookAtIK != null) lookAtIK.enabled = true;

        animator.updateMode = AnimatorUpdateMode.Fixed;

        // The pose chain has to run between the Animator and the IK. Once PuppetMaster owns that whole sequence
        // in FixedUpdate, the mixer's own LateUpdate would land after the puppet had already read the pose — so
        // it hands over too, and gets called off the FIRST solver's pre-update instead.
        poseMixer = GetComponent<ReferencePoseMixer>();
        if (poseMixer != null && poseMixer.enabled)
        {
            poseMixer.ExternalUpdate = true;
            IK first = lookAtIK != null ? (IK)lookAtIK : bodyIK;
            if (first != null) first.GetIKSolver().OnPreUpdate += ApplyPosesBeforeIK;
            else poseMixer.ExternalUpdate = false;   // no solver to hang it on; leave it in LateUpdate
        }
    }

    private void ApplyPosesBeforeIK()
    {
        if (poseMixer != null) poseMixer.Apply(Time.fixedDeltaTime);
    }

    /// <summary>The solver order PuppetMaster gets from component order is arbitrary. Look-at aims the spine and
    /// head first; the full-body solve (hands on the punch, feet planted) must have the last word.</summary>
    private void OrderSolvers()
    {
        solversOrdered = true;
        if (puppetMaster.solvers == null || puppetMaster.solvers.Count == 0) return;

        List<SolverManager> ordered = new List<SolverManager>();
        if (lookAtIK != null && puppetMaster.solvers.Contains(lookAtIK)) ordered.Add(lookAtIK);
        if (bodyIK != null && puppetMaster.solvers.Contains(bodyIK)) ordered.Add(bodyIK);
        foreach (SolverManager solver in puppetMaster.solvers)
            if (solver != null && !ordered.Contains(solver)) ordered.Add(solver);

        puppetMaster.solvers.Clear();
        puppetMaster.solvers.AddRange(ordered);
    }

    private void ConfigurePuppet()
    {
        // A puppet tuned for a 1x character cannot hold up a 2x one: mass grows with the cube of scale while
        // the drives were left alone. Squared is the practical middle ground.
        float boost = autoScaleToCharacter ? scale * scale : 1f;
        puppetMaster.muscleSpring = muscleSpring * boost;
        puppetMaster.muscleDamper = muscleDamper * boost;
        puppetMaster.pinDistanceFalloff = pinDistanceFalloff;
        puppetMaster.pinPow = pinPow;
        puppetMaster.angularPinning = angularPinning;
        puppetMaster.angularLimits = angularLimits;
        puppetMaster.internalCollisions = internalCollisions;
        puppetMaster.solverIterationCount = solverIterations;
        puppetMaster.updateJointAnchors = true;   // the rig has animated bones between muscles (spine, clavicles)
        puppetMaster.fixTargetTransforms = true;
        puppetMaster.mappingWeight = 1f;
        puppetMaster.pinWeight = 1f;
        puppetMaster.muscleWeight = 1f;
        puppetMaster.blendTime = 0.15f;
        puppetMaster.state = PuppetMaster.State.Alive;
    }

    /// <summary>Sort every muscle into a body part and give it the control from req.md's table.</summary>
    private void MapMuscles()
    {
        parts.Clear();
        Array.Clear(handParts, 0, 2);
        Array.Clear(forearmParts, 0, 2);
        Array.Clear(upperArmParts, 0, 2);
        hipsPart = chestPart = null;

        Dictionary<Transform, HumanBodyBones> boneOf = new Dictionary<Transform, HumanBodyBones>();
        foreach (HumanBodyBones bone in InterestingBones)
        {
            Transform t = animator.GetBoneTransform(bone);
            if (t != null && !boneOf.ContainsKey(t)) boneOf.Add(t, bone);
        }

        foreach (Muscle muscle in puppetMaster.muscles)
        {
            if (muscle == null || muscle.target == null) continue;

            Role role;
            int side;
            if (boneOf.TryGetValue(muscle.target, out HumanBodyBones bone)) Classify(bone, out role, out side);
            else { RoleFromGroup(muscle.props.group, out role); side = 0; }

            Vector2 control = ControlFor(role);
            bool isArm = role == Role.UpperArm || role == Role.Forearm || role == Role.Hand;
            bool isLeg = role == Role.Leg || role == Role.Foot;

            // An arm has to be able to pin harder than it rests — while it is flying at a target, and while it is
            // braced in a guard. That headroom lives in props; the multiplier scales down from it.
            float ceiling = isArm ? Mathf.Clamp01(Mathf.Max(control.x + driveAssist, guardPin)) : Mathf.Clamp01(control.x);
            // An unmapped pelvis must still TRACK the animated one, or the physical torso hanging off it drifts
            // away from the body you can see. Pinned hard, the seam at the waist stays invisible.
            if (upperBodyOnly && role == Role.Hips) ceiling = Mathf.Max(ceiling, 0.95f);
            muscle.props.pinWeight = ceiling;
            muscle.props.muscleWeight = control.y;
            // The legs are unmapped through the MULTIPLIER (see Part); the head is damped here because the gap
            // it has to bridge is a real bone, not a multiplier.
            muscle.props.mappingWeight = role == Role.Head ? headMapping : 1f;
            muscle.props.muscleDamper = 1f;

            Part part = new Part
            {
                muscle = muscle,
                role = role,
                side = side,
                restPin = ceiling > 0.001f ? Mathf.Clamp01(control.x / ceiling) : 1f,
                guardPinMlp = ceiling > 0.001f ? Mathf.Clamp01(guardPin / ceiling) : 1f,
                restMapping = (legsFromAnimationOnly && isLeg) || (upperBodyOnly && (isLeg || role == Role.Hips)) ? 0f : 1f,
            };
            parts.Add(part);

            int h = side > 0 ? 1 : 0;
            switch (role)
            {
                case Role.Hips: hipsPart = part; break;
                case Role.Spine: if (chestPart == null) chestPart = part; break;
                case Role.UpperArm: if (side != 0) upperArmParts[h] = part; break;
                case Role.Forearm: if (side != 0) forearmParts[h] = part; break;
                case Role.Hand: if (side != 0) handParts[h] = part; break;
            }
        }
    }

    private static readonly HumanBodyBones[] InterestingBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
        HumanBodyBones.Neck, HumanBodyBones.Head,
        HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
    };

    private static void Classify(HumanBodyBones bone, out Role role, out int side)
    {
        switch (bone)
        {
            case HumanBodyBones.Hips: role = Role.Hips; side = 0; return;
            case HumanBodyBones.Spine:
            case HumanBodyBones.Chest:
            case HumanBodyBones.UpperChest: role = Role.Spine; side = 0; return;
            case HumanBodyBones.Neck:
            case HumanBodyBones.Head: role = Role.Head; side = 0; return;
            case HumanBodyBones.LeftShoulder:
            case HumanBodyBones.LeftUpperArm: role = Role.UpperArm; side = -1; return;
            case HumanBodyBones.RightShoulder:
            case HumanBodyBones.RightUpperArm: role = Role.UpperArm; side = 1; return;
            case HumanBodyBones.LeftLowerArm: role = Role.Forearm; side = -1; return;
            case HumanBodyBones.RightLowerArm: role = Role.Forearm; side = 1; return;
            case HumanBodyBones.LeftHand: role = Role.Hand; side = -1; return;
            case HumanBodyBones.RightHand: role = Role.Hand; side = 1; return;
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.LeftLowerLeg: role = Role.Leg; side = -1; return;
            case HumanBodyBones.RightUpperLeg:
            case HumanBodyBones.RightLowerLeg: role = Role.Leg; side = 1; return;
            case HumanBodyBones.LeftFoot: role = Role.Foot; side = -1; return;
            case HumanBodyBones.RightFoot: role = Role.Foot; side = 1; return;
            default: role = Role.Other; side = 0; return;
        }
    }

    private static void RoleFromGroup(Muscle.Group group, out Role role)
    {
        switch (group)
        {
            case Muscle.Group.Hips: role = Role.Hips; break;
            case Muscle.Group.Spine: role = Role.Spine; break;
            case Muscle.Group.Head: role = Role.Head; break;
            case Muscle.Group.Arm: role = Role.UpperArm; break;
            case Muscle.Group.Hand: role = Role.Hand; break;
            case Muscle.Group.Leg: role = Role.Leg; break;
            case Muscle.Group.Foot: role = Role.Foot; break;
            default: role = Role.Other; break;
        }
    }

    private Vector2 ControlFor(Role role)
    {
        switch (role)
        {
            case Role.Hips: return hipsControl;
            case Role.Spine: return spineControl;
            case Role.Head: return headControl;
            case Role.UpperArm:
            case Role.Forearm: return armControl;
            case Role.Hand: return handControl;
            case Role.Leg:
            case Role.Foot: return legControl;
            default: return spineControl;
        }
    }

    /// <summary>Everything that needs the puppet's live rigidbodies. Runs once PuppetMaster has initiated.</summary>
    private void OnPuppetInitiated()
    {
        if (IsBuilt) return;

        foreach (Part part in parts)
        {
            part.rb = part.muscle.rigidbody;
            if (part.rb == null) continue;

            part.rb.interpolation = RigidbodyInterpolation.Interpolate;
            part.rb.maxAngularVelocity = 40f;
            // A glove at punch speed will tunnel straight through a head or another glove at any sane step size —
            // the fast parts get continuous detection, the rest stay cheap (req.md §17, §40).
            part.rb.collisionDetectionMode = part.role == Role.Hand || part.role == Role.Forearm
                ? CollisionDetectionMode.ContinuousDynamic
                : CollisionDetectionMode.Discrete;

            PhysicsPuppetPart tag = part.rb.GetComponent<PhysicsPuppetPart>();
            if (tag == null) tag = part.rb.gameObject.AddComponent<PhysicsPuppetPart>();
            tag.Owner = owner;
            tag.IsHand = part.role == Role.Hand;
            tag.IsHead = part.role == Role.Head;
        }

        BuildFists();
        IgnoreCharacterCollisions();
        IsBuilt = true;

        if (logBringUp) Debug.Log(Report(), this);
    }

    /// <summary>The gloves become the hit sensors: real rigidbodies, real contacts, real relative velocity.</summary>
    private void BuildFists()
    {
        for (int h = 0; h < 2; h++)
        {
            Part hand = handParts[h];
            if (hand == null || hand.rb == null) continue;

            PhysicalFist fist = hand.rb.GetComponent<PhysicalFist>();
            if (fist == null) fist = hand.rb.gameObject.AddComponent<PhysicalFist>();
            fist.Hand = h;
            fist.Body = this;
            fist.SpeedScale = scale;   // human speed/mass ceilings are defined at 1x; Bennett is bigger

            PhysicsPuppetPart tag = hand.rb.GetComponent<PhysicsPuppetPart>();
            if (tag != null) tag.Fist = fist;

            if (h == 0) LeftFist = fist; else RightFist = fist;
        }
    }

    /// <summary>The puppet must never fight the CharacterController capsule or the Legs Animator's foot probes.</summary>
    private void IgnoreCharacterCollisions()
    {
        List<Collider> own = new List<Collider>(GetComponentsInChildren<Collider>(true));
        foreach (Part part in parts)
        {
            Collider[] muscleColliders = part.muscle.colliders;
            if (muscleColliders == null) continue;
            foreach (Collider mc in muscleColliders)
            {
                if (mc == null) continue;
                foreach (Collider oc in own)
                    if (oc != null && oc != mc) Physics.IgnoreCollision(mc, oc, true);
            }
        }
    }

    // ------------------------------------------------------------------ The physics step

    /// <summary>
    /// Called by PuppetMaster right after the Animator and the IK have produced this step's reference pose and
    /// just before the muscles are updated — the exact moment to say how hard each one should try.
    /// </summary>
    private void OnPuppetRead()
    {
        if (!IsBuilt) return;
        if (panicked)
        {
            // Give it another go rather than losing physics for the rest of the session over one bad moment.
            if (panicCount > MaxPanics || Time.time < retryAt) return;
            panicked = false;
            stretchSince = -1f;
            puppetMaster.mode = PuppetMaster.Mode.Active;
            return;
        }
        if (!solversOrdered) OrderSolvers();

        float dt = Time.fixedDeltaTime;
        UpdateStates(dt);
        UpdateWatchdog(dt);
        ApplyMuscleProfile();
        ApplyFistAssist();
        ApplyChainTorque();
        UpdateBalance(dt);
    }

    private void UpdateStates(float dt)
    {
        float now = Time.time;

        if (IsKnockedDown)
        {
            MuscleStrength = 0.08f;
            if (now >= knockdownUntil) GetUp();
            return;
        }

        if (gettingUp)
        {
            float t = Mathf.Clamp01((now - getUpStart) / getUpTime);
            MuscleStrength = Mathf.Lerp(0.3f, 1f, t);
            if (t >= 1f) gettingUp = false;
            return;
        }

        // Stun is not a state you lose control in — the muscles are simply weaker for a moment and the character
        // physically struggles to hold his own guard (req.md §31, §32).
        bool stunned = now < stunUntil;
        MuscleStrength = Mathf.MoveTowards(MuscleStrength, stunned ? stunStrength : 1f, dt / (stunned ? 0.08f : 0.3f));
    }

    /// <summary>
    /// req.md §16's table, alive: each part's static control, times global strength, times what this hand is
    /// doing right now. The kinetic chain lives here — the hips fire first and the fist last, so power is
    /// generated from the ground up instead of the arm being thrown on its own (req.md §8, §20).
    /// </summary>
    private void ApplyMuscleProfile()
    {
        float strength = MuscleStrength * externalStrength;

        foreach (Part part in parts)
        {
            float pin = part.restPin;
            float muscle = strength;
            float mapping = part.restMapping * mappingFade;

            bool isArm = part.role == Role.UpperArm || part.role == Role.Forearm || part.role == Role.Hand;
            int h = part.side > 0 ? 1 : 0;

            if (part.role == Role.Leg || part.role == Role.Foot)
            {
                // The legs are the base: they hold the stance at full strength however stunned the upper body is,
                // or the whole boxer folds.
                muscle = Mathf.Max(muscle, 0.85f);
            }
            else if (isArm && part.side != 0)
            {
                HandCommand cmd = hands[h];
                float chain = ChainWindow(part.role, cmd);

                switch (cmd.phase)
                {
                    case BoxerPunchController.Phase.Control:
                        muscle *= 1f + 0.2f * Mathf.Clamp01(cmd.x / Mathf.Max(0.01f, PunchTimeline.ChamberX));
                        break;

                    case BoxerPunchController.Phase.Drive:
                    {
                        // The pose chain starts whipping the arm from the FIRST frame of the drive (the coil
                        // releases top-down), but the per-part chain windows only open late — so for the first
                        // half of every punch the arm chased a fast-moving target on its resting pin and fell
                        // half a metre behind. A floor keeps the whole arm engaged for the whole flight.
                        float engaged = Mathf.Max(chain, 0.3f);
                        // Physical capability rises with how well the punch was loaded and flicked — never damage.
                        muscle *= Mathf.Lerp(1f, driveMuscle, engaged * (0.45f + 0.55f * cmd.overdrive));
                        // …and the flying arm is pulled toward its intent harder. This is the invisible assist
                        // (req.md §35): at Assist 0 the arm is on its own and you have to be genuinely good.
                        pin = Mathf.Lerp(part.restPin, 1f, engaged * assist);
                        break;
                    }

                    case BoxerPunchController.Phase.Impact:
                    {
                        // Contact: go slack for a few steps so the bag's impulse really crumples the arm.
                        float since = Time.time - cmd.impactAt;
                        float slack = impactSlack > 0f ? 1f - Mathf.Clamp01(since / impactSlack) : 0f;
                        pin = part.restPin * Mathf.Lerp(1f, impactSlackPin, slack);
                        muscle *= Mathf.Lerp(1f, impactSlackMuscle, slack);
                        break;
                    }

                    case BoxerPunchController.Phase.Recover:
                        muscle *= Mathf.Lerp(1f, 1.15f, cmd.x);
                        break;
                }

                // Guard is physical: the arms stiffen, so a heavy shot shoves the glove into your own face
                // instead of a flag zeroing the damage (req.md §14, §26).
                if (guard)
                {
                    muscle *= guardMuscle;
                    pin = Mathf.Max(pin, part.guardPinMlp);
                }
            }
            else if (part.role == Role.Head)
            {
                // The head is deliberately loose — that is what makes a jaw shot snap it (req.md §13, §30).
                if (guard) muscle *= 1.2f;
            }

            // Down: nothing is pinned to the animation any more and the legs map again, so you watch him fall
            // rather than watching an animated stance slide along the floor (req.md §33).
            if (IsKnockedDown) { pin = 0f; muscle = Mathf.Min(muscle, 0.1f); mapping = 1f; }

            part.muscle.state.pinWeightMlp = Mathf.Clamp01(pin);
            part.muscle.state.muscleWeightMlp = Mathf.Max(0.02f, muscle);
            part.muscle.state.mappingWeightMlp = Mathf.Clamp01(mapping);
        }
    }

    /// <summary>
    /// Where this part sits in the kinetic chain, as a 0-1 window over the punch's own clock. The hips lead and
    /// the fist arrives last — the same staggering the pose mixer uses, so muscles and reference agree.
    /// </summary>
    private static float ChainWindow(Role role, HandCommand cmd)
    {
        float x = Mathf.Clamp01(cmd.x);
        switch (role)
        {
            case Role.UpperArm: return Mathf.Clamp01(Mathf.InverseLerp(0.35f, 0.95f, x));
            case Role.Forearm: return Mathf.Clamp01(Mathf.InverseLerp(0.45f, 1f, x));
            case Role.Hand: return Mathf.Clamp01(Mathf.InverseLerp(0.55f, 1f, x));
            default: return x;
        }
    }

    /// <summary>
    /// req.md §42: a helping force toward the intent, hard-capped. Most of the punch must still come from the
    /// joint drives and the torso, or the hand turns into a rocket dragging the body behind it.
    /// </summary>
    private void ApplyFistAssist()
    {
        if (IsKnockedDown) return;
        for (int h = 0; h < 2; h++)
        {
            Part hand = handParts[h];
            if (hand == null || hand.rb == null || !hands[h].active) continue;

            float gain = assist * MuscleStrength * externalStrength * (0.6f + 0.9f * hands[h].drive);
            if (gain <= 0.001f) continue;

            Vector3 positionError = hands[h].target - hand.rb.position;
            Vector3 velocityError = hands[h].velocity - hand.rb.linearVelocity;
            Vector3 force = (positionError * assistPositionGain + velocityError * assistVelocityGain) * gain;
            hand.rb.AddForce(Vector3.ClampMagnitude(force, assistMaxForce * gain), ForceMode.Force);
        }
    }

    /// <summary>
    /// req.md §8: the punch comes from the ground up. The hips turn first, the chest follows, and the arm is
    /// carried by them — added as torque, so the body really rotates and really has to recover from it.
    /// </summary>
    private void ApplyChainTorque()
    {
        if (IsKnockedDown || chainTorque <= 0f) return;

        for (int h = 0; h < 2; h++)
        {
            HandCommand cmd = hands[h];
            if (cmd.phase != BoxerPunchController.Phase.Drive) continue;

            int side = h == 1 ? 1 : -1;
            float hips = Mathf.Sin(Mathf.Clamp01(Mathf.InverseLerp(0f, 0.55f, cmd.x)) * Mathf.PI);
            float chest = Mathf.Sin(Mathf.Clamp01(Mathf.InverseLerp(0.1f, 0.7f, cmd.x)) * Mathf.PI);
            float power = assist * (0.5f + 0.5f * cmd.overdrive) * MuscleStrength;

            if (hipsPart != null && hipsPart.rb != null && hips > 0f)
                hipsPart.rb.AddTorque(Vector3.up * (-side * chainTorque * 0.6f * hips * power), ForceMode.Acceleration);
            if (chestPart != null && chestPart.rb != null && chest > 0f)
                chestPart.rb.AddTorque(Vector3.up * (-side * chainTorque * chest * power), ForceMode.Acceleration);
        }
    }

    /// <summary>
    /// req.md §15 / §33: the centre of mass belongs over the feet. While it is, nothing happens. When it is not,
    /// the balance controller pulls the chest back over the base — and if even that cannot save it, he goes down.
    /// </summary>
    private void UpdateBalance(float dt)
    {
        Vector3 com = Vector3.zero;
        float total = 0f;
        foreach (Part part in parts)
        {
            if (part.rb == null || part.rb.isKinematic) continue;
            com += part.rb.worldCenterOfMass * part.rb.mass;
            total += part.rb.mass;
        }
        if (total <= 0f) return;
        com /= total;

        Vector3 support = leftFoot != null && rightFoot != null
            ? (leftFoot.position + rightFoot.position) * 0.5f
            : transform.position;

        Vector3 offset = Vector3.ProjectOnPlane(com - support, Vector3.up);
        BalanceOffset = offset.magnitude / scale;

        if (IsKnockedDown || gettingUp) { offBalanceSince = -1f; return; }

        // Invisible help (req.md §35): a corrective push back over the feet, so ordinary punching never topples him.
        if (chestPart != null && chestPart.rb != null && BalanceOffset > balanceLimit * 0.4f)
        {
            float over = BalanceOffset - balanceLimit * 0.4f;
            chestPart.rb.AddForce(-offset.normalized * (over * 45f * assist * scale), ForceMode.Acceleration);
        }

        if (!allowKnockdown) { offBalanceSince = -1f; return; }
        if (BalanceOffset > balanceLimit)
        {
            if (offBalanceSince < 0f) offBalanceSince = Time.time;
            else if (Time.time - offBalanceSince > 0.15f) KnockDown();
        }
        else offBalanceSince = -1f;
    }

    /// <summary>
    /// The one thing that must never happen is the character LOOKING broken. Watch how far the muscles are from
    /// the bones they belong on; if the puppet is coming apart, fade the mapping out so the mesh quietly reverts
    /// to the animation while physics sorts itself out, and only give up (Kinematic) if it is hopeless.
    /// </summary>
    private void UpdateWatchdog(float dt)
    {
        float worst = 0f;
        foreach (Part part in parts)
        {
            if (part.rb == null || part.muscle.target == null) continue;
            if (part.role == Role.Leg || part.role == Role.Foot) continue;   // unmapped anyway

            // AN ARM THROWING A PUNCH IS ALLOWED TO BE BEHIND. The whip outruns the muscles mid-flight and the
            // planted fist presses against a bag that (now) does not fly away — both are the system working,
            // not the puppet coming apart. Watching them anyway made the mapping fade in and out with every
            // punch, and that oscillation IS the "arm suddenly darts sideways" artifact: faded = you see the
            // animation, restored = you see the displaced puppet arm, and the switch reads as a snap.
            // The core (hips, spine, head) and any IDLE arm are still watched at full strictness.
            bool isArm = part.role == Role.UpperArm || part.role == Role.Forearm || part.role == Role.Hand;
            if (isArm && part.side != 0 && hands[part.side > 0 ? 1 : 0].phase >= BoxerPunchController.Phase.Drive)
                continue;

            float d = Vector3.Distance(part.rb.position, part.muscle.target.position);
            // An idle arm still gets slack: a guard shoved back by the swinging bag is the physics WORKING and
            // should be shown, not faded. Only past ~1.6x the core tolerance does it count as coming apart.
            if (isArm) d /= 1.6f;
            if (d > worst) worst = d;
        }
        Stretch = worst / scale;

        if (Stretch > stretchPanic)
        {
            panicked = true;
            panicCount++;
            retryAt = Time.time + 4f;
            puppetMaster.mode = PuppetMaster.Mode.Kinematic;
            puppetMaster.mappingWeight = 1f;
            mappingFade = 1f;
            if (panicCount <= MaxPanics)
                Debug.LogWarning($"BoxerPhysics: the puppet came apart ({Stretch:0.00}x character height behind " +
                                 "the animation) — dropped to Kinematic and will try Active again in 4 s. If this " +
                                 "keeps happening, raise Muscle Spring or turn Angular Limits off.", this);
            else
                Debug.LogError("BoxerPhysics: the puppet has come apart repeatedly and is staying Kinematic. The " +
                               "muscles cannot hold this character: raise Muscle Spring (and check the ragdoll's " +
                               "rigidbody masses match the character's actual size).", this);
            return;
        }

        if (Stretch > stretchLimit) { if (stretchSince < 0f) stretchSince = Time.time; }
        else stretchSince = -1f;

        bool hold = stretchSince > 0f && Time.time - stretchSince > 0.25f;
        mappingFade = Mathf.MoveTowards(mappingFade, hold ? stretchSafeMapping : 1f, dt / (hold ? 0.1f : 0.4f));

        if (hold && Time.time - lastWatchdogLog > 5f)
        {
            lastWatchdogLog = Time.time;
            Debug.LogWarning($"BoxerPhysics: the puppet is lagging {Stretch:0.00}x character height ({Stretch * scale:0.00} m) " +
                             "behind the animation — mapping faded down so the character keeps looking right. " +
                             "Raise Muscle Spring, or lower Pin Pow so mid pin values actually pin.", this);
        }
    }

    // ------------------------------------------------------------------ IBoxerBody

    public void SetHand(int hand, bool active, Vector3 target, Vector3 velocity, float drive)
    {
        hands[hand].active = active;
        hands[hand].target = target;
        hands[hand].velocity = velocity;
        hands[hand].drive = Mathf.Clamp01(drive);
    }

    public void SetPunch(int hand, BoxerPunchController.Phase phase, float x, float overdrive)
    {
        if (hands[hand].phase != BoxerPunchController.Phase.Impact && phase == BoxerPunchController.Phase.Impact)
            hands[hand].impactAt = Time.time;
        hands[hand].phase = phase;
        hands[hand].x = Mathf.Clamp01(x);
        hands[hand].overdrive = Mathf.Clamp01(overdrive);
    }

    public void SetGuard(bool blocking) => guard = blocking;

    public void SetStrength(float strength) => externalStrength = Mathf.Clamp(strength, 0.05f, 2f);

    public void NotifyHeadHit(float speed)
    {
        if (IsKnockedDown || gettingUp) return;
        if (allowKnockdown && speed >= knockdownSpeed) { KnockDown(); return; }
        if (speed < stunSpeed) return;
        stunUntil = Mathf.Max(stunUntil, Time.time + stunDuration * Mathf.Clamp(speed / stunSpeed, 1f, 2f));
    }

    /// <summary>
    /// req.md §7 / §19: a fist on its own is a kilogram of nothing. What hurts is how much of the forearm, arm,
    /// chest and hips was genuinely travelling with it — measured from the puppet's real velocities, not assumed.
    /// </summary>
    public float EffectiveMass(int hand, Vector3 direction, float impactSpeed)
    {
        Part fist = handParts[hand];
        float mass = fist != null && fist.rb != null ? fist.rb.mass : 1f;
        float norm = Mathf.Max(impactSpeed, 0.5f);

        mass += forearmMass * Contribution(forearmParts[hand], direction, norm);
        mass += upperArmMass * Contribution(upperArmParts[hand], direction, norm);
        mass += chestMassFactor * Contribution(chestPart, direction, norm);
        mass += hipsMassFactor * Contribution(hipsPart, direction, norm);
        return mass;
    }

    private static float Contribution(Part part, Vector3 direction, float norm)
    {
        if (part == null || part.rb == null || part.rb.isKinematic) return 0f;
        return Mathf.Clamp01(Vector3.Dot(part.rb.linearVelocity, direction) / norm);
    }

    public void KnockDown()
    {
        if (IsKnockedDown || !IsBuilt) return;
        IsKnockedDown = true;
        gettingUp = false;
        offBalanceSince = -1f;
        knockdownUntil = Time.time + knockdownTime;
        KnockdownChanged?.Invoke(true);
    }

    /// <summary>Back on his feet: put the animated character where the puppet actually ended up, then let the
    /// pins wind back in so he pulls himself up into the stance instead of snapping to it.</summary>
    private void GetUp()
    {
        IsKnockedDown = false;
        gettingUp = true;
        getUpStart = Time.time;

        if (hipsPart != null && hipsPart.rb != null)
        {
            Vector3 landed = hipsPart.rb.position;
            Vector3 here = new Vector3(landed.x, transform.position.y, landed.z);
            if (Physics.Raycast(landed + Vector3.up * scale, Vector3.down, out RaycastHit hit, 4f * scale,
                                ~0, QueryTriggerInteraction.Ignore) && !hit.collider.transform.IsChildOf(transform))
                here.y = hit.point.y;

            bool had = characterController != null && characterController.enabled;
            if (had) characterController.enabled = false;
            transform.position = here;
            if (had) characterController.enabled = true;
        }

        KnockdownChanged?.Invoke(false);
    }

    // ------------------------------------------------------------------ Reporting

    /// <summary>What actually got set up — printed on bring-up and by the editor validator.</summary>
    public string Report()
    {
        int arms = 0, legs = 0, torso = 0;
        foreach (Part p in parts)
        {
            if (p.role == Role.UpperArm || p.role == Role.Forearm || p.role == Role.Hand) arms++;
            else if (p.role == Role.Leg || p.role == Role.Foot) legs++;
            else torso++;
        }

        string ik = puppetOwnsIK
            ? "PuppetMaster solves Final IK in FixedUpdate (LookAt → FullBody), Animator = Animate Physics"
            : "IK left to IKExecutionOrder — Active mode WILL fight the IK";
        string gloves = LeftFist != null && RightFist != null
            ? "both gloves are physical hit sensors"
            : "MISSING a physical glove — check the ragdoll has hand muscles";
        string armMap = $"L arm: upper {(upperArmParts[0] != null ? "✓" : "✗")} fore {(forearmParts[0] != null ? "✓" : "✗")} hand {(handParts[0] != null ? "✓" : "✗")}" +
                        $" | R arm: upper {(upperArmParts[1] != null ? "✓" : "✗")} fore {(forearmParts[1] != null ? "✓" : "✗")} hand {(handParts[1] != null ? "✓" : "✗")}";

        return $"BoxerPhysics: ACTIVE on {parts.Count} muscles ({torso} torso/head, {arms} arm, {legs} leg), " +
               $"{physicsRate} Hz, spring {muscleSpring}, {gloves}.\n  {armMap}\n  {ik}.\n" +
               $"  drawn from physics: {(upperBodyOnly ? "UPPER BODY ONLY (pelvis + legs render from animation, so the stance never drifts)" : "whole body")}; " +
               $"legs: {(legsFromAnimationOnly ? "animation-only (Legs Animator owns them)" : "physical")}; " +
               $"internal collisions: {(internalCollisions ? "on" : "off")}; " +
               $"knockdowns: {(allowKnockdown ? "on" : "off")}; assist: {assist:0.00}.";
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos || !Application.isPlaying || !IsBuilt) return;
        foreach (Part part in parts)
        {
            if (part.rb == null || part.muscle.target == null) continue;
            float d = Vector3.Distance(part.rb.position, part.muscle.target.position) / scale;
            Gizmos.color = Color.Lerp(new Color(0.2f, 1f, 0.4f, 0.8f), new Color(1f, 0.2f, 0.1f, 0.9f),
                                      Mathf.Clamp01(d / Mathf.Max(0.01f, stretchLimit)));
            Gizmos.DrawLine(part.rb.position, part.muscle.target.position);
            Gizmos.DrawWireSphere(part.rb.position, 0.02f * scale);
        }
    }
}
