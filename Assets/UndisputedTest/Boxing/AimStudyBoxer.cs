using System.Collections.Generic;
using Animancer;
using RootMotion.FinalIK;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

/// <summary>
/// Animancer plays the animation, AimIK aims it. Three stances, a jab on each trigger, footwork
/// on the left stick, aim on the right stick.
/// </summary>
/// <remarks>
/// Two systems only, each doing one job:
///
///   <b>Animancer</b> owns playback — the stance loops on layer 0, the punch plays on layer 1
///   masked to the upper body so the stance keeps the legs. No Animator Controller asset.
///
///   <b>AimIK</b> owns direction — it rotates upper arm and forearm until the fist's own axis
///   points at the aim circle. It is a ROTATION solver, so it never drags the hand toward the
///   target; the punch stays the animation's own motion and simply leaves on a better line.
///
/// The aim axis is measured off the rig (wrist to middle knuckle) rather than assumed, and the
/// IK weight ramps on a window centred on each clip's MEASURED strike frame — the two jabs
/// strike at 0.40 and 0.55 of their length, so a shared value would fire one of them early.
/// </remarks>
// LateUpdate order matters: Unity applies the animation before any LateUpdate, and FinalIK
// solves in its own at the default order. Sitting at -20 means the orientation fix lands on
// the posed skeleton and the IK then solves on the corrected body, not the twisted one.
[DefaultExecutionOrder(-20)]
[RequireComponent(typeof(AnimancerComponent))]
[RequireComponent(typeof(UndisputedClipEvents))]
public class AimStudyBoxer : MonoBehaviour
{
    [Header("Stances — 1 / 2 / 3")]
    public AnimationClip HighEnergy;
    public AnimationClip MidEnergy;
    public AnimationClip LowEnergy;

    [Range(0, 2)] public int Stance = 1;
    public float StanceFade = 0.25f;

    [Tooltip("Drop to the gassed stance as stamina runs out. The low-energy idle IS the gassed " +
             "capture, so this costs nothing and reads immediately.")]
    public bool StanceFollowsStamina = true;

    [Header("Punches — L2 / LMB and R2 / RMB")]
    public AnimationClip LeftJab;
    public AnimationClip RightJab;

    [Header("Neutral-trigger jab clips — optional overrides")]
    [Tooltip("L2 with a centred right stick. Empty uses Left Jab. Uses the existing left strike/height/orientation tuning.")]
    public AnimationClip LeftTriggerJab;
    [Tooltip("R2 with a centred right stick. Empty uses Right Jab. Uses the existing right strike/height/orientation tuning.")]
    public AnimationClip RightTriggerJab;

    public AnimationClip JabClip(int hand) => hand == 0
        ? (LeftTriggerJab != null ? LeftTriggerJab : LeftJab)
        : (RightTriggerJab != null ? RightTriggerJab : RightJab);

    [Tooltip("Normalised time the glove is fully out, measured off the rig by the setup tool.")]
    [Range(0f, 1f)] public float LeftStrike = 0.404f;
    [Range(0f, 1f)] public float RightStrike = 0.551f;

    [System.Serializable]
    public class PunchMotion
    {
        public AnimationClip Clip;
        public Boxing.Hand Hand;
        public Boxing.PunchType Type;
        public Boxing.PunchTarget Target;
        [Range(0.05f, 0.9f)] public float Strike = 0.45f;
        [Range(-180f, 180f)] public float Orientation;
        [Range(-40f, 40f)] public float Height;
        [Range(0.1f, 0.8f)] public float ImpactSeconds = 0.32f;
    }

    [Header("Tap / hold / release")]
    [Tooltip("Press begins preparation; release throws. Short taps stay quick. Disable to restore immediate press-to-punch input.")]
    public bool ReleaseToPunch = true;
    [Range(0.03f, 0.2f)] public float TapGrace = 0.1f;
    [Range(0.2f, 1.5f)] public float FullChargeTime = 0.7f;
    [Range(0.08f, 0.4f)] public float PreparationSeconds = 0.18f;
    [Min(0f)] public float HoldStaminaPerSecond = 8f;
    [Range(0f, 0.4f)] public float ChargePowerBonus = 0.25f;
    public bool IsPreparing => _Preparing;
    public float Charge(int hand) => hand >= 0 && hand < 2 && _Holds[hand].Active
        ? PunchPlayback.ChargeAmount(_Holds[hand].HeldSeconds, TapGrace, FullChargeTime) : 0f;

    [Header("Punch vocabulary")]
    public PunchMotion[] Punches = new PunchMotion[0];
    public bool DirectionalPunches = true;
    [Range(0.1f, 0.5f)] public float InputBufferSeconds = 0.3f;
    [Range(0f, 0.3f)] public float ComboAcceleration = 0f;
    [Range(0.2f, 1.5f)] public float ComboWindow = 0.9f;
    [Range(0f, 1f)] public float HookAimScale = 0.25f;
    [Range(0f, 1f)] public float UppercutAimScale = 0.45f;
    [Range(0.01f, 0.2f)] public float CorrectionSmoothing = 0.045f;
    public BoxerHitboxes Hitboxes;
    public CameraFollow FeedbackCamera;
    [Range(0f, 1f)] public float ContactShake = 0.18f;
    public event System.Action<int, Boxing.PunchType> PunchStarted;
    public event System.Action<int, PunchingBag.HitInfo> HitLanded;

    public enum Defense { None, Body, Head }

    [Header("Defense — L1 body / R1 head")]
    public AnimationClip HeadDefense;
    public AnimationClip BodyDefense;
    [Range(0f, 1f)] public float HeadDefensePose = 0.5f;
    [Range(0f, 1f)] public float BodyDefensePose = 0.5f;
    [Range(0.04f, 0.4f)] public float DefenseBlend = 0.16f;
    [Range(0f, 1f)] public float DefenseTorsoWeight = 0.35f;
    [Range(0.3f, 1f)] public float DefenseMoveScale = 0.75f;
    public float HeadGuardWeight { get; private set; }
    public float BodyGuardWeight { get; private set; }
    public bool IsDefending => HeadGuardWeight + BodyGuardWeight > 0.1f;

    [Header("Right-stick body movement")]
    public bool EnableLean = true;
    [Range(0f, 0.4f)] public float LeanDeadzone = 0.15f;
    [Range(0f, 35f)] public float SideLeanDegrees = 28f;
    [Range(0f, 35f)] public float BackLeanDegrees = 30f;
    [Range(0f, 25f)] public float ForwardLeanDegrees = 18f;
    [Range(0.04f, 0.35f)] public float LeanResponse = 0.1f;
    [Range(0f, 1f)] public float LeanHeadStability = 0.55f;
    [Range(0f, 6f)] public float ContactRecoilDegrees = 2.5f;
    public Vector2 LeanInput => _Lean;

    [Header("Early punch steering")]
    [Tooltip("Use small right-stick corrections during preparation. Release locks the aim. In press-to-punch mode, steering locks before impact. This takes precedence over the older wide aim circle.")]
    public bool LivePunchSteering = true;
    [Tooltip("Maximum correction in metres at character scale. Uses the existing AimIK weight; 0 disables steering.")]
    [Range(0f, 0.2f)] public float PunchSteerDistance = 0.09f;
    [Range(0.02f, 0.2f)] public float PunchSteerResponse = 0.055f;
    [Range(0.3f, 0.9f)] public float TechniqueThreshold = 0.55f;

    [Header("How much of the body joins the punch")]
    [Tooltip("The punching arm is always full. These two say how much the REST of him comes " +
             "along: 1 is the whole animation, 0 leaves that part in the stance.")]
    [Range(0f, 1f)] public float BodyFollow = 1f;

    [Tooltip("The hand that is NOT punching. Low keeps the guard up while the other hand works.")]
    [Range(0f, 1f)] public float OffHandFollow = 1f;

    [Header("Masks (filled by the setup tool)")]
    public AvatarMask TorsoMask;
    public AvatarMask LeftArmMask;
    public AvatarMask RightArmMask;

    [Tooltip("Seconds to blend the punch in. Too short and it snaps; too long and it feels soft.")]
    public float PunchFade = 0.12f;

    [Tooltip("Seconds to blend back to the stance. Longer than the fade in, so the arm settles.")]
    public float RecoverFade = 0.28f;

    [Tooltip("How far through a punch the next one may interrupt it, 0-1. Lower gives faster " +
             "combinations; the crossfade covers the join.")]
    [Range(0.3f, 1f)] public float ReplayAfter = 0.6f;

    [Tooltip("Playback speed of the punch clips. The mocap is real speed; raise for arcade feel.")]
    [Range(0.3f, 2.5f)] public float PunchSpeed = 1f;

    [Tooltip("Scale each hand so BOTH land at the same moment. The two jabs are separate " +
             "captures - the right takes 556 ms to reach its strike, the left 280 ms - so " +
             "without this one hand always feels sluggish next to the other.")]
    public bool MatchImpactTiming = true;

    [Tooltip("Use each motion's Impact Seconds instead of the shared Impact Time. Leave off to match all punch types and both hands.")]
    public bool UsePerMotionTiming;

    [Tooltip("Seconds from the punch starting to the glove landing, once matched.")]
    [Range(0.1f, 0.8f)] public float ImpactTime = 0.3f;

    [Header("Stamina — a tired jab is a slower jab")]
    public bool UseStamina = true;

    [Min(1f)] public float MaxStamina = 100f;

    [Tooltip("Cost of one punch.")]
    [Min(0f)] public float StaminaPerPunch = 11f;

    [Tooltip("Recovered per second once he stops throwing.")]
    [Min(0f)] public float StaminaRecovery = 16f;

    [Tooltip("Seconds after a punch before recovery starts, so a flurry really does drain him.")]
    [Min(0f)] public float RecoveryDelay = 0.7f;

    [Tooltip("Playback speed at zero stamina. 0.22 makes exhausted punches visibly laboured without freezing them.")]
    [Range(0.1f, 1f)] public float TiredSpeed = 0.22f;

    [Header("Aim target")]
    [Tooltip("What he aims at when the stick is centred. Created in front of him by the setup " +
             "tool. Without one the aim is a point straight ahead — and NOT the mouse, which " +
             "would otherwise park the aim wherever the cursor happens to sit.")]
    public Transform Target;

    [Tooltip("Let the mouse steer the aim when no gamepad is connected. Off by default: the " +
             "mouse has no centre to return to, so it holds a permanent offset and the torso " +
             "leans that way forever.")]
    public bool UseMouseAim;

    [Header("Aim circle — right stick")]
    [Tooltip("Off = a jab is just a jab. It goes straight at the target and the right stick is " +
             "ignored. Turn it on to steer the shot around the aim circle again.")]
    public bool AimFromStick;

    public float AimDistance = 0.55f;

    [Tooltip("Radius of the circle the right stick sweeps, in metres.")]
    public float AimRadius = 0.22f;

    public float HeadHeight = 1.35f;

    [Tooltip("How far the aim drops at full down — this is the body shot.")]
    public float BodyDrop = 0.45f;

    [Tooltip("Seconds for the aim to follow the stick.")]
    public float AimSmoothing = 0.06f;

    [Header("AimIK — chain")]
    [Tooltip("Include the spine. The chain rotates from the waist up, which turns the whole " +
             "body into the punch AND carries the shoulder forward, so the fist ends up " +
             "noticeably closer to the target than an arm-only chain can get.")]
    public bool IncludeSpine = true;

    public bool IncludeChest = true;
    public bool IncludeUpperChest = true;
    public bool IncludeUpperArm = true;
    public bool IncludeForeArm = true;

    [Tooltip("How much each spine/chest bone contributes. Raise it for more body rotation and " +
             "more reach; lower it to keep the torso still.")]
    [Range(0f, 1f)] public float TorsoWeight = 0.4f;

    [Tooltip("How much the FOREARM may turn. Keep this low for straight punches: at full weight " +
             "the solver bends the elbow to satisfy the aim, which folds the arm mid-flight " +
             "instead of driving it out. The aim should come from the shoulder and torso.")]
    [Range(0f, 1f)] public float ForeArmWeight = 0.15f;

    [Tooltip("How much the UPPER ARM may turn. This is where a straight punch should be aimed from.")]
    [Range(0f, 1f)] public float UpperArmWeight = 1f;

    [Header("AimIK — solver")]
    [Tooltip("0 = the animation alone drives the punch and AimIK does nothing. Raise it only " +
             "once the animation itself looks right; IK cannot rescue a mismatched clip.")]
    [Range(0f, 1f)] public float AimWeight;

    [Range(1, 12)] public int Iterations = 4;

    [Tooltip("Stop once the aim is this close, in degrees. 0 = always run every iteration.")]
    [Range(0f, 10f)] public float Tolerance = 0f;

    [Tooltip("How far the aim may pull from the animated direction. 0 = free, 1 = pinned to " +
             "the animation. Low values let the aim reach further off-centre.")]
    [Range(0f, 1f)] public float ClampWeight = 0f;

    [Tooltip("Off = this script stops writing iterations / tolerance / clamp, so those fields " +
             "on the AimIK components become authoritative and editable during play.")]
    public bool DriveSolverSettings = true;

    [Tooltip("Width of the window around the strike where the aim is live, in normalised time.")]
    [Range(0.05f, 0.6f)] public float AimWindow = 0.3f;

    [Tooltip("Hold the aim this long past the strike so it releases rather than snapping off.")]
    [Min(0f)] public float HoldAfterStrike = 0.1f;

    [Header("Orientation fix — tune live")]
    [Tooltip("Counter-rotate the body while a punch plays, cancelling the heading difference " +
             "between the punch clip and the idle. Each Undisputed clip carries its own " +
             "m_OrientationOffsetY from whatever way the performer stood: idle 180, left jab " +
             "185.3, right jab 192 — so the right twists 12 degrees the moment it blends in.")]
    public bool CorrectOrientation = true;

    [Tooltip("Degrees for the LEFT jab. Found by eye, not from the clip metadata.")]
    [Range(-180f, 180f)] public float LeftOrientationFix = 10f;

    [Tooltip("Degrees for the RIGHT jab. -90 rather than the -12 the clip offsets predicted, " +
             "which says the right jab is authored a quarter turn out rather than merely a few " +
             "degrees off — a different problem from the offset mismatch, same cure here.")]
    [Range(-180f, 180f)] public float RightOrientationFix = -90f;

    [Tooltip("Spread the correction across Spine, Chest and UpperChest instead of putting it " +
             "all on one bone. Two reasons: the legs are children of the Hips, so correcting " +
             "there swings the stance from orthodox to southpaw — and 90 degrees on a single " +
             "joint shears the waist. Split three ways it is 30 each, which the spine can " +
             "actually do, and the feet never move.")]
    public bool SpreadAcrossSpine = true;

    [Tooltip("Used only when the correction is NOT spread. Hips turns the whole body, legs " +
             "included — which is what flips the stance.")]
    public HumanBodyBones CorrectionBone = HumanBodyBones.Spine;

    [Header("Jab height")]
    [Tooltip("Pitch the punching arm up or down, in degrees. Positive aims higher. This tilts " +
             "the arm itself rather than only moving the aim point, so it works even with " +
             "AimIK at zero weight.")]
    [Range(-40f, 40f)] public float LeftJabHeight;

    [Range(-40f, 40f)] public float RightJabHeight;

    [Tooltip("How much of the pitch the shoulder takes rather than the upper arm. A little " +
             "shoulder makes a raised jab look lifted instead of just tilted.")]
    [Range(0f, 1f)] public float ShoulderShare = 0.35f;

    [Header("Head")]
    [Tooltip("Cancel the orientation correction at the head, so the twist stays in the torso " +
             "and the head keeps looking where it was. The head is a child of the spine, so " +
             "without this it inherits the whole 90 degrees.")]
    public bool StabiliseHead = true;

    [Tooltip("1 = the head ignores the correction entirely. 0 = it rides along with the torso.")]
    [Range(0f, 1f)] public float HeadStability = 1f;

    [Tooltip("Also steady the neck, by this fraction of the head amount. A little is enough — " +
             "cancelling both fully can look stiff.")]
    [Range(0f, 1f)] public float NeckStability = 0.5f;

    [Header("Solvers (filled by the setup tool)")]
    public AimIK LeftAim;
    public AimIK RightAim;

    [Header("Movement — left stick / WASD")]
    public float MoveSpeed = 1.5f;
    public float Acceleration = 10f;
    public float TurnSpeed = 200f;
    public Vector3 MovementVelocity => isActiveAndEnabled ? _Velocity + _ImpactVelocity : Vector3.zero;

    [Header("Debug")]
    [Tooltip("Log why a punch did or did not happen. Leave on until it is behaving.")]
    public bool Verbose = true;

    public bool ShowPanel = true;
    public bool DrawGizmos = true;

    private AnimancerComponent _Animancer;
    private AnimancerLayer _Base, _Punch;
    private AnimancerState _PunchState;

    // 0 = torso, 1 = left arm, 2 = right arm. Fixed masks; only the weights change, so the
    // punching arm gets the full clip while the torso and off hand take their percentage.
    private readonly AnimancerLayer[] _Parts = new AnimancerLayer[3];
    private readonly AnimancerState[] _PartStates = new AnimancerState[3];

    private struct ShotRequest
    {
        public int Hand;
        public Boxing.PunchType Type;
        public Boxing.PunchTarget Target;
        public Vector2 Aim;
        public Vector2 Control;
        public int Sequence;
        public float Charge;
        public AnimationClip Clip;
        public PunchMotion Motion;
    }

    private struct MotionSample
    {
        public int Hand;
        public float Orientation;
        public float Height;
        public float AimScale;
        public Vector3 LocalAim;
        public PunchPlayback.Timing Timing;
        public bool Legacy;
    }

    private readonly AnimancerLayer[] _DefenseParts = new AnimancerLayer[3];
    private readonly AnimancerState[,] _DefenseStates = new AnimancerState[3, 2];
    private Defense _GuardRequested;
    private Defense _GuardPlaying;
    private AnimationClip _GuardClip;
    private Vector2 _ControlStick;
    private Vector2 _Lean, _LeanVelocity;
    private Vector2 _ContactRecoil, _ContactRecoilVelocity;
    private Vector2 _SteerOrigin, _SteerOffset;
    private Vector3 _ShotAimBase;
    private readonly bool[] _PunchHeld = new bool[2];
    private readonly List<Transform> _LeanChain = new List<Transform>(3);

    private readonly PunchHold[] _Holds = { new PunchHold(), new PunchHold() };
    private readonly ShotRequest[] _HeldRequests = new ShotRequest[2];
    private bool _Preparing;
    private int _PreparationSequence = -1;
    private int _RequestSequence;
    private float _PreparationElapsed;
    private float _ReleasedCharge;
    private float _PunchPower = 1f;
    private float _LastStaminaSpend = -99f;

    private readonly PunchBuffer<ShotRequest> _InputBuffer = new PunchBuffer<ShotRequest>(2);
    private readonly Dictionary<AnimancerState, MotionSample> _Samples = new Dictionary<AnimancerState, MotionSample>();
    private readonly AnimationClip[] _LastClips = new AnimationClip[2];
    private readonly float[] _AimWeights = new float[2];
    private readonly Vector3[] _HandAim = new Vector3[2];
    private readonly float[] _HeightBlend = new float[2];
    private readonly PunchHitbox[] _Sensors = new PunchHitbox[2];
    private readonly AvatarMask[] _RuntimeMasks = new AvatarMask[3];
    private PunchPlayback.Timing _Timing;
    private Boxing.PunchType _ShotType;
    private bool _ContactLanded;
    private bool _ImpactRecorded;
    private int _Combo;
    private float _CorrectionVelocity;
    private float _ActiveImpactTime;

    public AimStudyBoxer Opponent;
    public bool IsPlayerControlled => _Input == null || _Input.IsPlayer;
    public bool IsPunching => _PunchState != null && !_Preparing;
    public int AttackSequence { get; private set; }
    public Boxing.PunchTarget AttackTarget { get; private set; }
    public bool InputBodyShot { get; set; }
    public bool CombatLocked { get; set; }
    public float MovementMultiplier { get; set; } = 1f;
    private IBoxerInput _Input;
    private readonly bool[] _InputPressed = new bool[2];
    private Vector3 _ImpactVelocity;
    private CharacterController _Mover;
    private Animator _OpponentAnimator;

    public void SetInput(IBoxerInput input)
    {
        if (ReferenceEquals(_Input, input)) return;
        CancelAttacks();
        _Input = input;
    }
    public void CancelAttacks()
    {
        CancelHolds();
        _InputBuffer.Clear();
        EndPunch();
    }
    public void SpendStaminaExternal(float amount) => SpendStamina(amount);
    public void AddImpulse(Vector3 velocity) => _ImpactVelocity = Vector3.ClampMagnitude(_ImpactVelocity + velocity, 2.5f);
    public void ResetCombat()
    {
        CancelAttacks();
        CombatLocked = false;
        MovementMultiplier = 1f;
        _Stamina = MaxStamina;
        _Combo = 0;
        _LastPunch = _LastStaminaSpend = -99f;
        _Velocity = _ImpactVelocity = Vector3.zero;
        _Lean = _LeanVelocity = _ContactRecoil = _ContactRecoilVelocity = Vector2.zero;
    }

    private int _Hand = -1;
    private int _LiveStance = -1;
    private Vector2 _Aim, _AimVelocity;
    private Vector3 _AimWorld;
    private Vector3 _Velocity;
    private float _Weight;
    private string _Last = "-";
    private string _ChainText = "";
    private float _Stamina = -1f;
    private float _FixApplied;
    private float _HeightApplied;

    private static readonly HumanBodyBones[] SpineBones =
    {
        HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
    };

    private readonly System.Collections.Generic.List<Transform> _SpineChain =
        new System.Collections.Generic.List<Transform>();
    private float _LastPunch = -99f;

    /// <summary>
    /// Real time by which the punch must be over. A punch only clears itself when the clip
    /// reaches its end, so anything that makes it play very slowly -- a heavy fatigue
    /// multiplier, a short strike time against a long impact time -- would leave it running
    /// forever, and ReadPunch would refuse every press after it. This is the backstop.
    /// </summary>
    private float _PunchDeadline;

    /// <summary>
    /// How present the punch is, 0 to 1, rising over PunchFade and falling over RecoverFade.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT any layer's weight. The orientation fix and the jab height used to
    /// read the torso layer, which is exactly what BodyFollow turns down -- so dialling the
    /// body back to 0.5 quietly halved a tuned -90 degree correction to -45 and the right
    /// jab pointed wrong again. The corrections describe the punch, not how much of the body
    /// joins it, so they get their own envelope and the sliders cannot reach them.
    /// </remarks>
    private float _PunchWeight;

    private UndisputedClipEvents _ClipEvents;

    /// <summary>Real time of the last authored impact, for the on-screen readout.</summary>
    private float _LastImpact = -99f;

    /// <summary>
    /// Fired by the clip itself, on the frame the punch was authored to land.
    /// </summary>
    /// <remarks>
    /// This is the real thing, not the peak-extension guess in LeftStrike/RightStrike --
    /// those disagree with it by a median of 7% of clip length. Nothing consumes it yet; it
    /// is where a hit test or a camera shake belongs once there is something to hit.
    /// </remarks>
    private void OnAuthoredImpact()
    {
        if (_Preparing || _PunchState == null || _ImpactRecorded ||
            Mathf.Abs(_PunchState.NormalizedTime - _Timing.Strike) > 0.08f) return;
        _ImpactRecorded = true;
        _LastImpact = Time.time;
    }
    private float _ReachError;
    private int _ChainHash = -1;

    /************************************************************************************/

    private void Awake()
    {
        _Animancer = GetComponent<AnimancerComponent>();
        _Mover = GetComponent<CharacterController>();

        if (_Animancer.Animator == null || !_Animancer.Animator.isHuman)
        {
            Debug.LogError("AimStudyBoxer needs a Humanoid Animator assigned to Animancer.", this);
            enabled = false;
            return;
        }
        _Animancer.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        foreach (HumanBodyBones bone in SpineBones)
        {
            Transform joint = _Animancer.Animator.GetBoneTransform(bone);
            if (joint != null) _LeanChain.Add(joint);
        }

        if (Hitboxes == null) Hitboxes = GetComponent<BoxerHitboxes>();
        if (FeedbackCamera == null && Camera.main != null)
            FeedbackCamera = Camera.main.GetComponent<CameraFollow>();

        Disarm(LeftAim);
        Disarm(RightAim);
    }

    /// <summary>
    /// Builds both AimIK chains from the current toggles. Safe to call at runtime.
    /// </summary>
    /// <remarks>
    /// Including the spine and chest is not decoration. AimIK cannot translate the hand, but
    /// rotating the torso carries the SHOULDER toward the target, and the arm rides along — so a
    /// waist-up chain closes far more of the gap than an arm-only one, and it looks like a punch
    /// rather than a reach. That is the honest answer to "why can the hand not get there".
    /// </remarks>
    [ContextMenu("Rebuild AimIK chains")]
    public void BuildChains()
    {
        Animator animator = _Animancer != null ? _Animancer.Animator : GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            return;

        LeftAim = BuildChain(animator, true, LeftAim);
        RightAim = BuildChain(animator, false, RightAim);
        _ChainHash = ChainHash();
    }

    private int ChainHash()
        => (IncludeSpine ? 1 : 0) | (IncludeChest ? 2 : 0) | (IncludeUpperChest ? 4 : 0)
         | (IncludeUpperArm ? 8 : 0) | (IncludeForeArm ? 16 : 0)
         | (Mathf.RoundToInt(TorsoWeight * 100f) << 5)
         | (Mathf.RoundToInt(ForeArmWeight * 100f) << 13)
         | (Mathf.RoundToInt(UpperArmWeight * 100f) << 21);

    private AimIK BuildChain(Animator animator, bool left, AimIK existing)
    {
        Transform upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        Transform fore = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        Transform hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);

        if (upper == null || fore == null || hand == null)
            return existing;

        var bones = new System.Collections.Generic.List<Transform>();
        var weights = new System.Collections.Generic.List<float>();

        void Add(Transform t, float w)
        {
            if (t != null) { bones.Add(t); weights.Add(w); }
        }

        if (IncludeSpine) Add(animator.GetBoneTransform(HumanBodyBones.Spine), TorsoWeight);
        if (IncludeChest) Add(animator.GetBoneTransform(HumanBodyBones.Chest), TorsoWeight);
        if (IncludeUpperChest) Add(animator.GetBoneTransform(HumanBodyBones.UpperChest), TorsoWeight);
        if (IncludeUpperArm) Add(upper, UpperArmWeight);
        if (IncludeForeArm) Add(fore, ForeArmWeight);

        if (bones.Count == 0)
        {
            _ChainText = "<empty>";
            return existing;
        }

        AimIK ik = existing != null ? existing : gameObject.AddComponent<AimIK>();

        ik.solver.transform = hand;

        // Measured off the rig, never assumed — a guessed axis aims through the elbow.
        Transform knuckle = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal
                                                           : HumanBodyBones.RightMiddleProximal);
        Vector3 dir = knuckle != null ? knuckle.position - hand.position : hand.position - fore.position;
        if (dir.sqrMagnitude > 1e-6f)
            ik.solver.axis = hand.InverseTransformDirection(dir.normalized).normalized;

        ik.solver.target = null;
        ik.solver.poleWeight = 0f;
        ik.solver.SetChain(bones.ToArray(), transform);

        for (int i = 0; i < ik.solver.bones.Length && i < weights.Count; i++)
            ik.solver.bones[i].weight = weights[i];

        if (!left)
            _ChainText = string.Join(" -> ", bones.ConvertAll(t => t.name));

        return ik;
    }

    private static void Disarm(AimIK ik)
    {
        if (ik != null && ik.solver != null)
            ik.solver.IKPositionWeight = 0f;
    }

    private void Start()
    {
        // The ripped clips still fire Undisputed's own gameplay events. Something has to
        // answer them or Unity warns on every call; the receiver must sit on the Animator's
        // GameObject, which is this one. Added here as well as by RequireComponent so a rig
        // built before this existed heals itself instead of needing the setup tool re-run.
        _ClipEvents = GetComponent<UndisputedClipEvents>()
                   ?? gameObject.AddComponent<UndisputedClipEvents>();
        _ClipEvents.Impact += OnAuthoredImpact;

        _Base = _Animancer.Layers[0];

        AvatarMask[] masks = { TorsoMask, LeftArmMask, RightArmMask };

        for (int i = 0; i < _Parts.Length; i++)
        {
            _Parts[i] = _Animancer.Layers[i + 1];

            if (masks[i] == null)
            {
                _RuntimeMasks[i] = CreateRegionMask(i);
                masks[i] = _RuntimeMasks[i];
            }
            _Parts[i].Mask = masks[i];
            _Parts[i].SetLayerWeightOnPlay = false;
            _Parts[i].Weight = 0f;
            _DefenseParts[i] = _Animancer.Layers[i + 4];
            _DefenseParts[i].Mask = masks[i];
            _DefenseParts[i].SetLayerWeightOnPlay = false;
            _DefenseParts[i].Weight = 0f;
        }

        _Punch = _Parts[0];

        if (masks[1] == null || masks[2] == null)
        {
            Debug.LogWarning("AimStudyBoxer: arm masks are missing, so the punch cannot be " +
                "separated from the body. Re-run the setup tool.", this);
        }

        PlayStance(Stance, 0f);

        if (Verbose)
        {
            Debug.Log("AimStudyBoxer ready on " + name, this);
            Debug.Log("   leftJab  " + (JabClip(0) != null ? JabClip(0).name : "EMPTY - left cannot punch"), this);
            Debug.Log("   rightJab " + (JabClip(1) != null ? JabClip(1).name : "EMPTY - right cannot punch"), this);
            Debug.Log("   stance   " + (MidEnergy != null ? MidEnergy.name : "EMPTY"), this);
            Debug.Log("   masks    torso " + (TorsoMask != null) + "  L " + (LeftArmMask != null) + "  R " + (RightArmMask != null), this);
            Debug.Log("   layers   " + _Parts.Length + " built, animancer has " + _Animancer.Layers.Count, this);
            Debug.Log("   follow   body " + BodyFollow.ToString("0.00") + "  offHand " + OffHandFollow.ToString("0.00"), this);
        }
    }

    /************************************************************************************/

    public static AvatarMask CreateRegionMask(int region)
    {
        var mask = new AvatarMask { name = "Punch Region " + region };
        for (AvatarMaskBodyPart part = 0; part < AvatarMaskBodyPart.LastBodyPart; part++)
            mask.SetHumanoidBodyPartActive(part, false);
        if (region == 0) mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
        else
        {
            mask.SetHumanoidBodyPartActive(region == 1 ? AvatarMaskBodyPart.LeftArm : AvatarMaskBodyPart.RightArm, true);
            mask.SetHumanoidBodyPartActive(region == 1 ? AvatarMaskBodyPart.LeftFingers : AvatarMaskBodyPart.RightFingers, true);
        }
        return mask;
    }

    private void OnDisable()
    {
        CancelHolds();
        _InputBuffer.Clear();
        EndPunch();
        _Hand = -1;
        _PunchWeight = _Weight = _FixApplied = _CorrectionVelocity = 0f;
        _Velocity = Vector3.zero;
        for (int i = 0; i < 2; i++)
        {
            _AimWeights[i] = _HeightBlend[i] = 0f;
            if (_Sensors[i] == null) continue;
            _Sensors[i].Armed = false;
            _Sensors[i].PowerScale = 1f;
            _Sensors[i].OnLanded.RemoveListener(OnContact);
            _Sensors[i].OnLandedOnBoxer -= OnBoxerContact;
            _Sensors[i] = null;
        }
        if (_Animancer != null && _Animancer.IsGraphInitialized)
        {
            foreach (AnimancerLayer part in _Parts)
                if (part != null) part.Weight = 0f;
            foreach (AnimancerLayer part in _DefenseParts)
                if (part != null) part.Weight = 0f;
        }
        _GuardRequested = _GuardPlaying = Defense.None;
        _GuardClip = null;
        HeadGuardWeight = BodyGuardWeight = 0f;
        _ControlStick = _Lean = _LeanVelocity = _ContactRecoil = _ContactRecoilVelocity = Vector2.zero;
        _SteerOffset = Vector2.zero;
        _PunchHeld[0] = _PunchHeld[1] = false;
        Disarm(LeftAim);
        Disarm(RightAim);
    }

    private void OnApplicationFocus(bool focused)
    {
        if (!focused)
        {
            CancelHolds();
            _InputBuffer.Clear();
        }
    }

    private void OnDestroy()
    {
        if (_ClipEvents != null)
            _ClipEvents.Impact -= OnAuthoredImpact;
        foreach (AvatarMask mask in _RuntimeMasks)
            if (mask != null) Destroy(mask);
    }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (IsPlayerControlled && Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame) ShowPanel = !ShowPanel;
#endif
        if (_ChainHash != ChainHash())
            BuildChains();

        _Input?.Poll();
        bool controls = Application.isFocused && !CombatLocked;
        _ControlStick = controls ? ReadLook() : Vector2.zero;
        for (int hand = 0; hand < 2; hand++)
        {
            bool held = controls && TriggerHeld(hand);
            _InputPressed[hand] = _Input != null && held && !_PunchHeld[hand];
            _PunchHeld[hand] = held;
        }
        _GuardRequested = controls ? ReadGuard() : Defense.None;
        if (CombatLocked) CancelAttacks();
        RecoverStamina(Time.deltaTime);
        ReadStance();
        ReadAim();
        HookContacts();

        if (!_Preparing && _PunchState != null &&
            (!_PunchState.IsPlaying || _PunchState.NormalizedTime >= _Timing.End || Time.time > _PunchDeadline))
            EndPunch();

        UpdateDefense();
        ReadPunch();
        Move();
        SyncParts();
        UpdatePunchSteering(Time.deltaTime);
        UpdateContacts();
        AdvancePunchWeight(Time.deltaTime);
        UpdateLean(Time.deltaTime);
        Advance(Time.deltaTime);

        // Release the hand only once the punch has faded, so nothing it drives can snap.
        if (_PunchState == null && _Hand >= 0 && _PunchWeight < 0.01f)
            _Hand = -1;
    }

    /// <summary>Filled lazily so the inspector's MaxStamina is respected on the first frame.</summary>
    public float Stamina
    {
        get
        {
            if (_Stamina < 0f)
                _Stamina = MaxStamina;
            return _Stamina;
        }
    }

    public float Stamina01 => MaxStamina > 0f ? Mathf.Clamp01(Stamina / MaxStamina) : 1f;

    /// <summary>Speed multiplier from fatigue: 1 when fresh, TiredSpeed when empty.</summary>
    private float Fatigue() => UseStamina ? PunchPlayback.StaminaSpeed(Stamina01, TiredSpeed) : 1f;

    /// <summary>
    /// Refills once he stops throwing. The delay is what makes a flurry cost something — without
    /// it stamina creeps back between punches and he never actually tires.
    /// </summary>
    private void RecoverStamina(float dt)
    {
        if (!UseStamina || _Holds[0].Active || _Holds[1].Active || _PunchState != null
            || Time.time - _LastStaminaSpend < RecoveryDelay)
            return;

        _Stamina = Mathf.Min(MaxStamina, Stamina + StaminaRecovery * dt);
    }

    private void ReadStance()
    {
        int wanted = Stance;
        if (IsPlayerControlled && Pressed(1)) wanted = 0;
        else if (IsPlayerControlled && Pressed(2)) wanted = 1;
        else if (IsPlayerControlled && Pressed(3)) wanted = 2;
        else if (StanceFollowsStamina && UseStamina)
            wanted = Stamina01 > 0.66f ? 0 : Stamina01 > 0.33f ? 1 : 2;

        if (wanted != _LiveStance)
        {
            Stance = wanted;
            PlayStance(wanted, StanceFade);
        }
    }

    private void PlayStance(int index, float fade)
    {
        AnimationClip clip = index == 0 ? HighEnergy : index == 2 ? LowEnergy : MidEnergy;
        if (clip == null)
            return;

        if (fade <= 0f)
            _Base.Play(clip);
        else
            _Base.Play(clip, fade);

        _LiveStance = index;
    }

    /// <summary>
    /// The right stick sweeps a small circle in front of the chest. Down drops further than the
    /// radius, so the body shot is a clearly separate place rather than a low head shot.
    /// </summary>
    private void ReadAim()
    {
        Vector2 stick = AimFromStick && !LivePunchSteering && (_PunchHeld[0] || _PunchHeld[1]) && _GuardRequested == Defense.None
            ? _ControlStick : Vector2.zero;
        _Aim = Vector2.SmoothDamp(_Aim, stick, ref _AimVelocity, Mathf.Max(0.001f, AimSmoothing));

        float lift = _Aim.y >= 0f ? _Aim.y * AimRadius : _Aim.y * BodyDrop;

        // Centre of the circle: the target if there is one, otherwise straight ahead. Either
        // way a centred stick means dead ahead — never a leftover offset.
        Vector3 centre = AimCentre();

        // Raise the aim by the same angle the arm is pitched, so AimIK agrees with the pose
        // rather than pulling against it whenever its weight is turned up.
        float pitch = _Hand == 0 ? LeftJabHeight : _Hand == 1 ? RightJabHeight : 0f;
        float fromPitch = Mathf.Tan(pitch * Mathf.Deg2Rad) * AimDistance;

        _AimWorld = centre
                  + transform.right * (_Aim.x * AimRadius)
                  + Vector3.up * (lift + fromPitch);
    }

    private void UpdateDefense()
    {
        if (_GuardRequested != Defense.None) CancelHolds();
        bool canGuard = PunchPlayback.CanRaiseGuard(_PunchState != null && !_Preparing,
            _PunchState != null ? _PunchState.NormalizedTime : 0f, _Timing.ContactEnd);
        Defense wanted = canGuard ? _GuardRequested : Defense.None;
        if (_GuardRequested != Defense.None) _InputBuffer.Clear();
        if (wanted != Defense.None && _PunchState != null) EndPunch();
        AnimationClip clip = wanted == Defense.Head ? HeadDefense : wanted == Defense.Body ? BodyDefense : null;
        float blend = Mathf.Max(0.04f, DefenseBlend);
        if (wanted != _GuardPlaying || clip != _GuardClip)
        {
            _GuardPlaying = wanted;
            _GuardClip = clip;
            if (wanted != Defense.None && clip == null)
                Debug.LogWarning($"AimStudyBoxer: {wanted} defense clip is missing. Run Upgrade Selected Aim Study Boxer.", this);
            if (clip != null)
            {
                int slot = wanted == Defense.Head ? 1 : 0;
                float time = Mathf.Clamp01(wanted == Defense.Head ? HeadDefensePose : BodyDefensePose) * clip.length;
                for (int part = 0; part < _DefenseParts.Length; part++)
                {
                    AnimancerState state = _DefenseParts[part].Play(clip, blend);
                    state.Time = time;
                    state.Speed = 0f;
                    _DefenseStates[part, slot] = state;
                }
            }
        }
        for (int part = 0; part < _DefenseParts.Length; part++)
        {
            AnimancerLayer layer = _DefenseParts[part];
            if (layer == null) continue;
            float weight = clip != null ? (part == 0 ? DefenseTorsoWeight : 1f) : 0f;
            if (Mathf.Abs(layer.TargetWeight - weight) > 0.001f) layer.StartFade(weight, blend);
        }
        BodyGuardWeight = DefenseContribution(0);
        HeadGuardWeight = DefenseContribution(1);
    }

    private float DefenseContribution(int slot)
    {
        float total = 0f;
        for (int part = 1; part < 3; part++)
        {
            AnimancerState state = _DefenseStates[part, slot];
            if (state != null && _DefenseParts[part] != null)
                total += state.Weight * _DefenseParts[part].Weight;
        }
        return Mathf.Clamp01(total * 0.5f);
    }

    private void UpdateLean(float dt)
    {
        if (dt <= 0f) return;
        bool flying = !PunchPlayback.CanRaiseGuard(_PunchState != null && !_Preparing,
            _PunchState != null ? _PunchState.NormalizedTime : 0f, _Timing.ContactEnd);
        float authority = PunchPlayback.LeanAuthority(_PunchHeld[0] || _PunchHeld[1], _GuardRequested != Defense.None, flying);
        float magnitude = _ControlStick.magnitude;
        Vector2 wanted = EnableLean && magnitude > 0.0001f
            ? _ControlStick / magnitude * PunchPlayback.DeadzoneMagnitude(magnitude, LeanDeadzone) * authority
            : Vector2.zero;
        _Lean = Vector2.ClampMagnitude(Vector2.SmoothDamp(_Lean, wanted, ref _LeanVelocity,
            Mathf.Max(0.04f, LeanResponse * (flying ? 0.75f : 1f)), 12f, dt), 1f);
        _ContactRecoil = Vector2.SmoothDamp(_ContactRecoil, Vector2.zero, ref _ContactRecoilVelocity, 0.12f, 90f, dt);
    }

    private void ApplyLean()
    {
        if (_LeanChain.Count == 0) return;
        float pitch = _Lean.y * (_Lean.y < 0f ? BackLeanDegrees : ForwardLeanDegrees) + _ContactRecoil.y;
        float side = _Lean.x * SideLeanDegrees + _ContactRecoil.x;
        Transform head = _Animancer.Animator.GetBoneTransform(HumanBodyBones.Head);
        Quaternion headRotation = head != null ? head.rotation : Quaternion.identity;
        float share = 1f / _LeanChain.Count;
        Quaternion rotation = Quaternion.AngleAxis(pitch * share, transform.right)
            * Quaternion.AngleAxis(-side * share, transform.forward);
        foreach (Transform joint in _LeanChain) joint.rotation = rotation * joint.rotation;
        if (head != null) head.rotation = Quaternion.Slerp(head.rotation, headRotation, LeanHeadStability);
    }

    private void UpdatePunchSteering(float dt)
    {
        if (!LivePunchSteering || dt <= 0f || _PunchState == null || _Hand < 0
            || !PunchPlayback.CanSteer(ReleaseToPunch, _Preparing, _PunchHeld[_Hand])
            || _GuardRequested != Defense.None || _ContactLanded) return;
        float authority = _Preparing ? 1f : PunchPlayback.SteeringAuthority(_PunchState.NormalizedTime, _Timing.Strike);
        if (authority <= 0f) return;
        Vector2 wanted = Vector2.ClampMagnitude(new Vector2(_ControlStick.x, _ControlStick.y - _SteerOrigin.y), 1f);
        float follow = 1f - Mathf.Exp(-dt * authority / Mathf.Max(0.02f, PunchSteerResponse));
        _SteerOffset = Vector2.Lerp(_SteerOffset, wanted, follow);
        Vector3 offset = new Vector3(_SteerOffset.x, _SteerOffset.y, 0f) * PunchSteerDistance;
        foreach (AnimancerState state in _PartStates)
        {
            if (state == null || !_Samples.TryGetValue(state, out MotionSample sample)) continue;
            sample.LocalAim = _ShotAimBase + offset;
            _Samples[state] = sample;
        }
    }

    private Vector3 AimCentre(Boxing.PunchTarget target = Boxing.PunchTarget.Head)
    {
        if (Opponent != null)
        {
            if (_OpponentAnimator == null) _OpponentAnimator = Opponent.GetComponent<Animator>();
            Transform bone = _OpponentAnimator != null
                ? _OpponentAnimator.GetBoneTransform(target == Boxing.PunchTarget.Head
                    ? HumanBodyBones.Head : HumanBodyBones.Chest) : null;
            if (bone == null && _OpponentAnimator != null && target == Boxing.PunchTarget.Body)
                bone = _OpponentAnimator.GetBoneTransform(HumanBodyBones.Spine);
            if (bone != null) return bone.position;
            return Opponent.transform.position + Vector3.up * HeadHeight;
        }
        if (Target == null) return transform.position + transform.forward * AimDistance + Vector3.up * HeadHeight;
        PunchingBag bag = Target.GetComponentInParent<PunchingBag>();
        if (bag == null || bag.BagCollider == null) return Target.position;
        Bounds bounds = bag.BagCollider.bounds;
        return new Vector3(bounds.center.x,
            Mathf.Clamp(transform.position.y + HeadHeight, bounds.min.y + 0.05f, bounds.max.y - 0.05f), bounds.center.z);
    }

    private void HookContacts()
    {
        if (Hitboxes == null) return;
        for (int hand = 0; hand < 2; hand++)
        {
            PunchHitbox sensor = hand == 0 ? Hitboxes.LeftHand : Hitboxes.RightHand;
            if (sensor == _Sensors[hand]) continue;
            if (_Sensors[hand] != null)
            {
                _Sensors[hand].OnLanded.RemoveListener(OnContact);
                _Sensors[hand].OnLandedOnBoxer -= OnBoxerContact;
            }
            _Sensors[hand] = sensor;
            if (sensor == null) continue;
            sensor.Armed = false;
            sensor.OnLanded.AddListener(OnContact);
            sensor.OnLandedOnBoxer += OnBoxerContact;
        }
    }

    private void UpdateContacts()
    {
        float time = !_Preparing && _PunchState != null ? _PunchState.NormalizedTime : -1f;
        for (int hand = 0; hand < 2; hand++)
        {
            PunchHitbox sensor = _Sensors[hand];
            if (sensor == null) continue;
            bool armed = hand == _Hand && !_ContactLanded && time >= _Timing.ContactStart && time <= _Timing.ContactEnd;
            sensor.Armed = armed;
            sensor.PowerScale = armed ? _PunchPower : 1f;
        }
        if (!_ImpactRecorded && time >= _Timing.Strike && _PunchState != null)
        {
            _ImpactRecorded = true;
            _LastImpact = Time.time;
        }
    }

    private void OnContact(PunchHitbox sensor, PunchingBag bag, PunchingBag.HitInfo hit)
    {
        if (!isActiveAndEnabled || _Preparing || sensor.Hand != _Hand || _ContactLanded || _PunchState == null) return;
        _ContactLanded = true;
        sensor.Armed = false;
        float strength = Mathf.Clamp01(hit.strength);
        Vector3 direction = transform.InverseTransformDirection(hit.direction);
        _ContactRecoil = Vector2.ClampMagnitude(_ContactRecoil + new Vector2(-direction.x,
            -Mathf.Max(0.3f, Mathf.Abs(direction.z))) * (ContactRecoilDegrees * strength), 6f);
        if (IsPlayerControlled && FeedbackCamera != null)
        {
            FeedbackCamera.Shake(ContactShake * strength);
            FeedbackCamera.Kick(hit.direction * (0.025f * strength));
        }
        HitLanded?.Invoke(sensor.Hand, hit);
    }

    private void OnBoxerContact(BoxerHealth target, BoxerHealth.Zone zone, float strength)
    {
        if (!isActiveAndEnabled || _Preparing || _Hand < 0 || _ContactLanded || _PunchState == null) return;
        _ContactLanded = true;
        for (int i = 0; i < 2; i++)
            if (_Sensors[i] != null) _Sensors[i].Armed = false;
        Vector3 direction = transform.InverseTransformDirection(
            (target.transform.position - transform.position).normalized);
        _ContactRecoil = Vector2.ClampMagnitude(_ContactRecoil + new Vector2(-direction.x,
            -Mathf.Max(0.3f, Mathf.Abs(direction.z))) * (ContactRecoilDegrees * Mathf.Clamp01(strength)), 6f);
        if (IsPlayerControlled && FeedbackCamera != null)
            FeedbackCamera.Shake(ContactShake * Mathf.Clamp01(strength));
    }

    private void ReadPunch()
    {
        // Interrupting is fine — the crossfade handles it, and waiting for the clip to run out
        // is what made a one-two feel like two separate events instead of a combination.
        if (Time.deltaTime <= 0f || !Application.isFocused || CombatLocked || _GuardRequested != Defense.None) return;
        if (!ReleaseToPunch) CancelHolds();
        for (int hand = 0; hand < 2; hand++)
        {
            bool started = Trigger(hand) && !_Holds[hand].Active;
            if (started)
            {
                ReadTechnique(out Boxing.PunchType type, out Boxing.PunchTarget target);
                if (!ReleaseToPunch) QueuePunch(hand, type, target, _Aim);
                else
                {
                    ShotRequest held = NewRequest(hand, type, target, _Aim);
                    if (ResolveShot(ref held))
                    {
                        _HeldRequests[hand] = held;
                        _Holds[hand].Begin();
                    }
                }
            }
            if (!ReleaseToPunch || !_Holds[hand].Active) continue;
            if (!started)
            {
                float cost = _Holds[hand].Advance(Time.deltaTime, TapGrace, HoldStaminaPerSecond);
                SpendStamina(cost);
            }
            if (!_PunchHeld[hand])
            {
                ShotRequest released = _HeldRequests[hand];
                released.Charge = _Holds[hand].Release(TapGrace, FullChargeTime);
                _InputBuffer.Enqueue(released, Time.time, Mathf.Max(0.05f, InputBufferSeconds));
            }
        }

        if (_InputBuffer.TryPeek(Time.time, out ShotRequest request) && CanStartShot(request.Hand))
        {
            _InputBuffer.Dequeue();
            Throw(request);
        }
        if (_Preparing && (_Hand < 0 || !_Holds[_Hand].Active)) EndPunch();
        if (ReleaseToPunch && !_Preparing)
        {
            int next = -1;
            for (int hand = 0; hand < 2; hand++)
                if (_Holds[hand].Active && CanStartShot(hand) &&
                    (next < 0 || _HeldRequests[hand].Sequence < _HeldRequests[next].Sequence)) next = hand;
            if (next >= 0) PlayShot(_HeldRequests[next], true);
        }
        if (_Preparing && _PunchState != null)
        {
            _PreparationElapsed += Time.deltaTime * Fatigue();
            _PunchState.NormalizedTime = PunchPlayback.PreparationTime(_PreparationElapsed, PreparationSeconds, _Timing);
        }
    }

    private bool CanStartShot(int hand)
    {
        return _PunchState == null || _Preparing || !_PunchState.IsPlaying ||
            _PunchState.NormalizedTime >= PunchPlayback.ComboTime(_Timing, ReplayAfter, hand != _Hand);
    }

    private ShotRequest NewRequest(int hand, Boxing.PunchType type, Boxing.PunchTarget target, Vector2 aim)
    {
        return new ShotRequest
        {
            Hand = hand, Type = type, Target = target, Aim = Vector2.ClampMagnitude(aim, 1f),
            Control = _ControlStick, Sequence = ++_RequestSequence,
        };
    }

    private void SpendStamina(float amount)
    {
        if (!UseStamina || amount <= 0f) return;
        _Stamina = Mathf.Max(0f, Stamina - amount);
        _LastStaminaSpend = Time.time;
    }

    private void CancelHolds()
    {
        foreach (PunchHold hold in _Holds) hold.Cancel();
        if (_Preparing) EndPunch();
    }

    private bool ResolveShot(ref ShotRequest request)
    {
        if (request.Clip != null) return true;
        if (!CanThrow(request.Hand, out string why))
        {
            if (Verbose) Debug.LogWarning($"AimStudyBoxer: preparation ignored — {why}", this);
            return false;
        }
        request.Motion = SelectMotion(request);
        bool legacy = request.Motion == null && request.Type == Boxing.PunchType.Jab && request.Target == Boxing.PunchTarget.Head;
        request.Clip = request.Motion != null ? request.Motion.Clip : legacy ? JabClip(request.Hand) : null;
        if (request.Clip != null) return true;
        if (Verbose) Debug.LogWarning($"AimStudyBoxer: no {request.Type} {request.Target} for hand {request.Hand}. Run Upgrade Selected Aim Study Boxer.", this);
        return false;
    }

    public bool QueuePunch(int hand, Boxing.PunchType type, Boxing.PunchTarget target, Vector2 aim)
    {
        if (!isActiveAndEnabled || hand < 0 || hand > 1 || CombatLocked || _GuardRequested != Defense.None) return false;
        return _InputBuffer.Enqueue(NewRequest(hand, type, target, aim), Time.time, Mathf.Max(0.05f, InputBufferSeconds));
    }

    private void ReadTechnique(out Boxing.PunchType type, out Boxing.PunchTarget target)
    {
        type = DirectionalPunches ? (Boxing.PunchType)PunchPlayback.StickTechnique(_ControlStick.x, _ControlStick.y, TechniqueThreshold)
            : Boxing.PunchType.Jab;
        if (_Input != null)
        {
            target = InputBodyShot ? Boxing.PunchTarget.Body : Boxing.PunchTarget.Head;
            return;
        }
        target = Boxing.PunchTarget.Head;
#if ENABLE_INPUT_SYSTEM
        Gamepad pad = Gamepad.current;
        if (pad != null && pad.buttonSouth.isPressed) target = Boxing.PunchTarget.Body;
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) type = Boxing.PunchType.Hook;
            if (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) type = Boxing.PunchType.Uppercut;
            if (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed) target = Boxing.PunchTarget.Body;
        }
#else
        if (Input.GetKey(KeyCode.LeftShift)) type = Boxing.PunchType.Hook;
        if (Input.GetKey(KeyCode.LeftControl)) type = Boxing.PunchType.Uppercut;
        if (Input.GetKey(KeyCode.LeftAlt)) target = Boxing.PunchTarget.Body;
#endif
    }

    private PunchMotion SelectMotion(ShotRequest request)
    {
        if (Punches == null || (request.Type == Boxing.PunchType.Jab && request.Target == Boxing.PunchTarget.Head)) return null;
        PunchMotion selected = null;
        int seen = 0;
        for (int pass = 0; pass < 2 && selected == null; pass++)
        {
            foreach (PunchMotion motion in Punches)
            {
                if (motion == null || motion.Clip == null || (int)motion.Hand != request.Hand ||
                    motion.Type != request.Type || motion.Target != request.Target) continue;
                if (pass == 0 && motion.Clip == _LastClips[request.Hand]) continue;
                if (Random.Range(0, ++seen) == 0) selected = motion;
            }
        }
        return selected;
    }

    public static PunchPlayback.Timing ReadTiming(AnimationClip clip, float fallbackStrike)
    {
        float strike = fallbackStrike, start = -1f, end = -1f, complete = -1f;
        bool foundImpact = false;
        if (clip != null && clip.length > 0f)
        {
            foreach (AnimationEvent marker in clip.events)
            {
                float t = marker.time / clip.length;
                if (marker.functionName == "ExpectedImpact" && (!foundImpact || t < strike))
                {
                    strike = t;
                    foundImpact = true;
                }
                else if (marker.functionName == "Left_Arm_Attack_On" || marker.functionName == "Right_Arm_Attack_On")
                    start = start < 0f ? t : Mathf.Min(start, t);
                else if (marker.functionName == "Left_Arm_Attack_Off" || marker.functionName == "Right_Arm_Attack_Off")
                    end = Mathf.Max(end, t);
                else if (marker.functionName == "PunchComplete") complete = Mathf.Max(complete, t);
            }
        }
        return PunchPlayback.CreateTiming(strike, start, end, complete);
    }

    /// <summary>Says out loud why a press produced nothing, instead of failing silently.</summary>
    private bool CanThrow(int hand, out string why)
    {
        if (_Parts[0] == null)
        {
            why = "Start() never ran — the layers were never built.";
            return false;
        }

        if (JabClip(hand) == null &&
            (Punches == null || !System.Array.Exists(Punches, p => p != null && p.Clip != null && (int)p.Hand == hand)))
        {
            why = $"the {(hand == 0 ? "LeftJab" : "RightJab")} slot is empty.";
            return false;
        }

        why = null;
        return true;
    }

    public bool HasMotion(int hand, Boxing.PunchType type, Boxing.PunchTarget target)
    {
        if (hand < 0 || hand > 1) return false;
        if (type == Boxing.PunchType.Jab && target == Boxing.PunchTarget.Head) return JabClip(hand) != null;
        return Punches != null && System.Array.Exists(Punches,
            p => p != null && p.Clip != null && (int)p.Hand == hand && p.Type == type && p.Target == target);
    }

    private void Throw(ShotRequest request) => PlayShot(request, false);

    private void PlayShot(ShotRequest request, bool preparing)
    {
        if (!ResolveShot(ref request)) return;
        int hand = request.Hand;
        PunchMotion motion = request.Motion;
        AnimationClip clip = request.Clip;
        bool legacy = motion == null;
        bool resume = !preparing && _Preparing && _PreparationSequence == request.Sequence && _PunchState != null;
        float preparedTime = resume ? _PunchState.NormalizedTime : 0f;
        float preparedElapsed = resume ? _PreparationElapsed : 0f;
        if (!resume) _Timing = ReadTiming(clip, motion != null ? motion.Strike : hand == 0 ? LeftStrike : RightStrike);
        _ActiveImpactTime = PunchPlayback.ReleaseImpactTime(
            UsePerMotionTiming && motion != null ? motion.ImpactSeconds : ImpactTime,
            preparedElapsed, PreparationSeconds);
        if (!preparing) _Combo = Time.time - _LastPunch <= ComboWindow ? Mathf.Min(3, _Combo + 1) : 0;
        _Preparing = preparing;
        _PreparationSequence = preparing ? request.Sequence : -1;
        if (preparing) _PreparationElapsed = 0f;
        _Hand = hand;
        _ShotType = request.Type;
        AttackSequence = request.Sequence;
        AttackTarget = request.Target;
        _ContactLanded = _ImpactRecorded = false;
        if (!preparing)
        {
            float cost = request.Type == Boxing.PunchType.Jab ? 1f : request.Type == Boxing.PunchType.Hook ? 1.15f : 1.25f;
            SpendStamina(StaminaPerPunch * cost);
            float energy = UseStamina ? Stamina01 : 1f;
            _ReleasedCharge = Mathf.Clamp01(request.Charge);
            _PunchPower = Mathf.Lerp(0.65f, 1f, energy) * (_ShotType == Boxing.PunchType.Jab ? 1f : 1.15f)
                * PunchPlayback.ChargePower(_ReleasedCharge, energy, ChargePowerBonus);
            _LastPunch = Time.time;
            _LastClips[hand] = clip;
        }
        float speed = preparing ? 0f : SpeedFor(hand, clip, preparedTime);
        for (int i = 0; i < 2; i++)
            if (_Sensors[i] != null) _Sensors[i].Armed = false;

        MotionSample sample = default;
        if (!resume)
        {
            float aimScale = request.Type == Boxing.PunchType.Hook ? HookAimScale
                : request.Type == Boxing.PunchType.Uppercut ? UppercutAimScale : 1f;
            float height = motion != null ? motion.Height : hand == 0 ? LeftJabHeight : RightJabHeight;
            float lift = request.Target == Boxing.PunchTarget.Body ? (Opponent != null ? 0f : -BodyDrop) + request.Aim.y * AimRadius * 0.25f
                : request.Aim.y >= 0f ? request.Aim.y * AimRadius : request.Aim.y * BodyDrop;
            Vector3 shotAim = AimCentre(request.Target) + transform.right * (request.Aim.x * AimRadius)
                + Vector3.up * (lift + Mathf.Tan(height * Mathf.Deg2Rad) * AimDistance);
            sample = new MotionSample
            {
                Hand = hand, Legacy = legacy, Timing = _Timing, AimScale = aimScale,
                Orientation = motion != null ? motion.Orientation : hand == 0 ? LeftOrientationFix : RightOrientationFix,
                Height = height, LocalAim = transform.InverseTransformPoint(shotAim),
            };
            _ShotAimBase = sample.LocalAim;
            _SteerOrigin = request.Control;
            _SteerOffset = Vector2.zero;
        }

        // Fade the LAYERS, never assign their weight. Play() fades the state inside a layer, but
        // the layer itself would jump 0 -> 1 in one frame, taking the whole punch with it.
        for (int i = 0; i < _Parts.Length; i++)
        {
            if (_Parts[i] == null)
                continue;

            float w = FollowOf(i);
            float fade = preparing ? Mathf.Max(0.04f, PunchFade)
                : Mathf.Min(Mathf.Max(0.015f, PunchFade), clip.length * (_Timing.Strike - preparedTime) / speed * 0.45f);
            if (!resume)
            {
                _PartStates[i] = _Parts[i].Play(clip, Mathf.Max(0.01f, fade), FadeMode.FromStart);
                _Samples[_PartStates[i]] = sample;
            }
            _PartStates[i].Speed = speed;
            _Parts[i].StartFade(w, fade);
        }

        _PunchState = _PartStates[hand + 1];
        _PunchDeadline = Time.time + (clip.length - _PunchState.Time) / Mathf.Max(0.02f, speed) + 0.5f;
        _Last = $"{(hand == 0 ? "LEFT" : "RIGHT")} {request.Type} {request.Target}   " +
            (preparing ? "preparing" : $"charge {_ReleasedCharge:0%} / combo {_Combo + 1}");
        if (!preparing) PunchStarted?.Invoke(hand, request.Type);

        if (Verbose && !preparing)
        {
            Debug.Log($"AimStudyBoxer: threw {(hand == 0 ? "LEFT" : "RIGHT")} '{clip.name}' " +
                $"speed {speed:0.00}, states " +
                $"[{(_PartStates[0] != null ? "torso" : "-")}, " +
                $"{(_PartStates[1] != null ? "L" : "-")}, " +
                $"{(_PartStates[2] != null ? "R" : "-")}]", this);
        }
    }

    /// <summary>
    /// Playback speed for this hand.
    /// </summary>
    /// <remarks>
    /// Time to the strike is clipLength x strike / speed, so solving that for a shared target
    /// gives each hand its own multiplier and both gloves land together. Mirroring one clip from
    /// the other is the better fix — then they match by construction and this is not needed —
    /// but this makes two unmatched captures usable as a pair.
    /// </remarks>
    private float SpeedFor(int hand, AnimationClip clip, float preparedTime)
    {
        float combo = PunchPlayback.ComboSpeed(ComboAcceleration, _Combo, UseStamina ? Stamina01 : 1f);
        if (!MatchImpactTiming || clip == null)
            return Mathf.Max(0.02f, PunchSpeed * Fatigue() * combo);

        float strike = _Timing.Strike;

        // Fatigue multiplies the whole thing, so a gassed punch is slower even when both hands
        // are matched to the same impact time.
        return PunchPlayback.ReleaseSpeed(clip.length, strike, preparedTime, _ActiveImpactTime, PunchSpeed * combo, Fatigue());
    }

    /// <summary>
    /// Starts the punch layer fading out. The hand is deliberately NOT cleared here.
    /// </summary>
    /// <remarks>
    /// The orientation fix and the aim both scale off the active hand, and the layer takes
    /// RecoverFade to go. Clearing the hand now dropped a 90 degree correction in one frame
    /// while the punch pose was still fully visible. Update releases it once the layer has
    /// actually gone.
    /// </remarks>
    private void EndPunch()
    {
        _Preparing = false;
        _PreparationSequence = -1;
        _PunchState = null;

        for (int i = 0; i < _Parts.Length; i++)
        {
            _PartStates[i] = null;
            if (_Parts[i] == null || _Animancer == null || !_Animancer.IsGraphInitialized) continue;
            foreach (AnimancerState state in _Parts[i]) state.IsPlaying = false;
            _Parts[i].StartFade(0f, Mathf.Max(0.01f, RecoverFade));
        }
        for (int i = 0; i < 2; i++)
            if (_Sensors[i] != null) _Sensors[i].Armed = false;
    }

    /// <summary>Layer weight for a region: the punching arm is full, the rest take their share.</summary>
    /// <summary>
    /// Puts the whole body back on the punch -- the single full-weight punch layer this rig
    /// had before the follow sliders existed. Start here, then dial down.
    /// </summary>
    [ContextMenu("Full Body Punch (original look)")]
    public void FullBodyPunch()
    {
        BodyFollow = 1f;
        OffHandFollow = 1f;
    }

    private float FollowOf(int part)
    {
        if (part == 0)
            return BodyFollow;                       // torso

        bool isPunchingArm = (part == 1 && _Hand == 0) || (part == 2 && _Hand == 1);
        return isPunchingArm ? 1f : OffHandFollow;
    }

    /// <summary>
    /// Keeps the region layers on the same frame and at their slider weights.
    /// </summary>
    /// <remarks>
    /// They all play the same clip at the same speed so they should not drift, but they are
    /// separate states and a dropped frame would show as one region lagging the rest. Driving
    /// them off the primary each frame costs nothing and removes the possibility.
    /// </remarks>
    /// <summary>Rises while a punch is live, falls once it is over.</summary>
    private void AdvancePunchWeight(float dt)
    {
        _PunchWeight = _Parts[1] != null && _Parts[2] != null
            ? Mathf.Clamp01(Mathf.Max(_Parts[1].Weight, _Parts[2].Weight)) : 0f;
    }

    private void SyncParts()
    {
        if (_PunchState == null)
            return;

        float t = _PunchState.Time;

        for (int i = 0; i < _Parts.Length; i++)
        {
            if (_Parts[i] == null)
                continue;

            // Live slider changes take effect on the next punch for the fade target, but the
            // weight itself follows immediately so tuning mid-flurry reads straight away.
            if (_PartStates[i] != null)
            {
                if (_PartStates[i] != _PunchState) _PartStates[i].Time = t;
                if (Mathf.Abs(_Parts[i].TargetWeight - FollowOf(i)) > 0.001f)
                    _Parts[i].StartFade(FollowOf(i), Mathf.Max(0.01f, PunchFade));
            }
        }
    }

    private void Move()
    {
        if (CombatLocked)
        {
            _Velocity = _ImpactVelocity = Vector3.zero;
            return;
        }

        Vector2 move = ReadMove();

        float guardSpeed = Mathf.Lerp(1f, DefenseMoveScale, Mathf.Clamp01(HeadGuardWeight + BodyGuardWeight));
        Vector3 wanted = (transform.right * move.x + transform.forward * move.y) * (MoveSpeed * guardSpeed * MovementMultiplier);
        _Velocity = Vector3.MoveTowards(_Velocity, wanted, Acceleration * Time.deltaTime);
        _ImpactVelocity = Vector3.MoveTowards(_ImpactVelocity, Vector3.zero, 6f * Time.deltaTime);
        Vector3 displacement = (_Velocity + _ImpactVelocity) * Time.deltaTime;
        if (_Mover != null && _Mover.enabled)
            _Mover.Move(displacement + Vector3.down * 2f * Time.deltaTime);
        else
            transform.position += displacement;

        bool committed = !PunchPlayback.CanRaiseGuard(_PunchState != null && !_Preparing,
            _PunchState != null ? _PunchState.NormalizedTime : 0f, _Timing.ContactEnd);
        if (Opponent != null)
        {
            if (!committed)
            {
                Vector3 towards = Vector3.ProjectOnPlane(Opponent.transform.position - transform.position, Vector3.up);
                if (towards.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.RotateTowards(transform.rotation,
                        Quaternion.LookRotation(towards.normalized, Vector3.up), TurnSpeed * Time.deltaTime);
            }
        }
        else
        {
            float turn = ReadTurn();
            if (turn != 0f)
                transform.Rotate(0f, turn * TurnSpeed * Time.deltaTime, 0f);
        }
    }

    /// <summary>
    /// Ramps the aim on a window centred on this clip's own strike frame.
    /// </summary>
    /// <remarks>
    /// Advanced in Update only. Writing the solver happens in LateUpdate, after Animancer has
    /// posed the body — AimIK has to correct the finished pose, not the one from last frame.
    /// </remarks>
    private void Advance(float dt)
    {
        for (int hand = 0; hand < 2; hand++)
        {
            float wanted = 0f;
            Vector3 aim = Vector3.zero;
            AnimancerLayer layer = _Parts[hand + 1];
            if (layer != null)
            {
                foreach (AnimancerState state in layer)
                {
                    if (state.Weight <= 0f || !_Samples.TryGetValue(state, out MotionSample sample) || sample.Hand != hand) continue;
                    float hold = HoldAfterStrike * Mathf.Abs(state.Speed) / Mathf.Max(0.01f, state.Length);
                    float envelope = PunchPlayback.AimEnvelope(state.NormalizedTime, sample.Timing.Strike, AimWindow, hold);
                    float defense = _DefenseParts[hand + 1] != null ? _DefenseParts[hand + 1].Weight : 0f;
                    float weight = envelope * state.Weight * layer.Weight * sample.AimScale * (1f - defense);
                    wanted += weight;
                    aim += transform.TransformPoint(sample.LocalAim) * weight;
                }
            }
            if (wanted > 0.001f) _HandAim[hand] = aim / wanted;
            else if (_AimWeights[hand] <= 0.001f) _HandAim[hand] = _AimWorld;
            _AimWeights[hand] = Mathf.MoveTowards(_AimWeights[hand], Mathf.Clamp01(wanted), dt * 10f);
        }
        _Weight = Mathf.Max(_AimWeights[0], _AimWeights[1]);
    }

    private void LateUpdate()
    {
        if (CombatLocked)
        {
            Disarm(LeftAim);
            Disarm(RightAim);
            return;
        }
        ApplyOrientationFix();
        ApplyJabHeight();
        ApplyLean();

        Write(LeftAim, _AimWeights[0], _HandAim[0]);
        Write(RightAim, _AimWeights[1], _HandAim[1]);
    }

    /// <summary>
    /// Pitches the punching arm up or down so the jab lands higher or lower.
    /// </summary>
    /// <remarks>
    /// Rotating the arm rather than moving the aim point, because with AimIK at zero weight the
    /// aim point is only a marker — nothing would follow it. The rotation is around the CHEST's
    /// right axis, so the pitch stays true to the body however the torso is turned, and it is
    /// scaled by the punch layer's weight so the guard is never touched.
    /// </remarks>
    private void ApplyJabHeight()
    {
        for (int hand = 0; hand < 2; hand++)
        {
            float wanted = 0f;
            AnimancerLayer layer = _Parts[hand + 1];
            if (layer == null) continue;
            foreach (AnimancerState state in layer)
            {
                if (!_Samples.TryGetValue(state, out MotionSample sample) || sample.Hand != hand) continue;
                float height = sample.Legacy ? (hand == 0 ? LeftJabHeight : RightJabHeight) : sample.Height;
                float defense = _DefenseParts[hand + 1] != null ? _DefenseParts[hand + 1].Weight : 0f;
                wanted += height * state.Weight * layer.Weight * (1f - defense);
            }
            _HeightBlend[hand] = Mathf.Lerp(_HeightBlend[hand], wanted,
                1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, CorrectionSmoothing)));
            ApplyJabHeight(hand, _HeightBlend[hand]);
        }
    }

    private void ApplyJabHeight(int hand, float degrees)
    {
        Animator animator = _Animancer != null ? _Animancer.Animator : null;
        if (animator == null || Mathf.Abs(degrees) < 0.01f) return;

        bool left = hand == 0;
        Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest)
                       ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        if (chest == null)
            return;

        // Positive should raise the fist for either hand, so the axis flips with the side.
        Vector3 axis = chest.right * (left ? 1f : -1f);

        Transform shoulder = animator.GetBoneTransform(left ? HumanBodyBones.LeftShoulder
                                                            : HumanBodyBones.RightShoulder);
        if (shoulder != null && ShoulderShare > 0f)
        {
            shoulder.rotation = Quaternion.AngleAxis(degrees * ShoulderShare, axis)
                              * shoulder.rotation;
        }

        Transform upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm
                                                         : HumanBodyBones.RightUpperArm);
        if (upper == null)
            return;

        float rest = shoulder != null ? 1f - ShoulderShare : 1f;
        upper.rotation = Quaternion.AngleAxis(degrees * rest, axis) * upper.rotation;

        _HeightApplied = degrees;
    }

    /// <summary>
    /// Takes the torso correction back out at the neck and head.
    /// </summary>
    /// <remarks>
    /// The head hangs off the end of the spine, so every degree put into the torso arrives at
    /// the head as well — a 90 degree fix swings his gaze right off the opponent. Rotating back
    /// by the same amount leaves the twist where it belongs, in the body, and the head keeps
    /// looking forward. Masking the head out of the punch layer is not enough on its own:
    /// that stops the CLIP driving the head, not the parent bones carrying it.
    /// </remarks>
    private void SteadyHead(Animator animator, float degrees)
    {
        if (!StabiliseHead || Mathf.Abs(degrees) < 0.01f)
            return;

        Transform neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        if (neck != null && NeckStability > 0f)
        {
            neck.rotation = Quaternion.AngleAxis(-degrees * NeckStability, transform.up)
                          * neck.rotation;
        }

        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null)
            return;

        // Whatever the neck already took back is not owed again.
        float remaining = HeadStability - (neck != null ? NeckStability : 0f);
        if (Mathf.Abs(remaining) < 0.01f)
            return;

        head.rotation = Quaternion.AngleAxis(-degrees * remaining, transform.up) * head.rotation;
    }

    /// <summary>
    /// Cancels the heading difference between the punch clip and the idle, live.
    /// </summary>
    /// <remarks>
    /// Scaled by the punch layer's weight, so it fades in and out exactly as the clip does and
    /// the idle is never touched. Applied after Animancer has posed the body and before the IK
    /// solves; the Animator rewrites the pose every frame, so this is a fresh correction each
    /// time rather than an accumulating one.
    /// </remarks>
    private void ApplyOrientationFix()
    {
        float wanted = 0f;
        if (CorrectOrientation && _Parts[0] != null)
        {
            foreach (AnimancerState state in _Parts[0])
            {
                if (!_Samples.TryGetValue(state, out MotionSample sample)) continue;
                float yaw = sample.Legacy ? (sample.Hand == 0 ? LeftOrientationFix : RightOrientationFix) : sample.Orientation;
                float defense = _DefenseParts[0] != null ? _DefenseParts[0].Weight : 0f;
                wanted += yaw * state.Weight * _PunchWeight * (1f - defense);
            }
        }
        _FixApplied = Mathf.SmoothDamp(_FixApplied, wanted, ref _CorrectionVelocity,
            Mathf.Max(0.01f, CorrectionSmoothing), 1440f, Time.deltaTime);
        float degrees = _FixApplied;
        Animator animator = _Animancer != null ? _Animancer.Animator : null;
        if (animator == null || Mathf.Abs(degrees) < 0.01f) return;

        if (!SpreadAcrossSpine)
        {
            Transform single = animator.GetBoneTransform(CorrectionBone);
            if (single != null)
                single.rotation = Quaternion.AngleAxis(degrees, transform.up) * single.rotation;

            SteadyHead(animator, degrees);
            _FixApplied = degrees;
            return;
        }

        // Parent first, and each bone carries its children — so a third each compounds down the
        // chain to the full angle at the hands, while no single joint bends more than a third
        // of it and the legs, which hang off the Hips, are never touched.
        _SpineChain.Clear();
        foreach (HumanBodyBones b in SpineBones)
        {
            Transform t = animator.GetBoneTransform(b);
            if (t != null)
                _SpineChain.Add(t);
        }

        if (_SpineChain.Count == 0)
            return;

        float each = degrees / _SpineChain.Count;
        Quaternion step = Quaternion.AngleAxis(each, transform.up);

        for (int i = 0; i < _SpineChain.Count; i++)
            _SpineChain[i].rotation = step * _SpineChain[i].rotation;

        SteadyHead(animator, degrees);

        _FixApplied = degrees;
    }

    private void Write(AimIK ik, float weight, Vector3 aim)
    {
        if (ik == null || ik.solver == null)
            return;

        ik.solver.IKPosition = aim;
        ik.solver.IKPositionWeight = weight * AimWeight;

        // Only written when asked. Writing them unconditionally every frame is what made the
        // AimIK components impossible to edit during play — anything typed there was gone the
        // next frame. Untick DriveSolverSettings to hand those three fields back.
        if (DriveSolverSettings)
        {
            ik.solver.maxIterations = Iterations;
            ik.solver.tolerance = Tolerance;
            ik.solver.clampWeight = ClampWeight;
        }

        if (weight > 0.01f && ik.solver.transform != null)
            _ReachError = Vector3.Distance(ik.solver.transform.position, _AimWorld);
    }

    /************************************************************************************/
    // Input — gamepad first, keyboard/mouse as the fallback.
    /************************************************************************************/

    private Vector2 ReadMove()
    {
        if (_Input != null) return Vector2.ClampMagnitude(_Input.Move, 1f);
#if ENABLE_INPUT_SYSTEM
        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            Vector2 v = pad.leftStick.ReadValue();
            if (v.sqrMagnitude > 0.02f)
                return Vector2.ClampMagnitude(v, 1f);
        }

        Keyboard kb = Keyboard.current;
        Vector2 m = Vector2.zero;
        if (kb != null)
        {
            if (kb.aKey.isPressed) m.x -= 1f;
            if (kb.dKey.isPressed) m.x += 1f;
            if (kb.sKey.isPressed) m.y -= 1f;
            if (kb.wKey.isPressed) m.y += 1f;
        }
        return Vector2.ClampMagnitude(m, 1f);
#else
        return Vector2.ClampMagnitude(
            new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f);
#endif
    }

    private Vector2 ReadLook()
    {
        if (_Input != null) return Vector2.ClampMagnitude(_Input.Body, 1f);
#if ENABLE_INPUT_SYSTEM
        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            Vector2 v = pad.rightStick.ReadValue();
            if (v.sqrMagnitude > 0.02f)
                return Vector2.ClampMagnitude(v, 1f);
        }

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            Vector2 arrows = new Vector2((kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.leftArrowKey.isPressed ? 1f : 0f),
                (kb.upArrowKey.isPressed ? 1f : 0f) - (kb.downArrowKey.isPressed ? 1f : 0f));
            if (arrows.sqrMagnitude > 0f) return Vector2.ClampMagnitude(arrows, 1f);
        }
        if (UseMouseAim)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 p = mouse.position.ReadValue();
                var mid = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                var half = new Vector2(Screen.width * 0.35f, Screen.height * 0.35f);
                return Vector2.ClampMagnitude(new Vector2((p.x - mid.x) / half.x,
                                                          (p.y - mid.y) / half.y), 1f);
            }
        }

#endif
        return Vector2.zero;
    }

    private float ReadTurn()
    {
        if (!IsPlayerControlled) return 0f;
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.qKey.isPressed) return -1f;
            if (kb.eKey.isPressed) return 1f;
        }
#endif
        return 0f;
    }

    private Defense ReadGuard()
    {
        if (_Input != null)
            return _Input.Block ? Defense.Head : _Input.BodyGuard ? Defense.Body : Defense.None;
#if ENABLE_INPUT_SYSTEM
        Gamepad pad = Gamepad.current;
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        bool body = (pad != null && pad.leftShoulder.isPressed) || (kb != null && kb.cKey.isPressed);
        bool head = (pad != null && pad.rightShoulder.isPressed) || (kb != null && kb.spaceKey.isPressed)
            || (mouse != null && mouse.middleButton.isPressed);
#else
        bool body = Input.GetKey(KeyCode.C);
        bool head = Input.GetKey(KeyCode.Space) || Input.GetMouseButton(2);
#endif
        return (Defense)PunchPlayback.GuardFromShoulders(body, head);
    }

    private bool TriggerHeld(int hand)
    {
        if (_Input != null) return _Input.PunchHeld(hand);
#if ENABLE_INPUT_SYSTEM
        Gamepad pad = Gamepad.current;
        Mouse mouse = Mouse.current;
        return (pad != null && (hand == 0 ? pad.leftTrigger.isPressed : pad.rightTrigger.isPressed))
            || (mouse != null && (hand == 0 ? mouse.leftButton.isPressed : mouse.rightButton.isPressed));
#else
        return Input.GetMouseButton(hand);
#endif
    }

    private bool Trigger(int hand)
    {
        if (_Input != null) return _InputPressed[hand];
#if ENABLE_INPUT_SYSTEM
        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            ButtonControl t = hand == 0 ? pad.leftTrigger : pad.rightTrigger;
            if (t.wasPressedThisFrame)
                return true;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            Vector2 pointer = mouse.position.ReadValue();
            if (ShowPanel && new Rect(10f, 10f, 430f, Mathf.Min(Screen.height - 20f, 620f))
                .Contains(new Vector2(pointer.x, Screen.height - pointer.y))) return false;
            return hand == 0 ? mouse.leftButton.wasPressedThisFrame : mouse.rightButton.wasPressedThisFrame;
        }
        return false;
#else
        return Input.GetMouseButtonDown(hand);
#endif
    }

    private static bool Pressed(int digit)
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        if (kb == null)
            return false;

        return digit switch
        {
            1 => kb.digit1Key.wasPressedThisFrame,
            2 => kb.digit2Key.wasPressedThisFrame,
            3 => kb.digit3Key.wasPressedThisFrame,
            _ => false,
        };
#else
        return false;
#endif
    }

    /************************************************************************************/

    private void OnDrawGizmos()
    {
        if (!DrawGizmos || !Application.isPlaying)
            return;

        Vector3 centre = transform.position + transform.forward * AimDistance
                       + Vector3.up * HeadHeight;

        Gizmos.color = new Color(1f, 1f, 1f, 0.35f);
        const int steps = 28;
        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= steps; i++)
        {
            float a = i / (float)steps * Mathf.PI * 2f;
            Vector3 p = centre + transform.right * (Mathf.Cos(a) * AimRadius)
                               + Vector3.up * (Mathf.Sin(a) * AimRadius);
            if (i > 0)
                Gizmos.DrawLine(prev, p);
            prev = p;
        }

        Gizmos.color = new Color(1f, 0.5f, 0f, 0.5f);
        Gizmos.DrawWireSphere(centre + Vector3.down * BodyDrop, 0.06f);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(_AimWorld, 0.07f);

        AimIK live = _Hand == 0 ? LeftAim : _Hand == 1 ? RightAim : null;
        if (live != null && live.solver != null && live.solver.transform != null)
        {
            Transform fist = live.solver.transform;

            // Green = where the fist points, yellow = where it should. They converge as the
            // solver takes hold, which is the whole of what AimIK does.
            Gizmos.color = Color.green;
            Gizmos.DrawRay(fist.position, fist.TransformDirection(live.solver.axis) * 0.4f);

            Gizmos.color = Color.Lerp(Color.yellow, Color.red, _Weight);
            Gizmos.DrawLine(fist.position, _AimWorld);
        }
    }

    private Vector2 _Scroll;

    private void OnGUI()
    {
        if (!ShowPanel || !IsPlayerControlled)
            return;

        var style = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };

        // Sized to the window and scrollable — a fixed height silently clipped the lower half
        // of the panel as soon as more controls were added.
        float h = Mathf.Min(Screen.height - 20f, 620f);
        GUILayout.BeginArea(new Rect(10, 10, 430, h), GUI.skin.box);
        _Scroll = GUILayout.BeginScrollView(_Scroll);

        GUILayout.Label("<b>Aim study boxer</b>   Animation-led combinations", style);
        GUILayout.Label("LMB/RMB straight | Shift hook | Ctrl uppercut | Alt body", style);
        GUILayout.Label(ReleaseToPunch ? "L2/R2: tap quick, hold to prepare, release to throw" : "L2/R2: punch on press", style);
        GUILayout.Label("Right stick up: hook | down: uppercut | hold: steer", style);
        GUILayout.Label("R1 head guard | L1 body guard | right stick: lean / steer", style);
        GUILayout.Label("Space/MMB head | C body | arrows lean | WASD move", style);
        GUILayout.Label("Q/E turn | 1/2/3 stance | F8 panel", style);
        GUILayout.Label($"buffer {_InputBuffer.Count}/2 | combo {_Combo + 1} | {_ShotType}", style);
        if (ReleaseToPunch)
            GUILayout.Label($"charge L {Charge(0):0%} / R {Charge(1):0%} | holding costs {HoldStaminaPerSecond:0.#}/s per hand", style);
        GUILayout.Label($"defense: head {HeadGuardWeight:0.00} / body {BodyGuardWeight:0.00} | lean {_Lean.x:0.00}, {_Lean.y:0.00}", style);

        GUILayout.Space(6);
        string[] names = { "High energy", "Mid energy", "Low energy" };
        GUILayout.Label($"stance : <color=lime>{names[Mathf.Clamp(Stance, 0, 2)]}</color>", style);
        GUILayout.Label($"target : {(Target != null ? Target.name : "<none — straight ahead>")}", style);
        UseMouseAim = GUILayout.Toggle(UseMouseAim, " mouse steers the aim (off = stick only)");
        GUILayout.Label($"aim    : ({_Aim.x,5:0.00}, {_Aim.y,5:0.00})   " +
                        $"<color={(_Aim.y < -0.35f ? "orange" : "lime")}>" +
                        $"{(_Aim.y < -0.35f ? "BODY" : "HEAD")}</color>", style);
        GUILayout.Label($"last   : {_Last}", style);

        if (UseStamina)
        {
            int filled = Mathf.RoundToInt(Stamina01 * 24f);
            string bar = new string('#', filled).PadRight(24, '.');
            string colour = Stamina01 > 0.6f ? "lime" : Stamina01 > 0.3f ? "yellow" : "red";
            GUILayout.Label($"stamina: <color={colour}>{bar}</color> {Stamina01 * 100f:0}%   " +
                            $"punch speed x{Fatigue():0.00}", style);
        }
        GUILayout.Label($"aim IK : {_Weight * AimWeight:0.00}   " +
                        $"{(_Hand < 0 ? "idle" : _Hand == 0 ? "left arm" : "right arm")}", style);
        GUILayout.Label($"gap    : {_ReachError:0.000} m from the fist to the aim point", style);
        GUILayout.Label($"chain  : {_ChainText}", style);

        GUILayout.Space(8);
        GUILayout.Label("<b>chain</b>  (rebuilds live — watch the gap change)", style);
        IncludeSpine = GUILayout.Toggle(IncludeSpine, " spine");
        IncludeChest = GUILayout.Toggle(IncludeChest, " chest");
        IncludeUpperChest = GUILayout.Toggle(IncludeUpperChest, " upper chest");
        IncludeUpperArm = GUILayout.Toggle(IncludeUpperArm, " upper arm");
        IncludeForeArm = GUILayout.Toggle(IncludeForeArm, " forearm");

        GUILayout.Label($"torso weight {TorsoWeight:0.00}", style);
        TorsoWeight = GUILayout.HorizontalSlider(TorsoWeight, 0f, 1f);

        GUILayout.Label($"upper arm {UpperArmWeight:0.00}", style);
        UpperArmWeight = GUILayout.HorizontalSlider(UpperArmWeight, 0f, 1f);

        GUILayout.Label($"forearm {ForeArmWeight:0.00}   " +
                        "<i>high values fold the elbow mid-punch</i>", style);
        ForeArmWeight = GUILayout.HorizontalSlider(ForeArmWeight, 0f, 1f);

        if (GUILayout.Button("straight-punch preset  (torso .4 / upper 1 / forearm .1)"))
        {
            IncludeSpine = IncludeChest = IncludeUpperChest = IncludeUpperArm = IncludeForeArm = true;
            TorsoWeight = 0.4f;
            UpperArmWeight = 1f;
            ForeArmWeight = 0.1f;
            ClampWeight = 0f;
        }

        GUILayout.Space(8);
        GUILayout.Label("<b>which parts follow the punch</b>", style);

        GUILayout.Label($"body   {BodyFollow:0.00}   <i>torso</i>", style);
        BodyFollow = GUILayout.HorizontalSlider(BodyFollow, 0f, 1f);

        GUILayout.Label($"off hand {OffHandFollow:0.00}   <i>the hand not punching</i>", style);
        OffHandFollow = GUILayout.HorizontalSlider(OffHandFollow, 0f, 1f);

        if (GUILayout.Button("whole body punches (original look)"))
            FullBodyPunch();

        if (_Parts[0] != null)
        {
            GUILayout.Label($"<i>live: torso {_Parts[0].Weight:0.00}  L arm {_Parts[1].Weight:0.00}" +
                            $"  R arm {_Parts[2].Weight:0.00}</i>", style);
        }

        GUILayout.Space(8);
        GUILayout.Label("<b>jab height</b>  (+ aims higher)", style);

        GUILayout.Label($"left {LeftJabHeight:0.0}°", style);
        LeftJabHeight = GUILayout.HorizontalSlider(LeftJabHeight, -40f, 40f);

        GUILayout.Label($"right {RightJabHeight:0.0}°   <i>applied now {_HeightApplied:0.0}°</i>", style);
        RightJabHeight = GUILayout.HorizontalSlider(RightJabHeight, -40f, 40f);

        GUILayout.Label($"shoulder share {ShoulderShare:0.00}", style);
        ShoulderShare = GUILayout.HorizontalSlider(ShoulderShare, 0f, 1f);

        if (GUILayout.Button("match both to the left"))
            RightJabHeight = LeftJabHeight;

        GUILayout.Space(8);
        GUILayout.Label("<b>orientation fix</b>  (idle 180 · L 185.3 · R 192)", style);
        CorrectOrientation = GUILayout.Toggle(CorrectOrientation, " correct while punching");

        if (CorrectOrientation)
        {
            GUILayout.Label($"left {LeftOrientationFix:0.0}°", style);
            LeftOrientationFix = GUILayout.HorizontalSlider(LeftOrientationFix, -180f, 180f);

            GUILayout.Label($"right {RightOrientationFix:0.0}°   <i>applied now {_FixApplied:0.0}°</i>", style);
            RightOrientationFix = GUILayout.HorizontalSlider(RightOrientationFix, -180f, 180f);

            SpreadAcrossSpine = GUILayout.Toggle(SpreadAcrossSpine,
                " spread across the spine (keeps the legs and stance out of it)");

            StabiliseHead = GUILayout.Toggle(StabiliseHead, " keep the head out of it");

            if (StabiliseHead)
            {
                GUILayout.Label($"head {HeadStability:0.00}   neck {NeckStability:0.00}", style);
                HeadStability = GUILayout.HorizontalSlider(HeadStability, 0f, 1f);
                NeckStability = GUILayout.HorizontalSlider(NeckStability, 0f, 1f);
            }

            if (GUILayout.Button("snap both to the nearest 5°"))
            {
                LeftOrientationFix = Mathf.Round(LeftOrientationFix / 5f) * 5f;
                RightOrientationFix = Mathf.Round(RightOrientationFix / 5f) * 5f;
            }
        }

        GUILayout.Space(8);
        GUILayout.Label("<b>solver</b>", style);

        GUILayout.Label($"aim weight {AimWeight:0.00}", style);
        AimWeight = GUILayout.HorizontalSlider(AimWeight, 0f, 1f);

        GUILayout.Label($"clamp {ClampWeight:0.00}   (0 = free, 1 = pinned to the animation)", style);
        ClampWeight = GUILayout.HorizontalSlider(ClampWeight, 0f, 1f);

        GUILayout.Label($"iterations {Iterations}", style);
        Iterations = Mathf.RoundToInt(GUILayout.HorizontalSlider(Iterations, 1f, 12f));

        DriveSolverSettings = GUILayout.Toggle(DriveSolverSettings,
            " this panel drives the solver (untick to edit the AimIK components directly)");

        GUILayout.Space(8);
        GUILayout.Label("<b>window</b>", style);

        GUILayout.Label($"aim window {AimWindow:0.00}   hold {HoldAfterStrike:0.00}s", style);
        AimWindow = GUILayout.HorizontalSlider(AimWindow, 0.05f, 0.6f);
        HoldAfterStrike = GUILayout.HorizontalSlider(HoldAfterStrike, 0f, 0.6f);

        GUILayout.Space(8);
        GUILayout.Label("<b>aim circle</b>", style);

        GUILayout.Label($"radius {AimRadius:0.00} m   distance {AimDistance:0.00} m", style);
        AimRadius = GUILayout.HorizontalSlider(AimRadius, 0.05f, 0.6f);
        AimDistance = GUILayout.HorizontalSlider(AimDistance, 0.2f, 1.2f);

        GUILayout.Label($"head height {HeadHeight:0.00} m   body drop {BodyDrop:0.00} m", style);
        HeadHeight = GUILayout.HorizontalSlider(HeadHeight, 0.8f, 2f);
        BodyDrop = GUILayout.HorizontalSlider(BodyDrop, 0.1f, 0.9f);

        GUILayout.Space(8);
        GUILayout.Label("<b>timing</b>", style);

        GUILayout.Label($"punch speed {PunchSpeed:0.00}", style);
        PunchSpeed = GUILayout.HorizontalSlider(PunchSpeed, 0.3f, 2.5f);

        GUILayout.Label($"blend in {PunchFade:0.00}s   out {RecoverFade:0.00}s", style);
        PunchFade = GUILayout.HorizontalSlider(PunchFade, 0.02f, 0.4f);
        RecoverFade = GUILayout.HorizontalSlider(RecoverFade, 0.05f, 0.6f);

        GUILayout.Label($"next punch may interrupt at {ReplayAfter:0.00}", style);
        ReplayAfter = GUILayout.HorizontalSlider(ReplayAfter, 0.3f, 1f);

        GUILayout.Label($"<i>punch {_PunchWeight:0.00}</i>", style);

        MatchImpactTiming = GUILayout.Toggle(MatchImpactTiming, " match release-to-impact timing");
        UsePerMotionTiming = GUILayout.Toggle(UsePerMotionTiming, " use per-motion timing overrides");
        UseStamina = GUILayout.Toggle(UseStamina, " tired punches are slower");

        if (UseStamina)
        {
            GUILayout.Label($"cost per punch {StaminaPerPunch:0}   recovery {StaminaRecovery:0}/s", style);
            StaminaPerPunch = GUILayout.HorizontalSlider(StaminaPerPunch, 0f, 40f);
            StaminaRecovery = GUILayout.HorizontalSlider(StaminaRecovery, 0f, 60f);

            GUILayout.Label($"speed when gassed {TiredSpeed:0.00}x", style);
            TiredSpeed = GUILayout.HorizontalSlider(TiredSpeed, 0.1f, 1f);
        }

        if (MatchImpactTiming)
        {
            GUILayout.Label($"impact time {ImpactTime:0.00}s", style);
            ImpactTime = GUILayout.HorizontalSlider(ImpactTime, 0.1f, 0.8f);

            AnimationClip left = JabClip(0), right = JabClip(1);
            float l = left != null ? left.length * ReadTiming(left, LeftStrike).Strike : 0f;
            float r = right != null ? right.length * ReadTiming(right, RightStrike).Strike : 0f;
            GUILayout.Label($"raw authored impact: L {l * 1000f:0} ms   R {r * 1000f:0} ms   " +
                            $"-> speed L {(l / Mathf.Max(0.01f, ImpactTime)):0.00}x " +
                            $"R {(r / Mathf.Max(0.01f, ImpactTime)):0.00}x", style);
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
}
