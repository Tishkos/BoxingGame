using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
/// Feet on a wooden gym floor — steps that fire when the foot REALLY lands, and squeaks that happen the way
/// real shoe squeaks happen: sometimes, and only when rubber twists against the boards.
///
/// THE SOURCE OF TRUTH IS LEGS ANIMATOR. It owns the feet in this project (glueing, repositioning, idle
/// shuffles), so this component registers itself as its step receiver and gets every landing straight from the
/// leg logic — per foot, with the step's own position and power, including the little idle repositioning steps
/// no bone-velocity heuristic would catch. Each step type sounds different:
///
///   MOVEMENT step  →  a full take, volume and pitch following the gait (creep = soft brush, sprint = planted).
///   IDLE shuffle   →  the foot quietly finding a better spot — a soft, low-passed brush.
///   STOPPING       →  the settle when footwork ends — soft, a touch deeper, and the classic place a sneaker
///                     chirps, so the squeak chance is boosted.
///   LANDING        →  grounded again — the heaviest plant of all.
///
/// If no Legs Animator is present (a stripped-down rig), a bone-motion fallback detects plants from the foot
/// bones themselves — the sound never just disappears.
///
/// SQUEAKS fire where physics would make them: a dash-step gripping the boards, a hard direction reversal,
/// fast pivoting or heavy strafe, the rear foot twisting into a big hook or an overdriven punch — plus a small
/// ambient chance, because floors are floors. A global minimum interval keeps them an event, not a soundtrack.
///
/// Every play draws from shuffle-bag banks (originals + minted pitch variants + soft takes) with live jitter —
/// eight step recordings and five squeaks become a floor that never loops.
/// Added and wired by Tools ▸ Boxer ▸ Rebuild Boxing System; works on the AI clone automatically.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]
public class FootstepAudio : MonoBehaviour, LegsAnimator.ILegStepReceiver
{
    [Header("Source recordings (variants are minted from these at load)")]
    [Tooltip("Step recordings — Assets/Sounds/Normal Step. The setup tool fills this.")]
    [SerializeField] private AudioClip[] stepClips;

    [Tooltip("Shoe squeak recordings — Assets/Sounds/Squeak.")]
    [SerializeField] private AudioClip[] squeakClips;

    [Header("Levels")]
    [Range(0f, 1f)] [SerializeField] private float masterVolume = 0.9f;
    [Tooltip("Step volume at a creep.")]
    [Range(0f, 1f)] [SerializeField] private float walkVolume = 0.3f;
    [Tooltip("Step volume at a sprint.")]
    [Range(0f, 1f)] [SerializeField] private float sprintVolume = 0.85f;
    [Tooltip("The quiet idle shuffle when Legs Animator repositions a foot.")]
    [Range(0f, 1f)] [SerializeField] private float idleShuffleVolume = 0.32f;
    [Range(0f, 1f)] [SerializeField] private float squeakVolume = 0.55f;
    [Range(0f, 0.12f)] [SerializeField] private float pitchJitter = 0.05f;

    [Header("Legs Animator (the step source of truth)")]
    [Tooltip("Auto-found on the character. Its glue/step events drive the sounds; the bone-motion fallback " +
             "only runs when this is missing.")]
    [SerializeField] private LegsAnimator legsAnimator;

    [Tooltip("Also hear the tiny idle repositioning steps (Legs Animator's idle gluing).")]
    [SerializeField] private bool idleShuffles = true;

    [Header("Fallback plant detection (no Legs Animator; thresholds at 1x scale)")]
    [Tooltip("Foot speed above this = the foot is in flight (m/s).")]
    [Min(0.05f)] [SerializeField] private float liftSpeed = 0.55f;
    [Tooltip("A flying foot dropping below this speed, near the floor, is a plant (m/s).")]
    [Min(0.02f)] [SerializeField] private float plantSpeed = 0.28f;
    [Tooltip("How high above the character's base a plant may register (metres).")]
    [Min(0.02f)] [SerializeField] private float plantHeight = 0.16f;
    [Tooltip("Below this movement speed a fallback plant is silent — the idle sway is not footwork.")]
    [Min(0f)] [SerializeField] private float minMoveSpeed = 0.3f;

    [Header("When rubber squeaks — chances 0-1")]
    [Range(0f, 1f)] [SerializeField] private float dashSqueak = 0.65f;
    [Range(0f, 1f)] [SerializeField] private float reversalSqueak = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float pivotSqueak = 0.35f;
    [Range(0f, 1f)] [SerializeField] private float stopSqueak = 0.3f;
    [Range(0f, 1f)] [SerializeField] private float punchPivotSqueak = 0.22f;
    [Tooltip("The ambient chance on any ordinary plant — floors are floors.")]
    [Range(0f, 1f)] [SerializeField] private float ambientSqueak = 0.06f;
    [Tooltip("Squeaks stay an event: at most one per this many seconds.")]
    [Min(0.1f)] [SerializeField] private float squeakInterval = 0.7f;

    private class Foot
    {
        public Transform bone;
        public Vector3 lastPosition;
        public Vector3 velocity;
        public bool hasLast;
        public bool airborne;
        public float nextStep;
    }

    public bool HasStepClips => stepClips != null && System.Array.Exists(stepClips, clip => clip != null);
    private AimStudyBoxer study;
    private readonly FootstepCadence studyCadence = new FootstepCadence();
    private Vector3 lastRootPosition;
    private float lastPlantTime = -10f;
    private int shuffleFoot;
    private bool UseStudyMovement => study != null && study.isActiveAndEnabled;
    private Vector3 MoveVelocity => UseStudyMovement ? study.MovementVelocity : MoveDirection * MoveSpeed;
    private float MoveSpeed => UseStudyMovement ? study.MovementVelocity.magnitude
        : locomotion != null ? locomotion.CurrentSpeed : 0f;
    private Vector3 MoveDirection => UseStudyMovement ? study.MovementVelocity.normalized
        : locomotion != null ? locomotion.MoveDirection : Vector3.zero;

    private readonly Foot[] feet = { new Foot(), new Foot() };
    private Controller locomotion;
    private BoxerPunchController boxer;
    private AudioVariants.Bank stepPrimary;
    private AudioVariants.Bank stepSoft;
    private AudioVariants.Bank squeaks;
    private AudioVariants.VoicePool pool;
    private float scale = 1f;
    private bool useFallback;

    private float lastDashSeen = -10f;
    private float nextSqueak;
    private float yawRate;
    private float lastYaw;
    private Vector3 slowMoveDirection;      // heavily smoothed — a sharp turn away from it is a reversal
    private float pendingSqueakAt = -1f;    // punch pivots squeak a beat AFTER the throw (the foot grips first)
    private Vector3 pendingSqueakPos;
    private float pendingSqueakVolume;

    private void Awake()
    {
        locomotion = GetComponent<Controller>();
        boxer = GetComponent<BoxerPunchController>();
        study = GetComponent<AimStudyBoxer>();
        lastRootPosition = transform.position;
        lastYaw = transform.eulerAngles.y;
        scale = Mathf.Max(0.5f, transform.lossyScale.y);

        Animator animator = GetComponent<Animator>();
        if (animator != null && animator.isHuman)
        {
            feet[0].bone = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            feet[1].bone = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        }

        stepPrimary = AudioVariants.BuildPrimary(stepClips, 2f, -2f);
        stepSoft = AudioVariants.BuildSoft(stepClips, 1800f);
        squeaks = AudioVariants.BuildPrimary(squeakClips, 2.5f, -2.5f);

        pool = new AudioVariants.VoicePool(transform, 5, 1.2f * scale, 25f * scale);

        WireLegsAnimator();

        if (stepPrimary.IsEmpty)
            Debug.LogWarning("FootstepAudio: no step clips assigned. Use Upgrade Selected Aim Study Boxer for the study rig, " +
                             "or Rebuild Boxing System for the legacy rig.", this);
    }

    /// <summary>
    /// Become Legs Animator's step receiver. Its UseEvents flag re-reads StepInfoReceiver every update, and
    /// InitializeGetStepInfoReceiver() is public, so this works whenever it runs — no package edits needed.
    /// </summary>
    private void WireLegsAnimator()
    {
        if (legsAnimator == null) legsAnimator = GetComponentInChildren<LegsAnimator>(true);
        useFallback = legsAnimator == null;
        if (useFallback) return;

        legsAnimator.StepInfoReceiver = transform;
        legsAnimator.SendOnMovingGlue = true;    // walking steps, not just idle gluing
        legsAnimator.SendOnStopping = true;      // the settle at the end of footwork
        legsAnimator.InitializeGetStepInfoReceiver();
    }

    private void ResetTracking()
    {
        studyCadence.Reset();
        lastRootPosition = transform.position;
        lastYaw = transform.eulerAngles.y;
        yawRate = 0f;
        pendingSqueakAt = -1f;
        slowMoveDirection = Vector3.zero;
        foreach (Foot foot in feet)
        {
            foot.hasLast = foot.airborne = false;
            foot.velocity = Vector3.zero;
            foot.nextStep = 0f;
        }
    }

    private void OnEnable()
    {
        ResetTracking();
        if (boxer != null) boxer.PunchThrown += OnPunchThrown;
    }

    private void OnDisable()
    {
        ResetTracking();
        if (boxer != null) boxer.PunchThrown -= OnPunchThrown;
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f || !Application.isFocused)
        {
            ResetTracking();
            return;
        }
        float travel = Vector3.ProjectOnPlane(transform.position - lastRootPosition, Vector3.up).magnitude;
        lastRootPosition = transform.position;

        // How fast the boxer is turning (pivot squeaks) and which way he has BEEN going (reversal squeaks).
        float yaw = transform.eulerAngles.y;
        yawRate = Mathf.Lerp(yawRate, Mathf.DeltaAngle(lastYaw, yaw) / dt, 0.25f);
        lastYaw = yaw;
        if (MoveSpeed > 0.2f)
            slowMoveDirection = Vector3.Slerp(slowMoveDirection.sqrMagnitude > 0.01f ? slowMoveDirection : MoveDirection,
                                              MoveDirection, 1f - Mathf.Exp(-3f * dt));

        // A dash-step grips the boards the moment it fires.
        if (locomotion != null && locomotion.LastDashTime > lastDashSeen)
        {
            lastDashSeen = locomotion.LastDashTime;
            TrySqueak(dashSqueak, FastestFootPosition(), 1f);
        }

        // The delayed punch-pivot squeak (the rear foot grips a beat after the hips fire).
        if (pendingSqueakAt > 0f && Time.time >= pendingSqueakAt)
        {
            pendingSqueakAt = -1f;
            TrySqueak(1f, pendingSqueakPos, pendingSqueakVolume);   // the chance was rolled when it was queued
        }

        // Foot velocities are always tracked (dash squeaks aim at the faster foot); plants only fire from the
        // bones when no Legs Animator owns the feet.
        for (int i = 0; i < 2; i++) TrackFoot(feet[i], dt);
        UpdateStudyShuffle(travel, dt);
    }

    // ---------------------------------------------------------------- Legs Animator steps (the real thing)

    /// <summary>
    /// A leg landed — straight from Legs Animator's glue logic, with the step's own position and power.
    /// This is the "when the character moves a foot and drops it on the ground" moment, per foot, exactly.
    /// </summary>
    public void LegAnimatorStepEvent(LegsAnimator.Leg leg, float power, bool isRight, Vector3 position,
                                     Quaternion rotation, LegsAnimator.EStepType type)
    {
        if (!isActiveAndEnabled || Time.deltaTime <= 0f || !Application.isFocused) return;
        studyCadence.Reset();
        lastPlantTime = Time.time;
        int side = isRight ? 1 : 0;
        if (Time.time < feet[side].nextStep) return;   // event bursts must not machine-gun
        feet[side].nextStep = Time.time + 0.12f;

        float p = Mathf.Clamp01(power <= 0f ? 1f : power);
        float moveSpeed = MoveSpeed;
        bool recentDash = locomotion != null && Time.time - locomotion.LastDashTime < 0.35f;
        float gait = Mathf.InverseLerp(0.15f, 4.5f, moveSpeed + (recentDash ? 1.5f : 0f));

        switch (type)
        {
            case LegsAnimator.EStepType.IdleGluing:
                // The foot quietly finding a better spot under the stance.
                if (!idleShuffles) return;
                Play(stepSoft.IsEmpty ? stepPrimary : stepSoft, position,
                     idleShuffleVolume * Mathf.Lerp(0.6f, 1f, p), 1.04f);
                TrySqueak(ambientSqueak * 0.5f, position, 0.5f);
                break;

            case LegsAnimator.EStepType.OnStopping:
                // Footwork ends: the settle — and the classic moment a sneaker chirps.
                Play(stepSoft.IsEmpty ? stepPrimary : stepSoft, position,
                     Mathf.Lerp(walkVolume, sprintVolume, gait) * 0.7f * p, 0.97f);
                TrySqueak(Mathf.Max(stopSqueak * Mathf.Clamp01(gait + 0.3f), ambientSqueak), position, Mathf.Lerp(0.6f, 1f, gait));
                break;

            case LegsAnimator.EStepType.OnLanding:
                // Grounded again — the heaviest plant of all.
                Play(stepPrimary, position, sprintVolume * p, 0.94f);
                TrySqueak(ambientSqueak * 2f, position, 1f);
                break;

            default:   // MovementGluing — an ordinary step of the footwork
                StepWithSqueakRolls(position, gait, p);
                break;
        }
    }

    /// <summary>A walking/strafing step: gait decides the take, the volume and the squeak physics.</summary>
    private void StepWithSqueakRolls(Vector3 position, float gait, float power, bool resetTravel = true)
    {
        if (resetTravel) studyCadence.Reset();
        lastPlantTime = Time.time;
        AudioClip take = gait < 0.45f && !stepSoft.IsEmpty ? stepSoft.Next() : stepPrimary.Next();
        float volume = Mathf.Lerp(walkVolume, sprintVolume, gait) * power;
        float pitch = Mathf.Lerp(1.03f, 0.95f, gait);
        pool.Play(take, position, volume * masterVolume, pitch * Jitter());

        // Would rubber squeak here? Pivoting, circling hard, or snapping back the way you came.
        float pivot = Mathf.Clamp01(Mathf.Abs(yawRate) / 140f);
        float strafe = UseStudyMovement ? Mathf.Abs(transform.InverseTransformDirection(MoveDirection).x)
            : locomotion != null ? Mathf.Abs(locomotion.LocalMove.x) : 0f;
        float moveSpeed = MoveSpeed;
        bool reversal = slowMoveDirection.sqrMagnitude > 0.01f && moveSpeed > 1f
                        && Vector3.Dot(slowMoveDirection.normalized, MoveDirection) < -0.2f;

        float chance = ambientSqueak;
        chance = Mathf.Max(chance, pivotSqueak * pivot);
        chance = Mathf.Max(chance, pivotSqueak * 0.7f * Mathf.InverseLerp(0.6f, 1f, strafe));
        if (reversal) chance = Mathf.Max(chance, reversalSqueak);
        TrySqueak(chance, position, Mathf.Lerp(0.6f, 1f, gait));
    }

    private void Play(AudioVariants.Bank bank, Vector3 position, float volume, float pitch)
    {
        if (bank == null || bank.IsEmpty) return;
        pool.Play(bank.Next(), position, volume * masterVolume, pitch * Jitter());
    }

    // ---------------------------------------------------------------- Fallback plants (no Legs Animator)

    private void TrackFoot(Foot foot, float dt)
    {
        if (foot.bone == null) return;

        Vector3 position = foot.bone.position;
        if (!foot.hasLast)
        {
            foot.lastPosition = position;
            foot.hasLast = true;
            return;
        }

        foot.velocity = Vector3.Lerp(foot.velocity,
            (position - foot.lastPosition) / dt - (UseStudyMovement ? MoveVelocity : Vector3.zero), 0.5f);
        foot.lastPosition = position;
        if (!useFallback) return;   // Legs Animator owns the plants; we only needed the velocity

        float speed = foot.velocity.magnitude;
        float height = position.y - transform.position.y;

        if (speed > liftSpeed * scale || height > plantHeight * scale * 1.5f)
        {
            foot.airborne = true;
            return;
        }

        bool planted = speed < plantSpeed * scale && height < plantHeight * scale;
        if (!foot.airborne || !planted || Time.time < foot.nextStep) return;

        foot.airborne = false;
        foot.nextStep = Time.time + 0.22f;

        float moveSpeed = MoveSpeed;
        bool recentDash = locomotion != null && Time.time - locomotion.LastDashTime < 0.35f;
        if (moveSpeed < minMoveSpeed && !recentDash) return;   // idle sway and punch leg-work are not footwork

        StepWithSqueakRolls(position, Mathf.InverseLerp(minMoveSpeed, 4.5f, moveSpeed + (recentDash ? 1.5f : 0f)), 1f);
    }

    // ---------------------------------------------------------------- Squeaks

    /// <summary>The rear foot twists into a big punch — the boxer's pivot, a beat after the hips fire.</summary>
    private void OnPunchThrown(BoxerPunchController.Hand hand, BoxerPunchController.PunchType type, float overdrive)
    {
        bool bigPivot = type == BoxerPunchController.PunchType.Hook
                     || type == BoxerPunchController.PunchType.Uppercut
                     || overdrive > 0.5f;
        if (!bigPivot || Random.value > punchPivotSqueak * (0.6f + 0.6f * overdrive)) return;

        // Orthodox: the rear (right) foot pivots on the power side; the lead foot on lead-hand hooks.
        Foot foot = hand == BoxerPunchController.Hand.Right ? feet[1] : feet[0];
        if (foot.bone == null) return;
        pendingSqueakAt = Time.time + Random.Range(0.04f, 0.1f);
        pendingSqueakPos = foot.bone.position;
        pendingSqueakVolume = 0.55f + 0.35f * overdrive;
    }

    private void UpdateStudyShuffle(float travel, float dt)
    {
        if (study == null || !useFallback) return;
        bool moving = study.isActiveAndEnabled && MoveSpeed >= minMoveSpeed
            && travel <= Mathf.Max(scale, MoveSpeed * dt * 3f);
        if (!studyCadence.Advance(travel, 0.65f * scale, moving)) return;
        if (Time.time - lastPlantTime < 0.22f) return;
        Foot foot = feet[shuffleFoot];
        if (foot.bone == null || foot.bone.position.y - transform.position.y > plantHeight * scale)
            foot = feet[1 - shuffleFoot];
        if (foot.bone == null || foot.bone.position.y - transform.position.y > plantHeight * scale) return;
        shuffleFoot = 1 - shuffleFoot;
        foot.nextStep = Time.time + 0.22f;
        StepWithSqueakRolls(foot.bone.position, Mathf.InverseLerp(minMoveSpeed, 4.5f, MoveSpeed), 0.45f, false);
    }

    private void TrySqueak(float chance, Vector3 position, float volume01)
    {
        if (squeaks.IsEmpty || Time.time < nextSqueak) return;
        if (Random.value > Mathf.Clamp01(chance)) return;

        nextSqueak = Time.time + squeakInterval * Random.Range(0.85f, 1.3f);
        pool.Play(squeaks.Next(), position, squeakVolume * Mathf.Clamp01(volume01) * masterVolume,
                  Random.Range(0.94f, 1.1f));
    }

    private Vector3 FastestFootPosition()
    {
        Foot best = feet[0].bone != null ? feet[0] : feet[1];
        if (feet[0].bone != null && feet[1].bone != null && feet[1].velocity.sqrMagnitude > feet[0].velocity.sqrMagnitude)
            best = feet[1];
        return best?.bone != null ? best.bone.position : transform.position;
    }

    private float Jitter() => 1f + Random.Range(-pitchJitter, pitchJitter);
}
