using System;
using System.Collections.Generic;
using RootMotion;
using RootMotion.FinalIK;
using UnityEngine;

/// <summary>
/// Trajectory-driven boxing (req.md / Punch-A-Bunch style). The stick / mouse does not pick a move — it moves a
/// virtual fist inside a volume around the boxer, and the punch emerges from the motion:
///
///   hold L2 / R2 (or LMB / RMB)  →  the right stick / mouse now controls that hand
///   pull back                    →  the fist loads (wind-up), the torso coils
///   flick forward                →  DRIVE: the fist accelerates at the target; a good load + a sharp flick
///                                   is "overdrive" — more muscle, more assist, more effective mass, more stamina
///   wide to the side             →  a hook   ·   inside and high  →  an uppercut   ·   low  →  a body shot
///   release the trigger          →  the hand goes back to guard
///
/// Pipeline (req.md): input → virtual fist (speed/acceleration limited, never teleported) → the REFERENCE
/// skeleton is posed at it by Final IK → <see cref="BoxerPhysicsBody"/>'s muscles pull the physical puppet
/// toward the reference → PhysX simulates momentum, joint limits, the glove hitting the bag, the bag hitting
/// the boxer → the visible skeleton copies the puppet. <see cref="PhysicalFist"/> reads the real contact
/// (relative velocity, squareness, effective mass) and applies what the plain collision couldn't know.
///
/// Without a trigger held the right stick / mouse leans the upper body (slip, duck, lean back) and aims.
/// Throw Mode "Release" keeps the older scheme (hold to aim & charge, release to throw).
/// Attach next to the Animator on the boxer, then run Tools ▸ Boxer ▸ Setup Upper-Body Punch Rig.
/// </summary>
[RequireComponent(typeof(Animator))]
[DefaultExecutionOrder(-10)]
public class BoxerPunchController : MonoBehaviour
{
    public enum Hand { Left = 0, Right = 1 }
    public enum PunchType { Straight = 0, Hook = 1, Body = 2, Uppercut = 3 }
    public enum ThrowMode
    {
        /// <summary>Hold = the Loaded reference pose while the stick aims (height / side); release = the strike pose, harder the longer you held. Poses drive the arm, IK only finishes the fist.</summary>
        Release = 0,
        /// <summary>Hold the trigger, move the stick: pull back to load, flick forward to punch (skill based).</summary>
        Flick = 1,
        /// <summary>Punch on press.</summary>
        Press = 2,
    }
    /// <summary>
    /// Where a hand is in its punch. Public because the physical body keys the kinetic chain, the arm's pinning
    /// and the go-slack-on-contact off it (<see cref="IBoxerBody.SetPunch"/>).
    /// </summary>
    public enum Phase { Guard, Control, Drive, Impact, Recover }

    /// <summary>What gives a punch its shape.</summary>
    public enum PunchShape
    {
        /// <summary>Authored single-frame poses run as a chain (<see cref="PunchPoseChain"/>). The real answer.</summary>
        Poses = 0,
        /// <summary>Full animation clips scrubbed by the punch clock. Needs a clip per hand × type × height.</summary>
        Clips = 1,
        /// <summary>Nothing shapes the arm; IK reaches for the target. Diagnostic only.</summary>
        IKOnly = 2,
    }

    /// <summary>Which active ragdoll actually carries the body.</summary>
    public enum PhysicsBackend
    {
        /// <summary>PuppetMaster (req.md §11) — the real one. <see cref="BoxerPhysics"/>.</summary>
        PuppetMaster = 0,
        /// <summary>The older self-built ConfigurableJoint puppet, kept as an escape hatch. <see cref="BoxerPhysicsBody"/>.</summary>
        SelfBuilt = 1,
    }

    [Serializable]
    public class PunchProfile
    {
        [Tooltip("Fist speed multiplier for this trajectory (hooks and uppercuts travel further, so a touch slower).")]
        [Min(0.1f)] public float speed = 1f;
        [Tooltip("Pause at the target on contact (seconds). Hit-stop is added on top.")]
        [Min(0f)] public float hold = 0.05f;
        [Tooltip("Time to recover back to guard (seconds). This IS the cooldown.")]
        [Min(0.05f)] public float recover = 0.28f;
        [Tooltip("Effective-mass multiplier for this punch type.")]
        [Min(0f)] public float power = 1f;
        [Tooltip("Stamina cost.")]
        [Min(0f)] public float stamina = 6f;

        [Tooltip("Seconds of hold needed for FULL launch power — the READABLE loading. Releasing earlier " +
                 "always fires instantly, it just carries smoothly less; a buffered tap throws at ~70%. " +
                 "Short for jabs, longer for the punches that should look loaded.")]
        [Min(0f)] public float anticipation = 0.12f;

        [Tooltip("Optional drive-rate curve over the punch clock (x: 0 chamber → 1 strike; y: relative rate). " +
                 "Empty = the built-in bell. Per punch type, in the Inspector.")]
        public AnimationCurve driveCurve = new AnimationCurve();

        [Tooltip("Multiplier on this type's reference-pose authority in the mixer.")]
        [Range(0.2f, 1.5f)] public float poseWeight = 1f;

        public PunchProfile() { }
        public PunchProfile(float speed, float hold, float recover, float power, float stamina, float anticipation = 0.12f)
        {
            this.speed = speed; this.hold = hold; this.recover = recover; this.power = power; this.stamina = stamina;
            this.anticipation = anticipation;
        }
    }

    /// <summary>Punch clips for one hand, by punch type. Empty lists fall back to a pure IK/physics punch.</summary>
    [Serializable]
    public class HandClips
    {
        public AnimationClip[] straight = new AnimationClip[0];
        public AnimationClip[] hook = new AnimationClip[0];
        public AnimationClip[] body = new AnimationClip[0];
        public AnimationClip[] uppercut = new AnimationClip[0];

        public AnimationClip[] For(PunchType type)
        {
            switch (type)
            {
                case PunchType.Hook:     return hook;
                case PunchType.Body:     return body;
                case PunchType.Uppercut: return uppercut;
                default:                 return straight;
            }
        }

        public void Set(PunchType type, AnimationClip[] clips)
        {
            switch (type)
            {
                case PunchType.Hook:     hook = clips; break;
                case PunchType.Body:     body = clips; break;
                case PunchType.Uppercut: uppercut = clips; break;
                default:                 straight = clips; break;
            }
        }

        public bool IsEmpty => Count(straight) + Count(hook) + Count(body) + Count(uppercut) == 0;

        private static int Count(AnimationClip[] clips)
        {
            if (clips == null) return 0;
            int n = 0;
            foreach (AnimationClip c in clips) if (c != null) n++;
            return n;
        }
    }

    // ------------------------------------------------------------------ Inspector

    [Header("Animation clips (optional — punches work with IK/physics alone)")]
    [Tooltip("Guard loop for the upper-body layer. Leave empty to reuse the base layer's default state.")]
    public AnimationClip guardClip;

    [Tooltip("Left-hand clips by punch type. Tools ▸ Boxer ▸ Auto-Assign Clips fills these from file names.")]
    public HandClips leftClips = new HandClips();

    [Tooltip("Right-hand clips by punch type.")]
    public HandClips rightClips = new HandClips();

    [Tooltip("Animator layer holding the punch states (created by Tools ▸ Boxer ▸ Setup Upper-Body Punch Rig).")]
    public string upperBodyLayerName = "Upper Body";

    [Tooltip("Weight of the upper-body layer. 1 = the animation IS the punch (animation-first); lower softens it under the IK.")]
    [Range(0f, 1f)]
    [SerializeField] private float animationWeight = 1f;

    [Tooltip("Normalised time in the punch clips where the fist is fully extended (Mixamo punches ≈ 0.35-0.45).")]
    [Range(0.1f, 0.9f)]
    [SerializeField] private float clipStrikeTime = 0.4f;

    [Tooltip("Per-clip strike frames, measured off the rig. Undisputed's real strike times run " +
             "0.15-0.73 of clip length, so the single value above lands most of them early or " +
             "late. Built by Tools > Undisputed > Boxing > Use Undisputed Animations; when it is " +
             "empty or a clip is missing from it, the value above is used instead.")]
    [SerializeField] private PunchClipStrikeTimes clipStrikeTimes;

    [Header("Physics body (active ragdoll — req.md)")]
    [Tooltip("THE physics boxer. On: the muscles chase the reference pose, the gloves are real rigidbodies that " +
             "really collide, the bag really stops the fist, and the visible skeleton is the ragdoll. " +
             "Off: IK-driven punches — the fist is placed rather than driven (the old, safe path).")]
    [SerializeField] private bool usePhysicsBody = false;

    [Tooltip("Which active ragdoll. Puppet Master is what req.md §11 recommends and what the scene is set up " +
             "for; Self Built is the older in-house puppet, kept as an escape hatch.")]
    [SerializeField] private PhysicsBackend physicsBackend = PhysicsBackend.PuppetMaster;

    [Header("Input")]
    [SerializeField] private ThrowMode throwMode = ThrowMode.Release;

    [Tooltip("Flick: stick pulled back at least this far (0-1) counts as a load / wind-up.")]
    [Range(0.05f, 0.9f)]
    [SerializeField] private float loadThreshold = 0.25f;

    [Tooltip("Flick: forward stick speed (units/s) that fires the punch.")]
    [Min(0.5f)]
    [SerializeField] private float flickSpeed = 5f;

    [Tooltip("Flick: a load still counts if the flick comes within this many seconds.")]
    [Min(0.05f)]
    [SerializeField] private float loadMemory = 0.4f;

    [Tooltip("Release mode: holding longer than this starts charging.")]
    [Min(0f)]
    [SerializeField] private float tapThreshold = 0.12f;

    [Tooltip("Release mode: hold time for a full charge.")]
    [Min(0.05f)]
    [SerializeField] private float maxChargeTime = 0.7f;

    [Header("Skill: release timing & rhythm")]
    [Tooltip("Seconds around the full-charge tick that count as ON THE TICK — release inside it and the punch snaps.")]
    [Range(0.02f, 0.2f)] [SerializeField] private float snapWindow = 0.09f;
    [Tooltip("Overdrive added for a release timed on the tick.")]
    [Range(0f, 0.4f)] [SerializeField] private float snapOverdriveBonus = 0.15f;
    [Tooltip("Punch speed bonus for a release timed on the tick.")]
    [Range(0f, 0.25f)] [SerializeField] private float snapSpeedBonus = 0.08f;
    [Tooltip("How much a snapped release tightens the wind-up (subtracted from the rolled coil jitter).")]
    [Range(0f, 0.3f)] [SerializeField] private float snapCoilTighten = 0.10f;
    [Tooltip("Seconds past the tick before a hold counts as OVERCOOKED: deeper, slower, telegraphed. Power is untouched — long holds are also aiming time.")]
    [Min(0.1f)] [SerializeField] private float overcookAfter = 0.75f;
    [Tooltip("Speed multiplier of an overcooked punch.")]
    [Range(0.85f, 1f)] [SerializeField] private float overcookSpeed = 0.93f;

    [Tooltip("Tap the SAME punch again inside this window and Bennett doubles it up: the identical line, a clipped, quicker second shot.")]
    [Min(0f)] [SerializeField] private float doubleUpWindow = 0.5f;
    [Tooltip("How close the stick must be to the previous throw's stick to count as the same punch.")]
    [Range(0.05f, 1f)] [SerializeField] private float doubleUpStickMatch = 0.3f;
    [SerializeField] private bool enableDoubleUp = true;

    [Tooltip("After slipping the bag or a perfect block, a punch fired inside this window is minted as a COUNTER: tighter, 10% harder, its own crack.")]
    [Min(0f)] [SerializeField] private float counterWindow = 0.8f;

    [Header("Balance (BoxerBalance feeds this — consequences scale smoothly, never cooldowns)")]
    [SerializeField] private bool useBalance = true;
    [Tooltip("Punch power multiplier at ZERO balance, measured at the moment of impact.")]
    [Range(0.2f, 1f)] [SerializeField] private float balancePowerFloor = 0.55f;
    [Tooltip("Aim scatter at ZERO balance (metres at 1x scale). Healthy balance (≥ 0.85) never scatters at all — " +
             "a normal standing punch goes exactly where it is aimed.")]
    [Range(0f, 0.3f)] [SerializeField] private float balanceAccuracyScatter = 0.06f;
    [Tooltip("Extra recovery time at zero balance (fraction).")]
    [Range(0f, 1f)] [SerializeField] private float balanceRecoverPenalty = 0.35f;
    [Tooltip("Below this balance an extreme throw (overdriven, or steeply up/down) risks a STUMBLE.")]
    [Range(0f, 0.6f)] [SerializeField] private float stumbleBelow = 0.35f;

    [Tooltip("Below this balance, a heavy unblocked bag hit triggers a NEAR-KNOCKDOWN: a huge stagger and a beat " +
             "of weakness — never a forced fall. A future knockdown system consumes the event.")]
    [Range(0f, 0.6f)] [SerializeField] private float knockdownVulnerability = 0.3f;

    [Header("Lower-body drive (the punch starts at the floor)")]
    [SerializeField] private bool lowerBodyDrive = true;
    [Tooltip("How far the driving foot pivots — heel out, toes toward the target — as the hips fire (degrees).")]
    [Range(0f, 40f)] [SerializeField] private float heelPivotDegrees = 22f;
    [Tooltip("Heel lift of the driving foot at full drive (degrees of toe-down pitch).")]
    [Range(0f, 25f)] [SerializeField] private float heelLiftDegrees = 12f;
    [Tooltip("Body weight carried toward the LEAD leg during a drive (metres at 1x).")]
    [Range(0f, 0.12f)] [SerializeField] private float weightTransfer = 0.045f;

    [Tooltip("How far the glove may visually press INTO the deformed leather (metres) — the soft wrap of skin " +
             "around the knuckles. The carved dent provides all the rest of the sink.")]
    [Range(0f, 0.06f)] [SerializeField] private float gloveLeatherWrap = 0.025f;

    [Tooltip("Draw each live punch in the Scene view: launch → target line, fist velocity, contact point.")]
    [SerializeField] private bool debugTrajectory = true;

    [Header("Simulation (multiplayer-safety seams — no networking yet)")]
    [Tooltip("Seed for THIS boxer's simulation randomness (variation styles, pose variants, scatter, stumbles). " +
             "0 = fresh every run; any other value makes the same inputs replay the same fight.")]
    [SerializeField] private int simulationSeed = 0;

    /// <summary>One line describing the last landed punch's calculated power — the debug overlay shows it.</summary>
    public string LastImpactReport { get; private set; } = "";

    [Tooltip("Hide and lock the mouse cursor when the game is clicked (Esc releases it). Aiming continues from mouse motion.")]
    [SerializeField] private bool hideCursorOnClick = true;

    [Tooltip("Mouse travel → pointer speed while the cursor is hidden.")]
    [Range(0.3f, 5f)]
    [SerializeField] private float mouseSensitivity = 1.6f;

    [Tooltip("Draw a reticle where the mouse is aiming while the cursor is hidden.")]
    [SerializeField] private bool drawReticle = true;

    [Header("Punch volume (how far the controlled hand may roam from guard, metres)")]
    [Min(0f)] [SerializeField] private float backRange = 0.25f;
    [Min(0f)] [SerializeField] private float forwardRange = 0.45f;
    [Min(0f)] [SerializeField] private float sideRange = 0.35f;
    [Min(0f)] [SerializeField] private float verticalRange = 0.4f;

    [Header("Virtual fist (never teleports: speed and acceleration limits)")]
    [Tooltip("Max fist speed while you are just moving the hand around (m/s).")]
    [Min(0.5f)] [SerializeField] private float controlSpeed = 4f;
    [Min(1f)]   [SerializeField] private float controlAcceleration = 40f;

    [Tooltip("Longest a drive may take before it counts as a miss (seconds).")]
    [Min(0.1f)] [SerializeField] private float maxDriveTime = 0.4f;

    [Header("Punch types")]
    [Tooltip("The power hand (right for an orthodox stance).")]
    [SerializeField] private Hand rearHand = Hand.Right;

    [Tooltip("Extra effective mass for the rear hand.")]
    [Min(0f)]
    [SerializeField] private float rearHandPower = 1.25f;

    [Tooltip("Effective-mass multiplier at full overdrive (a loaded, sharply flicked punch).")]
    [Min(1f)]
    [SerializeField] private float overdrivePower = 1.6f;

    [SerializeField] private PunchProfile straightLead = new PunchProfile(1f, 0.05f, 0.26f, 1f, 6f, 0.06f);
    [SerializeField] private PunchProfile straightRear = new PunchProfile(1f, 0.06f, 0.30f, 1.1f, 8f, 0.12f);
    [SerializeField] private PunchProfile hook = new PunchProfile(0.9f, 0.06f, 0.32f, 1.2f, 10f, 0.16f);
    [SerializeField] private PunchProfile body = new PunchProfile(1f, 0.05f, 0.30f, 1.1f, 8f, 0.10f);
    [SerializeField] private PunchProfile uppercut = new PunchProfile(0.85f, 0.07f, 0.34f, 1.25f, 12f, 0.18f);

    [Tooltip("Aim lower than this below the chest is a body shot (metres).")]
    [Min(0f)]
    [SerializeField] private float bodyShotBelowChest = 0.2f;

    [Tooltip("How far a hook arcs out to the side before coming in (metres).")]
    [Min(0f)]
    [SerializeField] private float hookWidth = 0.22f;

    [Tooltip("How far an uppercut dips before rising (metres).")]
    [Min(0f)]
    [SerializeField] private float uppercutDip = 0.2f;

    [Tooltip("Max punch angle above/below horizontal for straights and hooks (degrees).")]
    [Range(5f, 60f)]
    [SerializeField] private float straightPitchLimit = 20f;

    [Tooltip("Pitch range for body shots (degrees, negative = downward).")]
    [SerializeField] private Vector2 bodyPitchRange = new Vector2(-50f, -5f);

    [Tooltip("Pitch range for uppercuts (degrees, positive = upward).")]
    [SerializeField] private Vector2 uppercutPitchRange = new Vector2(15f, 70f);

    [Header("Impact physics")]
    [Tooltip("How far the fist drives into the bag past the surface (metres). Follow-through.")]
    [Range(0f, 0.2f)]
    [SerializeField] private float impactDepth = 0.07f;

    [Tooltip("Distance from the hand bone (wrist) to the knuckles (metres). The knuckles meet the bag, not the wrist.")]
    [Range(0f, 0.2f)]
    [SerializeField] private float fistLength = 0.09f;

    [Tooltip("The fastest a HUMAN fist travels (m/s at 1x character scale). The virtual fist's velocity is " +
             "clamped to this before it reaches a hit sensor — an IK correction can move the fist metres in one " +
             "frame, and that spike must read as a glitch, never as a superhuman punch.")]
    [Min(1f)]
    [SerializeField] private float maxFistSpeed = 13f;

    [Tooltip("Minimum fist bounce off the bag (metres) — the floor under the physical rebound below.")]
    [Range(0f, 0.15f)]
    [SerializeField] private float recoilAmount = 0.04f;

    [Tooltip("Share of the impact speed the fist carries back OUT of the bag (0 = dead stop in the leather). " +
             "The bag's real effective mass at the contact sets the split: the heavy middle kicks back hard, " +
             "a light bottom edge gives way and returns almost nothing.")]
    [Range(0f, 1f)]
    [SerializeField] private float reboundRestitution = 0.3f;

    [Tooltip("Hit-stop: after sinking into the bag the arm stays planted for this long (seconds, scaled by cleanliness). " +
             "The reference poses freeze with it; ImpactFeedback adds the time freeze on top.")]
    [Min(0f)]
    [SerializeField] private float hitStop = 0.05f;

    [Tooltip("Body shock when a punch lands: the torso recoils this many degrees at full strength (pitch back, shoulder pushed back), then settles.")]
    [Range(0f, 15f)]
    [SerializeField] private float impactJolt = 4f;

    [Tooltip("The body is pushed back along the punch by this much at full strength (metres) — the bag pushes back.")]
    [Range(0f, 0.08f)]
    [SerializeField] private float impactShove = 0.02f;

    [Tooltip("How far a missed punch overshoots its target — momentum you have to recover from (metres).")]
    [Range(0f, 0.2f)]
    [SerializeField] private float whiffOvershoot = 0.08f;

    [Tooltip("Recovery time multiplier after missing a bag that was in range.")]
    [Min(1f)]
    [SerializeField] private float whiffRecovery = 1.35f;

    [Header("Skill: rhythm & footwork")]
    [Min(0f)]         [SerializeField] private float comboWindow = 0.7f;
    [Range(0f, 0.3f)] [SerializeField] private float comboSpeedUp = 0.08f;
    [Min(0f)]         [SerializeField] private float alternatingPower = 1.12f;
    [Min(0f)]         [SerializeField] private float sameHandPower = 0.9f;
    [Min(0f)]         [SerializeField] private float steppingInPower = 1.15f;
    [Min(0f)]         [SerializeField] private float retreatingPower = 0.8f;

    [Header("Skill: stamina")]
    [Min(1f)]   [SerializeField] private float maxStamina = 100f;
    [Min(0f)]   [SerializeField] private float overdriveStaminaCost = 8f;
    [SerializeField] private Vector2 bagHitCost = new Vector2(4f, 1.5f);
    [Min(0f)]   [SerializeField] private float staminaRegen = 14f;
    [Min(0f)]   [SerializeField] private float regenDelay = 0.8f;
    [Range(0f, 1f)]   [SerializeField] private float gassedBelow = 0.3f;
    [Range(0.2f, 1f)] [SerializeField] private float gassedSpeed = 0.8f;
    [Range(0.2f, 1f)] [SerializeField] private float gassedPower = 0.7f;

    [Header("Block (L1 / middle mouse / Left Ctrl)")]
    [SerializeField] private bool allowBlock = true;
    [SerializeField] private Vector3 blockGuardOffset = new Vector3(0.12f, -0.06f, 0.26f);
    [Min(0f)]         [SerializeField] private float blockLeanScale = 1.4f;
    [Range(0.1f, 1f)] [SerializeField] private float blockMoveSpeed = 0.6f;
    [Range(0f, 1f)]   [SerializeField] private float blockKnockback = 0.3f;

    [Header("Getting hit by the bag")]
    [Min(0f)]        [SerializeField] private float knockbackPerSpeed = 0.5f;
    [Min(0f)]        [SerializeField] private float maxKnockback = 2.5f;
    [Range(0f, 40f)] [SerializeField] private float staggerLean = 14f;
    [Min(0.1f)]      [SerializeField] private float fullHitSpeed = 3f;

    [Header("Aim")]
    [SerializeField] private Camera aimCamera;
    [SerializeField] private LayerMask aimMask = ~0;
    [SerializeField] private bool targetAssist = true;
    [Min(0.5f)]       [SerializeField] private float assistRange = 2.2f;
    [Range(0.2f, 2f)] [SerializeField] private float assistLateralSpread = 1.1f;
    [Tooltip("Punches aim level with your shoulders by default; stick / mouse up or down moves the aim by this many metres (clamped to the bag).")]
    [Range(0.1f, 0.9f)] [SerializeField] private float assistAimHeight = 0.55f;
    [Min(0.2f)]       [SerializeField] private float aimPlaneDistance = 1.1f;
    [SerializeField] private Vector2 aimHeightRange = new Vector2(0.8f, 1.85f);
    [Range(0f, 90f)]  [SerializeField] private float maxAimAngle = 60f;
    [Min(0.3f)]       [SerializeField] private float maxReach = 0.75f;
    [Range(0.6f, 1f)] [SerializeField] private float reachFraction = 0.92f;
    [Range(0f, 0.3f)] [SerializeField] private float aimSmoothTime = 0.06f;

    [Header("Fist & body")]
    [Tooltip("Extra local rotation on the fist after it is aimed (degrees). Normally zero: the reference pose supplies the fist's roll.")]
    [SerializeField] private Vector3 fistEuler = Vector3.zero;
    [Tooltip("Extra roll of a hook fist about the punch line (degrees, per side). Zero = the Hook pose's own fist roll.")]
    [SerializeField] private float hookExtraRoll = 0f;
    [Tooltip("Extra pitch of an uppercut fist (degrees). Zero = the Uppercut pose's own fist.")]
    [SerializeField] private float uppercutExtraPitch = 0f;
    [Tooltip("How strongly the fist is aimed along the punch line (the finger axis is measured from the rig, so this is safe to raise).")]
    [Range(0f, 1f)] [SerializeField] private float fistRotationWeight = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float elbowGoalWeight = 0.8f;
    [Min(0f)]       [SerializeField] private float bodyLean = 0.06f;
    [Min(0f)]       [SerializeField] private float bodyShotCrouch = 0.06f;
    [Range(0f, 40f)] [SerializeField] private float punchTwistDegrees = 14f;
    [Tooltip("Torso coil while a hand is loaded (degrees, opposite to the punch twist) — the visible wind-up.")]
    [Range(0f, 25f)] [SerializeField] private float loadCoil = 12f;
    [SerializeField] private bool pinFeet = true;

    [Header("Body lean (right stick / mouse without a trigger)")]
    [Tooltip("Sideways lean at full stick (degrees).")]
    [Range(0f, 45f)]  [SerializeField] private float leanDegrees = 25f;
    [Tooltip("Backward lean as % of Lean Degrees. 130 = past the full angle — a real pull-back off a punch.")]
    [Range(0f, 200f)] [SerializeField] private float leanBackwardPercent = 130f;
    [Tooltip("Forward lean (crowding in) as % of Lean Degrees.")]
    [Range(0f, 100f)] [SerializeField] private float leanForwardPercent = 50f;
    [Range(0f, 0.5f)] [SerializeField] private float leanDeadZone = 0.1f;
    [Range(0.2f, 1f)] [SerializeField] private float leanFullAt = 0.6f;
    [SerializeField] private Vector3 leanShares = new Vector3(0.40f, 0.35f, 0.25f);
    [Range(0f, 0.5f)] [SerializeField] private float leanSmoothTime = 0.12f;
    [Range(0f, 15f)]  [SerializeField] private float moveLeanDegrees = 4f;

    [Header("Torso & head tracking")]
    [Range(0f, 1f)] [SerializeField] private float torsoAimWeight = 0.4f;
    [Range(0f, 1f)] [SerializeField] private float headAimWeight = 0.7f;
    [Range(0f, 1f)] [SerializeField] private float idleTrackingWeight = 0.35f;

    [Header("References (auto-filled)")]
    [SerializeField] private FullBodyBipedIK bodyIK;
    [SerializeField] private LookAtIK lookAtIK;
    [SerializeField] private BoxerHitboxes hitboxes;
    [Tooltip("PuppetMaster backend. Auto-found / added when Use Physics Body is on.")]
    [SerializeField] private BoxerPhysics puppetPhysics;
    [Tooltip("Self-built backend. Only used when Physics Backend is Self Built.")]
    [SerializeField] private BoxerPhysicsBody legacyPhysicsBody;

    /// <summary>Whichever body is live, seen only through <see cref="IBoxerBody"/> — the rest of this class
    /// never needs to know which one it got.</summary>
    private IBoxerBody physicsBody;

    [Header("What shapes a punch")]
    [Tooltip("POSES (default): the punch is an authored shape sequence — Guard ▸ Chamber ▸ Strike ▸ Follow — run " +
             "by the whole body as a kinetic chain, and the fist goes where the body sends it. This is the one " +
             "that looks like punching.\n" +
             "CLIPS: full animation clips scrubbed by the punch clock. Only the 6 hook clips exist, so every " +
             "other punch falls back to bare IK.\n" +
             "IK ONLY: no shape at all — the arm just reaches. Diagnostic.")]
    [SerializeField] private PunchShape punchShape = PunchShape.Poses;

    [Tooltip("Where the aim takes over from the authored motion. Below this the fist IS the posed hand — pure " +
             "authored punch; above it the fist converges on the actual target so it lands. Raise it for more " +
             "authored character, lower it for more accuracy.")]
    [Range(0.15f, 0.9f)]
    [SerializeField] private float aimTakeover = 0.45f;

    [Tooltip("How hard a STRAIGHT is held to a straight line. The authored poses wander sideways on the way out, " +
             "which is fine for a hook and wrong for a jab or a cross: this projects the punch back onto the " +
             "line from where it launched to where it is aimed, keeping the authored speed but removing the " +
             "sideways drift. Hooks and uppercuts are untouched — they are supposed to curve.")]
    [Range(0f, 1f)]
    [SerializeField] private float straightPath = 0.8f;

    [Tooltip("MOUSE ONLY. A gamepad stick springs back to centre, so its position IS the punch shape. A mouse " +
             "pointer does not — it sits wherever you are aiming, which is at the target, which reads as " +
             "'straight' every single time. So on mouse the shape comes from how far you MOVE the pointer " +
             "sideways after pressing: flick out for a hook, in for an uppercut, hold still for a straight. " +
             "This is the gain on that flick.")]
    [Range(0.5f, 6f)]
    [SerializeField] private float mouseShapeGain = 2.5f;

    [Tooltip("How fast the aim correction RELEASES during recovery (m/s at 1x scale). On the way home the aim " +
             "has no accuracy job left, so it lets go of the target as a glide instead of snapping off it. " +
             "The drive itself steers continuously and is never rate-limited — a punch must be able to arrive.")]
    [Range(0.5f, 12f)]
    [SerializeField] private float aimCorrectionRate = 3f;

    [Tooltip("The furthest the aim may drag the fist off the authored punch (metres, at character scale). " +
             "This is what stops a punch reading as TWO motions. A hook's pose ends with the hand across the " +
             "body; the aim point is a straight line to the target; with no limit the IK hauls the hand from one " +
             "to the other ON TOP of the punch's own travel, so you see the animation play and then a separate " +
             "sweep onto the target. Clamped, the punch is one motion and simply lands where the technique sent " +
             "it — if that misses, that is a spacing mistake, which is the honest answer (req.md §21, §22).")]
    [Range(0.05f, 1f)]
    [SerializeField] private float maxAimCorrection = 0.3f;

    [Tooltip("Optional ReferencePoseMixer on this character. Its poses shape the arm/torso/guard; the virtual fist still owns the motion.")]
    [SerializeField] private ReferencePoseMixer poseMixer;

    [Tooltip("The variation engine: every throw rolls its own style (speed, coil, height, roll, arc) and the " +
             "derived flavours — overhands, shovel hooks — that the pose library never authored. Auto-added.")]
    [SerializeField] private PunchVariation variationEngine;

    [Tooltip("While holding a punch, the stick moves the loaded fist up/down by this much (metres) on top of the Loaded pose.")]
    [Range(0f, 0.4f)]
    [SerializeField] private float loadAimVertical = 0.18f;

    [Tooltip("…and sideways by this much (metres).")]
    [Range(0f, 0.3f)]
    [SerializeField] private float loadAimLateral = 0.1f;

    [Tooltip("Seconds for the loaded fist to follow the stick.")]
    [Range(0.02f, 0.5f)]
    [SerializeField] private float loadAimSmoothTime = 0.12f;

    [Tooltip("How much the elbow follows the reference pose's elbow instead of the built-in elbow hints, while a strike pose is active.")]
    [Range(0f, 1f)]
    [SerializeField] private float poseElbowWeight = 0.7f;

    [Header("Body life & strike anatomy")]
    [Tooltip("Chin tucks by this many degrees at full strike — hands up, chin down.")]
    [Range(0f, 15f)] [SerializeField] private float chinTuckDegrees = 7f;
    [Tooltip("Share of the punch twist taken by the HIPS (the pivot) instead of the spine. Feet stay pinned by IK.")]
    [Range(0f, 0.6f)] [SerializeField] private float hipTwistShare = 0.35f;
    [Tooltip("Idle micro-sway of the guard (degrees) — the boxer is never a statue.")]
    [Range(0f, 4f)] [SerializeField] private float idleSwayDegrees = 1.4f;
    [Tooltip("Idle vertical bob (metres).")]
    [Range(0f, 0.06f)] [SerializeField] private float idleBob = 0.022f;
    [Tooltip("The wrist is never bent more than this to aim the fist (degrees) — past it the arm aims instead. Stops broken-looking wrists.")]
    [Range(20f, 90f)] [SerializeField] private float wristLimit = 75f;

    // ------------------------------------------------------------------ Public state

    /// <summary>Raised when a punch is driven: hand, type and overdrive (0 = lazy, 1 = fully loaded + flicked).</summary>
    public event Action<Hand, PunchType, float> PunchThrown;

    /// <summary>Raised when a punch lands on a bag, with the physics of the impact.</summary>
    public event Action<Hand, PunchingBag, PunchingBag.HitInfo> PunchLanded;

    /// <summary>Raised when a punch whiffs — drove to full extension without touching a bag.</summary>
    public event Action<Hand> PunchMissed;

    /// <summary>Raised when the bag swings into the boxer: strength 0..1 and whether it was blocked.</summary>
    public event Action<float, bool> BagContact;

    public Vector3 AimPoint { get; private set; }
    public Vector2 MouseOffset { get; private set; }
    public PunchingBag AssistTarget { get; private set; }

    /// <summary>The opposing fighter the aim is currently drawn to, if one is in range and in front.</summary>
    public BoxerHealth AssistOpponent { get; private set; }
    public float AssistDistance { get; private set; } = float.PositiveInfinity;
    public float Stamina { get; private set; }
    public float Stamina01 => maxStamina > 0f ? Stamina / maxStamina : 1f;
    public bool IsGassed => Stamina01 < gassedBelow;
    public bool IsBlocking { get; private set; }
    public bool IsBodyGuard { get; private set; }
    public bool IsPunching => punches[0].phase >= Phase.Drive || punches[1].phase >= Phase.Drive;

    /// <summary>True while a punch is actively FLYING (Drive) — feedback systems must never freeze time under it.</summary>
    public bool IsDriving => punches[0].phase == Phase.Drive || punches[1].phase == Phase.Drive;

    /// <summary>Whether ONE specific hand is mid-drive (0 left, 1 right) — feedback gates need per-hand truth.</summary>
    public bool IsHandDriving(int hand) => punches[Mathf.Clamp(hand, 0, 1)].phase == Phase.Drive;

    /// <summary>
    /// The exact phase one hand is in (0 left, 1 right).
    /// </summary>
    /// <remarks>
    /// <see cref="IsHandDriving"/> is only true during Drive, which ends the instant contact
    /// begins — anything that must stay engaged THROUGH the hit (aiming, IK, feedback) needs
    /// to see Impact as well, so it needs the phase rather than a flying/not-flying flag.
    /// </remarks>
    public Phase HandPhase(int hand) => punches[Mathf.Clamp(hand, 0, 1)].phase;

    /// <summary>How far the torso is leaned right now, 0..1 of the configured maximum — the balance model reads this.</summary>
    public float LeanMagnitude01 => Mathf.Clamp01(lean.magnitude / Mathf.Max(1f, leanDegrees));

    /// <summary>How charged a held hand is (0-1), for telegraphs; 0 when the hand is not being held.</summary>
    public float Charge(Hand hand)
    {
        PunchState p = punches[(int)hand];
        return p.phase == Phase.Control ? p.charge : 0f;
    }
    public bool IsAiming => pressed[0] || pressed[1];
    public bool IsKnockedDown => physicsBody != null && physicsBody.IsKnockedDown;

    /// <summary>The live physical body, or null while the boxer is running on IK alone.</summary>
    public IBoxerBody PhysicsBody => physicsBody;
    public float Overdrive(Hand hand) => punches[(int)hand].overdrive;

    /// <summary>The punch type that hand is currently throwing (or last threw) — VFX and feedback read this.</summary>
    public PunchType CurrentType(Hand hand) => punches[(int)hand].type;

    /// <summary>
    /// Everything that decided where one hand is THIS frame — captured for <see cref="PunchDebug"/>, which
    /// watches the pipeline live and names the component responsible when the arm does something wrong.
    /// </summary>
    public struct HandPipelineState
    {
        public Phase phase;
        public float x;                 // punch clock
        public float steer;             // how much the aim has taken over (0 = pure authored pose)
        public Vector3 animatedHand;    // the posed hand, before any aim correction
        public Vector3 virtualFist;     // where the controller put the fist
        public Vector3 fistVelocity;
        public Vector3 target;
        public Vector3 launch;
        public Vector3 correction;      // aim correction actually applied (rate-limited)
        public Vector3 projection;      // straight-line projection displacement
    }

    private readonly HandPipelineState[] pipelineState = new HandPipelineState[2];
    public HandPipelineState PipelineState(int hand) => pipelineState[Mathf.Clamp(hand, 0, 1)];

    // ------------------------------------------------------------------ Private state

    private class PunchState
    {
        public Phase phase;
        public float time;
        public PunchType type;
        public PunchProfile profile;
        public float overdrive;
        public float speed = 1f;            // combo / stamina factor
        public float recoverScale = 1f;     // whiff penalty
        public float hitStopLeft;
        public float loadDepth;             // deepest wind-up seen (0-1)
        public float loadTime = -10f;
        public Vector2 loadStick;           // where the stick was while loading — that decides the punch, not the flick frame
        public Vector2 pressBody;           // pointer/stick at the moment the hand was pressed (mouse shape origin)
        public bool hasLoadStick;
        public float charge;                // Release mode
        public float heldSince;

        /// <summary>
        /// This punch has already picked its pose variants, so the release must not pick again.
        /// </summary>
        /// <remarks>
        /// The chamber is synthesised FROM the strike pose (guard + coil·(guard − strike)). Rolling a
        /// different strike at release therefore redefines the chamber the body spent the whole hold
        /// coiling into, and the posed hand teleports on that one frame — measured at 12.3 m/s sideways
        /// against 4.1 m/s along the punch. Invisible while every slot held a single pose (RollVariants
        /// only reassigns a slot with more than one), so it surfaced only once the library grew variants.
        /// </remarks>
        public bool variantsRolled;

        /// <summary>The strike time of the clip this punch actually selected, or 0 if unknown.</summary>
        public float clipStrike;

        public Vector3 target;
        public Vector3 launchPos;           // where the fist was when the drive began — the straight-line origin
        public Vector3 direction;
        public Vector3 right;
        public int side;
        public float driveDistance = 0.3f;
        public Quaternion fistRotation = Quaternion.identity;
        public float powerScale = 1f;
        public bool landed;
        public bool missed;
        public PunchingBag landedBag;
        public Vector3 contactPoint;
        public float recoil;
        public Vector3 impactStart;         // where the fist was when the glove touched the bag
        public float sinkTime;              // seconds the bag takes to stop the fist (2·depth / speed)
        public float height01 = 0.5f;       // aim height of this punch: 0 low · 0.5 body · 1 head (drives pose variants)
        public float queuedAt = -10f;       // when the player pressed during impact/recovery — buffered, fires when ready
        public bool chargeNotified;         // full-charge rumble tick fired for this hold
        public float stickHook;             // continuous type mix at release: outward stick = hook…
        public float stickUpper;            // …down stick = uppercut, in-between = a real hybrid
        public float stickStraight;
        public Vector2 queuedStick;         // the stick AS IT WAS when the buffered press happened (identity snapshot)
        public float xAtRecover;            // timeline x where the recovery started — it plays back down from there
        public int clipStateHash;           // Animator state being SCRUBBED for this punch (0 = none)
        public bool clipBorrowed;           // the clip is from another punch type — IK corrects it harder
        public PunchType clipType;          // which type the selected clip was chosen for
        public float aimLateral;            // signed aim side (-1 … +1) this punch was loaded/thrown at
        public float reboundSpeed;          // fist exit speed reflected off the bag (m/s), from mass + restitution
        public float plantDepth;            // how deep THIS punch is allowed to sink (world m) — the push-out allowance
        public bool overcooked;             // held far past the tick: deeper, slower, telegraphed
        public string flavor;               // the rolled style's readable name ("overhand", "counter", …), or null
        public float prevThrowTime;         // when THIS hand last fired — the double-up window reads it
        public PunchType prevType;
        public Vector2 prevStick;
        public float prevHeight01;
        public float prevAimLateral;
        public float arcScale = 1f;         // per-throw hook width / uppercut dip multiplier (variation engine)
        public float rollExtra;             // per-throw extra fist roll about the punch line (degrees, signed)
        public Vector3 appliedCorrection;   // the aim correction as actually applied — rate-limited, never a jump
        public Vector3 recoverFrom;         // exactly where the fist was when the recovery began
        public bool recoverAnchored;        // recoverFrom captured for this recovery
        public Vector3 recoverExitVel;      // fist velocity at the moment recovery anchored (elastic exit)
        public float coilBlend;             // how deep the visible coil is right now — eased, never snapped
    }

    private struct LeanBone
    {
        public Transform bone;
        public float share;
    }

    private readonly PunchState[] punches = { new PunchState(), new PunchState() };
    private readonly bool[] pressed = new bool[2];
    private readonly float[] armLength = new float[2];
    private readonly Transform[] shoulders = new Transform[2];
    private readonly Vector3[] fingerAxisLocal = { Vector3.forward, Vector3.forward };   // hand-bone-local direction of the fingers
    private readonly Transform[] elbowGoals = new Transform[2];
    private readonly Vector3[] fistPos = new Vector3[2];
    private readonly Vector3[] fistVel = new Vector3[2];
    private readonly bool[] fistValid = new bool[2];
    private readonly Vector3[] loadOffset = new Vector3[2];
    private readonly Vector3[] loadOffsetVel = new Vector3[2];
    private readonly HashSet<string> animatorParameters = new HashSet<string>();
    private readonly List<LeanBone> leanBones = new List<LeanBone>();

    /// <summary>This boxer's intent. The player's forwards to BoxerInput; an AI opponent writes its own.</summary>
    private IBoxerInput input = PlayerBoxerInput.Instance;

    /// <summary>Hand this boxer over to an AI (or back to the player). Call before or after Awake, either works.</summary>
    public void SetInput(IBoxerInput source)
    {
        input = source ?? PlayerBoxerInput.Instance;
        if (!input.IsPlayer) { BoxerInput.StopRumble(); }
    }

    /// <summary>True while a human is driving this boxer.</summary>
    public bool IsPlayerControlled => input == null || input.IsPlayer;

    private Animator animator;
    private Controller locomotion;
    private Transform chest;
    private Transform head;

    private Vector2 aimBody;                // the body stick as last seen with no trigger held (aim + lean)
    private Vector3 aimVelocity;
    private float lookWeight;
    private int upperLayerIndex = -1;

    private Vector2 lean;
    private Vector2 leanVelocity;
    private Vector2 impactLean;
    private Vector2 impactLeanVelocity;
    private float twist;
    private float blockBlend;

    private float bodyShock;                // 0-1, jumps when a punch lands and settles in ~0.25 s
    private float bodyShockVelocity;
    private Vector3 shockDirection;
    private int shockSide;

    private readonly float[] bagPush = new float[2];      // smoothed glove push-out from the live bag (metres)
    private readonly Vector3[] bagPushDir = new Vector3[2];

    /// <summary>Character scale, so every hand-space distance means the same thing on a 1x or a 2x rig.</summary>
    private float CharacterScale => Mathf.Max(0.01f, transform.lossyScale.y);

    /// <summary>Wrist → knuckles in WORLD metres. fistLength is authored at 1x; Bennett's rig is 2x — using the
    /// raw value registered every contact half a glove late and deep, blunting every impact cue at the source.</summary>
    private float FistReach => fistLength * CharacterScale;

    private Transform hipsBone;
    private Transform neckBone;
    private Vector2 sway;                   // idle Perlin micro-sway (degrees)
    private float maxStrikeWeight;          // biggest live strike weight this frame (chin tuck, idle fade)
    private float idleWeight;               // 1 = standing quiet, 0 = punching/leaning
    private readonly Vector3[] footPos = new Vector3[2];
    private readonly Quaternion[] footRot = new Quaternion[2];
    private bool feetValid;
    private Vector3 prevRootPos;
    private Quaternion prevRootRot = Quaternion.identity;
    private bool rootAnchorValid;
    private float lastClipTime = -10f;
    private float lastWhiffLog = -10f;
    private float lastEvadeTime = -10f;   // slip / perfect block — the counter window opens from here
    private BoxerBalance balance;
    private readonly Queue<PunchCommand> commandLog = new Queue<PunchCommand>();
    private readonly Transform[] lowerArms = new Transform[2];

    /// <summary>This boxer's seeded simulation stream — every fight-affecting roll draws from it.</summary>
    public SimRng Rng { get; private set; }

    /// <summary>SEAM 2: raised for every punch that enters the simulation — the prediction/replay hook.</summary>
    public event Action<PunchCommand> PunchRequested;

    /// <summary>A heavy hit landed on a badly unbalanced boxer (strength 0-1). A knockdown system consumes this.</summary>
    public event Action<float> NearKnockdown;

    private float staggerUntil = -10f;

    /// <summary>The last punches requested, oldest first — reconciliation and replay read this.</summary>
    public IEnumerable<PunchCommand> RecentCommands => commandLog;
    private readonly float[] heelDrive = new float[2];
    private readonly float[] heelDriveVel = new float[2];

    // The Released Chain: one master clock per hand owns pose, fist, clip and hit-stop together.
    private readonly PunchTimeline[] timelines = { new PunchTimeline(), new PunchTimeline() };

    // The coil's shape (straight/hook/upper/height) as the mixer sees it during a hold — SMOOTHED, because a
    // stick flick used to rebuild the chamber instantly and the whole arm stepped to the new shape in one frame.
    private readonly Vector4[] coilShape = new Vector4[2];
    private readonly Vector4[] coilShapeVelocity = new Vector4[2];
    private readonly float[] coilLateral = new float[2];         // smoothed aim SIDE while holding — drives the 2D loading grid
    private readonly float[] coilLateralVelocity = new float[2];
    private static readonly int GuardStateHash = Animator.StringToHash("Guard");

    /// <summary>Punch progress of a hand (0 guard · ~0.18 chamber · 1 full strike) — for VFX and feedback systems.</summary>
    public float TimelineX(int hand) => timelines[hand].X;

    /// <summary>The rolled style name of this hand's current/last punch ("overhand", "counter", …) or null.</summary>
    public string LastFlavor(Hand hand) => punches[(int)hand].flavor;
    private float blockStartTime = -10f;
    private float energySmoothed = 1f;
    private float energyVelocity;
    private static readonly int EnergyHash = Animator.StringToHash("Energy");

    private PunchingBag[] bags = new PunchingBag[0];
    private BoxerHealth[] opponents = new BoxerHealth[0];
    private readonly Collider[] zoneHits = new Collider[8];
    private float nextBagScan;
    private float nextOpponentScan;
    private bool sensorsHooked;

    private int comboCount;
    private float lastThrowTime = -10f;
    private Hand lastHand = Hand.Left;
    private float lastStaminaSpend = -10f;
    private float lastContactTime = -10f;

    private Texture2D reticleTexture;

    private static readonly int PunchLeftHash = Animator.StringToHash("PunchLeft");
    private static readonly int PunchRightHash = Animator.StringToHash("PunchRight");
    private static readonly int PunchIndexHash = Animator.StringToHash("PunchIndex");
    private static readonly int PunchTypeHash = Animator.StringToHash("PunchType");
    private static readonly int PunchSpeedHash = Animator.StringToHash("PunchSpeed");

    // ------------------------------------------------------------------ Lifecycle

    private void Awake()
    {
        animator = GetComponent<Animator>();
        locomotion = GetComponent<Controller>();
        if (aimCamera == null) aimCamera = Camera.main;

        chest = Bone(HumanBodyBones.Chest);
        if (chest == null) chest = Bone(HumanBodyBones.Spine);
        if (chest == null) chest = transform;
        head = Bone(HumanBodyBones.Head);
        if (head == null) head = chest;
        hipsBone = Bone(HumanBodyBones.Hips);
        neckBone = Bone(HumanBodyBones.Neck);

        MeasureArms();
        BuildLeanChain();
        EnsureIK();

        for (int i = 0; i < 2; i++)
        {
            elbowGoals[i] = new GameObject(i == 0 ? "Left Elbow Goal" : "Right Elbow Goal").transform;
            elbowGoals[i].SetParent(transform, false);
        }

        if (hitboxes == null) hitboxes = GetComponent<BoxerHitboxes>();
        if (poseMixer == null) poseMixer = GetComponent<ReferencePoseMixer>();
        if (variationEngine == null) variationEngine = GetComponent<PunchVariation>();
        if (variationEngine == null) variationEngine = gameObject.AddComponent<PunchVariation>();
        balance = GetComponent<BoxerBalance>();
        if (balance == null && useBalance) balance = gameObject.AddComponent<BoxerBalance>();
        lowerArms[0] = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        lowerArms[1] = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);

        // DETERMINISM: one seeded stream per boxer for everything that changes the fight. Presentation
        // randomness stays on UnityEngine.Random and may diverge freely.
        uint rngSeed = simulationSeed != 0 ? (uint)simulationSeed : (uint)(Environment.TickCount ^ name.GetHashCode());
        Rng = new SimRng(rngSeed);
        if (simulationSeed != 0) Debug.Log($"BoxerPunchController: deterministic simulation, seed {simulationSeed}.", this);
        if (variationEngine != null) variationEngine.Rng = Rng;
        if (poseMixer != null) poseMixer.Rng = Rng;

        // Assist range is a WORLD distance and the scene's value predates the 2x character: standing at normal
        // punching range could drop the bag out of assist, sending the aim to the free-aim fallback mid-combo.
        assistRange = Mathf.Max(assistRange, 1.6f * CharacterScale);
        if (punchShape == PunchShape.Poses)
        {
            if (poseMixer == null) poseMixer = gameObject.AddComponent<ReferencePoseMixer>();   // poses auto-load from Resources
            poseMixer.enabled = true;
            if (!poseMixer.HasPoses)
            {
                Debug.LogError("BoxerPunchController: Punch Shape is Poses but NO reference poses are loaded — " +
                               "punches will be a bare IK reach. Run Tools ▸ Boxer ▸ Poses ▸ Reference Pose Baker " +
                               "to bake Assets/Refrences into Resources/ReferencePoses.", this);
                poseMixer = null;
            }
        }
        else if (poseMixer != null)
        {
            // The clips (or nothing) own the body. The pose mixer must not touch a single muscle.
            poseMixer.enabled = false;
            poseMixer = null;
        }
        // The mixer needs to know which hand is the POWER hand: the lead hand's pose library is a mirror of
        // the rear's, and its legwork/guard describe the wrong stance — the mixer masks those down per hand.
        if (poseMixer != null) poseMixer.RearHandIndex = (int)rearHand;

        if (usePhysicsBody) BuildPhysicalBody();
        else NeutralizeStrayPuppet();

        if (physicsBody != null)
        {
            // The physical gloves are the sensors now; the trigger gloves would double-count every hit.
            if (hitboxes != null) hitboxes.enabled = false;
        }
        else if (hitboxes == null)
        {
            hitboxes = gameObject.AddComponent<BoxerHitboxes>();
        }
        if (hitboxes != null) hitboxes.KnuckleOffset = FistReach;   // the sensor sits at the knuckles, where the target is measured

        CacheAnimatorInfo();
        if (punchShape == PunchShape.Clips) ReportClipCoverage();
        else if (punchShape == PunchShape.Poses && poseMixer != null)
            Debug.Log("BoxerPunchController: POSE-DRIVEN punches. Every punch is Guard ▸ Chamber ▸ Strike ▸ " +
                      "Follow, unwound hips-first as a kinetic chain; the fist follows the posed hand until " +
                      $"x = {aimTakeover:0.00} and only then steers onto the target.", this);
        Stamina = maxStamina;
        AimPoint = chest.position + transform.forward * aimPlaneDistance;
    }

    /// <summary>
    /// Wake the physical body — the moment Bennett stops being an animation with colliders and becomes a boxer.
    /// This has to happen in Awake: PuppetMaster needs to take the Final IK solvers away from IKExecutionOrder
    /// before that component's Start() disables them, or Active mode ends up pinning the ragdoll to a pose that
    /// never had IK applied (see <see cref="BoxerPhysics"/>). If a body cannot be brought up we simply stay on
    /// the IK path rather than shipping a broken one.
    /// </summary>
    /// <summary>
    /// Use Physics Body is OFF: make sure no PuppetMaster runs this character anyway. A puppet left active in
    /// the scene keeps simulating and MAPPING entirely on its own — stretching the body, dragging the visible
    /// hands off the punch line, and holding the knuckles short of the bag so no hit (and no sound, no VFX)
    /// ever fires. The checkbox is the single source of truth; a stray active puppet is put to sleep here.
    /// </summary>
    private void NeutralizeStrayPuppet()
    {
        foreach (RootMotion.Dynamics.PuppetMaster pm in FindObjectsByType<RootMotion.Dynamics.PuppetMaster>())
        {
            if (pm.transform.root != transform.root) continue;   // another character's puppet is not ours to touch
            pm.gameObject.SetActive(false);
            Debug.Log($"BoxerPunchController: Use Physics Body is OFF — PuppetMaster '{pm.name}' deactivated so it " +
                      "cannot fight the animation or keep punches from reaching the bag.", this);
        }
    }

    private void BuildPhysicalBody()
    {
        if (physicsBackend == PhysicsBackend.PuppetMaster)
        {
            if (puppetPhysics == null) puppetPhysics = GetComponent<BoxerPhysics>();
            if (puppetPhysics == null) puppetPhysics = gameObject.AddComponent<BoxerPhysics>();
            puppetPhysics.Initialize(this, animator, bodyIK);
            // BoxerPhysics disables itself if there is no usable PuppetMaster; IsBuilt only completes once
            // PuppetMaster has initiated, which is later in this same frame.
            physicsBody = puppetPhysics.enabled ? puppetPhysics : null;
            if (physicsBody == null)
                Debug.LogWarning("BoxerPunchController: PuppetMaster body could not start — staying on IK punches. " +
                                 "Run Tools ▸ Boxer ▸ Physics ▸ Setup Physics Boxer.", this);
            return;
        }

        if (legacyPhysicsBody == null) legacyPhysicsBody = GetComponent<BoxerPhysicsBody>();
        if (legacyPhysicsBody == null) legacyPhysicsBody = gameObject.AddComponent<BoxerPhysicsBody>();
        legacyPhysicsBody.Initialize(this, animator, bodyIK);
        physicsBody = legacyPhysicsBody.IsBuilt ? legacyPhysicsBody : null;
    }

    private void OnEnable()
    {
        if (bodyIK != null)
        {
            bodyIK.solver.OnPreUpdate += OnPreFullBodySolve;
            bodyIK.solver.OnPostUpdate += OnPostFullBodySolve;
        }
    }

    private void OnDisable()
    {
        if (bodyIK != null)
        {
            bodyIK.solver.OnPreUpdate -= OnPreFullBodySolve;
            bodyIK.solver.OnPostUpdate -= OnPostFullBodySolve;
            ResetEffector(bodyIK.solver.leftHandEffector);
            ResetEffector(bodyIK.solver.rightHandEffector);
            ResetEffector(bodyIK.solver.leftFootEffector);
            ResetEffector(bodyIK.solver.rightFootEffector);
            bodyIK.solver.leftArmChain.bendConstraint.weight = 0f;
            bodyIK.solver.rightArmChain.bendConstraint.weight = 0f;
        }

        for (int i = 0; i < 2; i++)
        {
            punches[i].phase = Phase.Guard;
            pressed[i] = false;
            fistValid[i] = false;
            IPunchSensor sensor = GetSensor((Hand)i);
            if (sensor != null) { sensor.Armed = false; sensor.PowerScale = 1f; }
            physicsBody?.SetHand(i, false, Vector3.zero, Vector3.zero, 0f);
        }

        IsBlocking = false;
        if (locomotion != null) locomotion.SpeedMultiplier = 1f;
        if (IsPlayerControlled)
        {
            BoxerInput.StopRumble();
            BoxerInput.ReleaseCursor();
        }
    }

    private void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);   // a frame spike must never integrate a wild simulation step

        if (input.IsPlayer)
        {
            BoxerInput.HideCursorOnClick = hideCursorOnClick;
            BoxerInput.MouseSensitivity = mouseSensitivity;
        }
        input.Poll();
        HookSensors();
        ScanBags();

        // While a trigger is held the stick belongs to the PUNCH (shape + height) — the body stops leaning with it,
        // so the shoulders hold still through the hold and the release flies from a stable base.
        Vector2 body = input.Body;
        if (!input.PunchHeld(0) && !input.PunchHeld(1)) aimBody = body;
        MouseOffset = aimBody;

        UpdateBlock();
        UpdateAim(dt);

        if (IsKnockedDown)
        {
            // Down: no punching, no walking; the puppet does the falling.
            for (int i = 0; i < 2; i++) if (punches[i].phase != Phase.Guard) CancelPunch(i);
            if (locomotion != null) locomotion.SpeedMultiplier = 0f;
        }
        else
        {
            HandleHand(Hand.Left, input.PunchHeld(0), body, dt);
            HandleHand(Hand.Right, input.PunchHeld(1), body, dt);
        }

        UpdateClips();
        UpdatePoseIntent();
        UpdateStamina(dt);
        UpdateLookAt(dt);
        UpdateLean(dt);
    }

    private void FixedUpdate()
    {
        // Follow-through: while the fist is planted, keep shoving — but only with energy the punch itself banked
        // on the bag (a fraction of the landed impulse). The shove deepens a punch; it can never out-punch it.
        for (int i = 0; i < 2; i++)
        {
            PunchState p = punches[i];
            if (p.phase != Phase.Impact || !p.landed || p.landedBag == null || !p.landedBag.IsBuilt) continue;
            p.landedBag.FollowThrough(i, p.contactPoint, p.direction, Time.fixedDeltaTime);
        }
    }

    // ------------------------------------------------------------------ Hand state machine

    private void HandleHand(Hand hand, bool isPressed, Vector2 stick, float dt)
    {
        int i = (int)hand;
        PunchState p = punches[i];
        bool wasPressed = pressed[i];
        pressed[i] = isPressed;
        p.time += dt;

        if (IsBlocking && p.phase < Phase.Drive)
        {
            // Blocking cancels a hold through a REAL recovery (the arm eases home), never a hard snap to guard.
            if (p.phase == Phase.Control)
            {
                p.phase = Phase.Recover;
                p.time = 0f;
                p.recoverScale = 0.5f;
                p.missed = false;
                p.xAtRecover = timelines[i].X;
            }
            return;
        }

        switch (p.phase)
        {
            case Phase.Guard:
                if (isPressed && !wasPressed)
                {
                    if (throwMode == ThrowMode.Press) RequestPunch(new PunchCommand { hand = hand, overdrive = 0f, stick = stick, time = Time.time });
                    else BeginControl(hand, stick);
                }
                break;

            case Phase.Control:
                if (throwMode == ThrowMode.Release)
                {
                    if (!isPressed) { RequestPunch(new PunchCommand { hand = hand, overdrive = p.charge, stick = p.hasLoadStick ? p.loadStick : stick, time = Time.time }); break; }
                    float held = Time.time - p.heldSince - tapThreshold;
                    p.charge = Mathf.Clamp01(held / Mathf.Max(0.01f, maxChargeTime - tapThreshold));
                    if (p.charge >= 1f && !p.chargeNotified) { p.chargeNotified = true; Rumble(0.1f, 0.5f, 0.06f); }   // full charge: a crisp tick in the hand
                    p.overcooked = Time.time - (p.heldSince + maxChargeTime) > overcookAfter;   // sat on the beat too long
                    p.loadStick = ShapeStick(p, stick);   // how you move decides the shape; where you point decides the height
                    p.hasLoadStick = true;
                    p.loadDepth = 1f;             // the wind-up is shown in full while holding
                    p.type = Classify(p.loadStick, p.side);
                    p.profile = ProfileFor(hand, p.type);
                    p.height01 = HoldHeight(p.loadStick, p.side);
                    SelectClip(hand, p);          // the stick reshapes the punch live: the held clip swaps with the type
                }
                else
                {
                    if (!isPressed) { p.phase = Phase.Recover; p.time = 0f; p.recoverScale = 0.6f; p.missed = false; break; }

                    // Load: how far back the stick has been pulled recently.
                    if (stick.y < -loadThreshold)
                    {
                        p.loadDepth = Mathf.Max(p.loadDepth, Mathf.Clamp01((-stick.y - loadThreshold) / (1f - loadThreshold)));
                        p.loadTime = Time.time;
                        // Direction is chosen while loading: keep the most sideways stick seen during the load.
                        Vector2 shaped = ShapeStick(p, stick);
                        if (!p.hasLoadStick || Mathf.Abs(shaped.x) >= Mathf.Abs(p.loadStick.x) - 0.05f) { p.loadStick = shaped; p.hasLoadStick = true; }
                    }
                    else if (Time.time - p.loadTime > loadMemory) p.loadDepth = 0f;

                    // Flick: a sharp forward stick motion fires the punch. The load decides how much of it is overdrive.
                    float forwardSpeed = input.BodyVelocity.y;
                    if (forwardSpeed > flickSpeed && stick.y > -0.9f)
                    {
                        float flick = Mathf.Clamp01((forwardSpeed - flickSpeed) / flickSpeed);
                        float loaded = Time.time - p.loadTime <= loadMemory ? p.loadDepth : 0f;
                        float overdrive = Mathf.Clamp01(0.55f * loaded + 0.45f * flick * (0.4f + 0.6f * loaded));
                        RequestPunch(new PunchCommand { hand = hand, overdrive = overdrive, stick = p.hasLoadStick && loaded > 0f ? p.loadStick : stick, time = Time.time });
                    }
                    else
                    {
                        p.type = Classify(p.hasLoadStick ? p.loadStick : stick, p.side);
                        p.profile = ProfileFor(hand, p.type);
                    }
                }
                break;

            case Phase.Drive:
            {
                if (!p.landed) TryLandOnSurface(hand, p, i);      // frame-exact: the knuckles crossing the bag surface
                if (p.landed) BeginImpact(p, i);
                else if (timelines[i].X >= 1f || p.time > maxDriveTime / Mathf.Max(0.01f, p.speed))
                {
                    // Missed: the punch played to full extension without touching a bag — pay for the air.
                    p.missed = true;
                    p.recoverScale = AssistTarget != null ? whiffRecovery : 1.1f;
                    if (AssistTarget != null) SpendStamina(2f);
                    p.phase = Phase.Recover;
                    p.time = 0f;
                    p.xAtRecover = Mathf.Clamp01(timelines[i].X);
                    Rumble(0.04f, 0.22f, 0.05f);   // a thin air-tick: a whiff reads as a spacing mistake, not lag
                    LogWhiff(hand, p, i);          // a silent bag must be diagnosable from one console line
                    PunchMissed?.Invoke(hand);
                    IPunchSensor armed = GetSensor(hand);
                    if (armed != null) armed.Armed = false;
                }
                break;
            }

            case Phase.Impact:
                PressIntoBag(p, i);
                if (isPressed && !wasPressed) { p.queuedAt = Time.time; p.queuedStick = stick; }   // buffer + stick snapshot
                if (p.time >= ImpactDuration(p))
                {
                    p.phase = Phase.Recover;
                    p.time = 0f;
                    p.recoverScale = 1f;
                    p.xAtRecover = 1f;
                    if (p.landedBag != null && physicsBody == null) p.landedBag.ClearContact(i);
                    IPunchSensor armed = GetSensor(hand);
                    if (armed != null) armed.Armed = false;
                }
                break;

            case Phase.Recover:
            {
                if (isPressed && !wasPressed) { p.queuedAt = Time.time; p.queuedStick = stick; }   // buffer + stick snapshot
                float duration = RecoverDuration(p);
                bool done = p.time >= duration;
                // A queued DOUBLE (same punch, same line, inside the window) fires much earlier than any
                // ordinary buffered punch — the rat-tat lives in that clipped gap.
                bool wouldDouble = enableDoubleUp && Time.time - p.queuedAt < 0.35f
                                   && Time.time - p.prevThrowTime < doubleUpWindow
                                   && Classify(p.queuedStick, p.side) == p.prevType
                                   && (p.queuedStick - p.prevStick).magnitude < doubleUpStickMatch;
                bool canRetake = p.time >= duration * (wouldDouble ? 0.25f : 0.4f);
                bool queued = Time.time - p.queuedAt < 0.35f;
                if (done || (canRetake && (isPressed || queued) && throwMode != ThrowMode.Press))
                {
                    p.recoil = 0f;
                    p.missed = false;
                    EndClip(p);
                    IPunchSensor sensor = GetSensor(hand);
                    if (sensor != null) { sensor.Armed = false; sensor.PowerScale = 1f; }
                    if (isPressed && throwMode != ThrowMode.Press) BeginControl(hand, stick);
                    else if (queued && throwMode != ThrowMode.Press) { p.queuedAt = -10f; RequestPunch(new PunchCommand { hand = hand, overdrive = 0.15f, stick = p.queuedStick, time = Time.time }); }   // the buffered tap fires with ITS OWN stick, not today's
                    else { p.phase = Phase.Guard; p.time = 0f; fistValid[i] = false; }
                }
                break;
            }
        }
    }

    private void BeginControl(Hand hand, Vector2 stick)
    {
        PunchState p = punches[(int)hand];
        p.phase = Phase.Control;
        p.time = 0f;
        p.heldSince = Time.time;
        p.charge = 0f;
        p.loadDepth = 0f;
        p.loadTime = -10f;
        p.hasLoadStick = false;
        p.loadStick = stick;
        p.pressBody = stick;
        loadOffset[(int)hand] = Vector3.zero;
        loadOffsetVel[(int)hand] = Vector3.zero;
        p.landed = false;
        p.missed = false;
        p.landedBag = null;
        p.recoil = 0f;
        p.appliedCorrection = Vector3.zero;
        p.recoverAnchored = false;
        p.coilBlend = 0f;   // seeded on the first Control tick: full depth after a retake, held depth fresh
        p.queuedAt = -10f;
        p.chargeNotified = false;
        p.overcooked = false;
        // The coil starts from wherever the stick IS — smoothing glides only the changes made during the hold.
        StickBlend(ShapeStick(p, stick), p.side, out float pressHook, out float pressUpper, out float pressStraight);
        coilShape[(int)hand] = new Vector4(pressStraight, pressHook, pressUpper, HoldHeight(stick, p.side));
        coilShapeVelocity[(int)hand] = Vector4.zero;
        p.side = hand == Hand.Right ? 1 : -1;
        p.type = Classify(stick, p.side);
        p.profile = ProfileFor(hand, p.type);
        p.height01 = HoldHeight(stick, p.side);
        p.clipStateHash = 0;
        p.clipBorrowed = false;
        timelines[(int)hand].Frozen = false;
        SelectClip(hand, p);   // animation-first: the clip's OWN wind-up is the hold, and it flows straight into the strike

        // LOCK THE POSE VARIANTS BEFORE THE COIL, not at the release. The chamber is derived from the
        // strike pose, so swapping the strike later moves the shape the body is already coiling into and
        // the hand jumps on that frame. All three categories are rolled because the hold can still promote
        // a straight into a hook or an uppercut, and whichever it lands on must already be settled.
        if (poseMixer != null)
        {
            poseMixer.RollVariants((int)hand, ReferencePose.Category.Straight);
            poseMixer.RollVariants((int)hand, ReferencePose.Category.Hook);
            poseMixer.RollVariants((int)hand, ReferencePose.Category.Uppercut);
            p.variantsRolled = true;
        }
    }

    /// <summary>Fire the punch: the virtual fist accelerates at the target. Overdrive 0..1 = how well it was loaded and flicked.</summary>
    /// <summary>
    /// SEAM 2 — the REQUESTED ACTION. Every punch enters the simulation through this one gate: the command is
    /// logged (ring of 64) and announced, then executed. Input handling BUILDS commands; this runs them — the
    /// exact joint where a future network layer injects remote or predicted punches.
    /// </summary>
    public void RequestPunch(in PunchCommand cmd)
    {
        commandLog.Enqueue(cmd);
        while (commandLog.Count > 64) commandLog.Dequeue();
        PunchRequested?.Invoke(cmd);
        BeginDrive(cmd.hand, cmd.overdrive, cmd.stick);
    }

    public void BeginDrive(Hand hand, float overdrive, Vector2 stick)
    {
        int i = (int)hand;
        PunchState p = punches[i];
        Transform shoulder = shoulders[i] != null ? shoulders[i] : chest;

        bool inCombo = Time.time - lastThrowTime <= comboWindow;
        comboCount = inCombo ? Mathf.Min(comboCount + 1, 3) : 0;
        float flow = inCombo ? (lastHand != hand ? alternatingPower : sameHandPower) : 1f;
        lastThrowTime = Time.time;
        lastHand = hand;

        // ON THE TICK: the full-charge buzz is a beat you can play. Release inside the window around it and
        // the punch is SNAPPED — a bonus earned by timing, never by holding longer.
        float snap01 = 0f;
        if (p.phase == Phase.Control && throwMode == ThrowMode.Release && input.IsPlayer)
        {
            float tickAt = p.heldSince + maxChargeTime;
            snap01 = 1f - Mathf.Clamp01(Mathf.Abs(Time.time - tickAt) / snapWindow);
        }
        p.overdrive = Mathf.Min(1f, Mathf.Clamp01(overdrive) + snapOverdriveBonus * snap01);
        p.side = hand == Hand.Right ? 1 : -1;
        p.type = Classify(stick, p.side);
        p.profile = ProfileFor(hand, p.type);

        // DOUBLE UP: the same punch tapped again inside the window repeats ITS OWN line — deliberate sameness,
        // the one thing the anti-repeat engine must never produce. Rat-tat.
        bool doubled = enableDoubleUp && Time.time - p.prevThrowTime < doubleUpWindow && p.type == p.prevType
                       && (stick - p.prevStick).magnitude < doubleUpStickMatch;
        StickBlend(stick, p.side, out p.stickHook, out p.stickUpper, out p.stickStraight);
        // The pose must MATCH the thrown type: if range or aim promoted the punch, promote its shape too —
        // a hook always wears a hook reference, never a leftover straight blend.
        if (p.type == PunchType.Hook && p.stickHook < 0.7f) p.stickHook = 0.85f;
        else if (p.type == PunchType.Uppercut && p.stickUpper < 0.7f) p.stickUpper = 0.85f;
        else if ((p.type == PunchType.Straight || p.type == PunchType.Body) && p.stickStraight < 0.6f) p.stickStraight = 0.8f;
        float blendSum = p.stickHook + p.stickUpper + p.stickStraight;
        if (blendSum > 1f) { p.stickHook /= blendSum; p.stickUpper /= blendSum; p.stickStraight /= blendSum; }
        // Only roll here for punches that never had a Control phase to roll in (Press mode, AI, buffered
        // taps). Re-rolling a held punch at the release would move the chamber it has already coiled into.
        if (!doubled && !p.variantsRolled)
            poseMixer?.RollVariants(i, p.type == PunchType.Hook ? ReferencePose.Category.Hook
                                     : p.type == PunchType.Uppercut ? ReferencePose.Category.Uppercut
                                     : ReferencePose.Category.Straight);    // extra bakes of a slot become variants (a double replays its own)
        p.variantsRolled = false;   // consumed — the next punch rolls its own

        // THE VARIATION ENGINE: every throw rolls its own style — clock speed, coil depth, follow-through, aim
        // height, fist roll, arc width, and the derived flavours (overhand, shovel hook, stiff jab) that reshape
        // the trajectory into punches the pose library never authored. Rolled BEFORE the direction is computed,
        // so a flavour's pitch genuinely changes where the punch goes.
        ThrowStyle style = ThrowStyle.Default;
        float plannedHeight = HoldHeight(stick, p.side);
        if (variationEngine != null)
        {
            // The style knows the CONTEXT it was thrown from: a hook off a back-dash mints the check hook, a
            // charged high straight the corkscrew, a cashed evade the counter — techniques, not dice.
            bool dashedBack = locomotion != null && Time.time - locomotion.LastDashTime < 0.35f
                              && Vector3.Dot(locomotion.MoveDirection, transform.forward) < -0.5f;
            bool countered = Time.time - lastEvadeTime < counterWindow;
            if (countered) lastEvadeTime = -10f;   // one evade pays one counter

            style = doubled ? variationEngine.RollDoubled(i)
                            : variationEngine.Roll(hand, p.type, comboCount, plannedHeight, p.overdrive, dashedBack, countered);
            plannedHeight = doubled ? p.prevHeight01 : Mathf.Clamp01(plannedHeight + style.heightJitter);
            poseMixer?.SetThrowJitter(i, style.coilJitter - snapCoilTighten * snap01, style.followJitter, 0f);   // height applied here, once
        }
        p.arcScale = style.arcScale;
        p.rollExtra = style.rollDegrees * p.side;
        p.flavor = style.flavor;
        if (snap01 > 0.5f) Rumble(0.05f, 0.9f, 0.045f);   // the confirming double-tick: you hit the beat

        p.speed = (1f + comboSpeedUp * comboCount) * (IsGassed ? gassedSpeed : 1f) * p.profile.speed * style.speedMul
                * (1f + snapSpeedBonus * snap01) * (p.overcooked ? overcookSpeed : 1f)
                * (Time.time < staggerUntil ? 0.88f : 1f);   // still ringing from a near-knockdown
        p.hitStopLeft = 0f;
        p.landed = false;
        p.missed = false;
        p.landedBag = null;
        p.recoil = 0f;
        p.appliedCorrection = Vector3.zero;   // every punch launches as pure authored pose; the aim glides in
        p.recoverAnchored = false;

        // Straights fly PARALLEL to the facing: each hand aims at its own side of the bag's centreline (its
        // shoulder's width), so a jab or cross never angles across the body — dead straight, and still on the
        // bag. "Still on the bag" is now enforced: the sideways offset is clamped to well inside the bag's
        // radius, so a wide-shouldered character can never be sent punching past the leather.
        float sideOffset = Vector3.Dot(shoulder.position - transform.position, transform.right) * 0.8f;
        if (AssistTarget != null && AssistTarget.BagCollider != null)
        {
            Bounds bagBounds = AssistTarget.BagCollider.bounds;
            float bagRadius = Mathf.Max(0.05f, Mathf.Max(bagBounds.extents.x, bagBounds.extents.z));
            sideOffset = Mathf.Clamp(sideOffset, -bagRadius * 0.55f, bagRadius * 0.55f);
        }
        Vector3 handAim = AimPoint + transform.right * sideOffset;
        p.direction = PunchDirection(p.type, shoulder.position, handAim, out float distance);
        p.right = Vector3.Cross(Vector3.up, p.direction).normalized;
        if (p.right.sqrMagnitude < 0.001f) p.right = transform.right;
        if (Mathf.Abs(style.pitchBias) > 0.01f)
        {
            // A flavour bends the launch line itself: an overhand pitches DOWN over the guard, a shovel digs up.
            p.direction = (Quaternion.AngleAxis(-style.pitchBias, p.right) * p.direction).normalized;
        }
        float depth = impactDepth * Mathf.Lerp(0.7f, 1.4f, p.overdrive);
        p.plantDepth = depth;   // the push-out allowance must match what THIS punch actually sinks
        p.target = ClampToReach(shoulder.position + p.direction * (distance + depth - FistReach), shoulder.position, i, 0.25f);
        p.height01 = plannedHeight;

        if (useBalance && balance != null)
        {
            // ACCURACY lives in the legs — but only when the legs are genuinely gone. At healthy balance
            // (≥ 0.85, the normal standing state) there is ZERO scatter: a planted punch goes exactly where
            // it is aimed. The wobble ramps in only as the balance actually degrades, and stays capped.
            float unsteady = Mathf.InverseLerp(0.85f, 0.25f, balance.Balance01);
            float wobble = Mathf.Min(0.08f, unsteady * balanceAccuracyScatter) * CharacterScale;
            if (wobble > 0.001f)
            {
                Vector2 r = Rng.InsideUnitCircle() * wobble;
                p.target += p.right * r.x + Vector3.up * r.y;
            }

            // STUMBLE: throwing hard from a badly unbalanced position can cost the shot — never a scripted
            // fall, just the body paying for the moment, with odds that scale with how bad it is.
            if (balance.Balance01 < stumbleBelow
                && (p.overdrive > 0.5f || Mathf.Abs(p.direction.y) > 0.55f)
                && Rng.Value01() < (stumbleBelow - balance.Balance01) * 2f)
            {
                p.speed *= 0.82f;
                p.target += (Vector3.down * 0.04f + transform.right * Rng.Range(-0.05f, 0.05f)) * CharacterScale;
                impactLean += new Vector2(Rng.Range(-2.5f, 2.5f), -3.5f);
                balance.AddDisturbance(0.25f);
                Rumble(0.5f, 0.1f, 0.12f);
            }
        }
        if (!fistValid[i])
        {
            IKEffector effector = EffectorFor(hand);
            SetFist(i, effector.bone != null ? effector.bone.position : shoulder.position);
        }
        p.driveDistance = Mathf.Max(0.05f, Vector3.Distance(fistPos[i], p.target));
        // Where the straight line starts. Taken from the posed (chambered) hand, so a straight leaves from the
        // chamber and goes to the target without detouring.
        IKEffector launchEffector = EffectorFor(hand);
        p.launchPos = launchEffector.bone != null ? launchEffector.bone.position : fistPos[i];
        // The launch energy now comes from the timeline's bell rate, not an injected velocity: the punch
        // continues from wherever the chamber clock stands, so a release is one unbroken motion.

        // Footwork bonus from REAL motion — velocity toward the target or a fresh dash-step — not just held input.
        float footwork = 1f;
        if (locomotion != null)
        {
            Vector3 toTarget = Vector3.ProjectOnPlane(p.direction, Vector3.up).normalized;
            float closing = Vector3.Dot(locomotion.MoveDirection * locomotion.CurrentSpeed, toTarget);
            bool dashed = Time.time - locomotion.LastDashTime < 0.35f;
            footwork = closing > 0.8f || dashed ? steppingInPower : (closing < -0.8f ? retreatingPower : 1f);
        }

        SpendStamina(p.profile.stamina * (0.6f + 0.4f * p.overdrive) + overdriveStaminaCost * p.overdrive);
        float staminaPower = IsGassed ? gassedPower : 1f;

        // READABILITY: each punch type declares how much visible LOADING earns full power (Anticipation, in the
        // Inspector). Releasing early always fires instantly — it just carries smoothly less; a buffered tap
        // throws at ~70%. Fast punches read fast, heavy punches have to LOOK loaded to hit heavy.
        float loadFactor = Mathf.Lerp(0.65f, 1f, p.phase == Phase.Control
            ? Mathf.Clamp01((Time.time - p.heldSince) / Mathf.Max(0.01f, p.profile.anticipation))
            : 0.7f);

        // ANTI-SPAM IS ECONOMICS: every throw spends balance as well as breath, and pressing forward while
        // swinging drains both faster — power, accuracy and recovery all ride the balance down. No cooldowns.
        if (useBalance && balance != null)
        {
            bool pressingForward = locomotion != null && locomotion.CurrentSpeed > 0.5f
                                   && Vector3.Dot(locomotion.MoveDirection, transform.forward) > 0.5f;
            balance.NotifyPunch((0.05f + 0.07f * p.overdrive) * (pressingForward ? 1.8f : 1f), p.direction);
            if (pressingForward) SpendStamina(2f);
        }

        bool fromHold = p.phase == Phase.Control;
        p.phase = Phase.Drive;
        p.time = 0f;
        p.xAtRecover = 0f;
        // The side the punch was LOADED at is the side it flies with (smoothed while held); a buffered tap
        // carries its own stick snapshot instead.
        p.aimLateral = doubled ? p.prevAimLateral : (fromHold ? coilLateral[i] : Mathf.Clamp(stick.x, -1f, 1f));
        timelines[i].Frozen = false;

        SelectClip(hand, p);

        // Technique multiplier on the effective mass: what is behind the fist, not the input itself.
        p.powerScale = p.profile.power * (hand == rearHand ? rearHandPower : 1f)
                     * Mathf.Lerp(1f, overdrivePower, p.overdrive) * footwork * flow * staminaPower
                     * Mathf.Max(0.01f, style.powerMul) * loadFactor;
        IPunchSensor sensor = GetSensor(hand);
        if (sensor != null)
        {
            sensor.PowerScale = p.powerScale;
            sensor.Armed = true;
        }

        // What this throw WAS, for the double-up window of the next one.
        p.prevThrowTime = Time.time;
        p.prevType = p.type;
        p.prevStick = stick;
        p.prevHeight01 = p.height01;
        p.prevAimLateral = p.aimLateral;

        PunchThrown?.Invoke(hand, p.type, p.overdrive);
    }

    private void CancelPunch(int i)
    {
        PunchState p = punches[i];
        p.phase = Phase.Guard;
        p.time = 0f;
        p.landed = false;
        p.landedBag = null;
        p.recoil = 0f;
        p.appliedCorrection = Vector3.zero;
        p.recoverAnchored = false;
        fistValid[i] = false;
        timelines[i].Reset();
        EndClip(p);
        IPunchSensor sensor = GetSensor((Hand)i);
        if (sensor != null) { sensor.Armed = false; sensor.PowerScale = 1f; }
    }

    /// <summary>
    /// Continuous punch-type mix from the stick, PER HAND: stick OUTWARD (right for the right hand, left for the
    /// left) bends into a hook, stick DOWN coils an uppercut, between them mixes both smoothly; small deflections
    /// only steer the aim. This drives the pose preview during the hold, the pose blend and the fist's actual arc.
    /// </summary>
    /// <summary>
    /// The stick as the PUNCH SHAPE sees it. A gamepad is used as-is: it is spring-centred, so where you are
    /// holding it is what you mean. A mouse is not — the pointer lives on the target, so its absolute X is
    /// always ~0 and every punch came out a straight no matter what you did. On mouse the shape is therefore
    /// taken from how far the pointer has MOVED since you pressed: flick outward for a hook, inward for an
    /// uppercut, hold still for a straight. Height stays absolute on both, because pointing up at a head is
    /// exactly what you would expect to do.
    /// </summary>
    private Vector2 ShapeStick(PunchState p, Vector2 raw)
    {
        if (!input.IsPlayer || BoxerInput.GamepadActive) return raw;
        float x = Mathf.Clamp((raw.x - p.pressBody.x) * mouseShapeGain, -1f, 1f);
        return new Vector2(x, raw.y);
    }

    /// <summary>Deflection before the stick starts bending a straight into a hook or an uppercut.</summary>
    private const float ShapeDeadZone = 0.3f;

    /// <summary>
    /// X is the SHAPE axis, Y is the HEIGHT axis, and they are INDEPENDENT. That is what makes all nine authored
    /// poses per hand reachable: push OUT (toward this hand's own side) and the punch bends into a hook, push IN
    /// across the body and it comes up as an uppercut — which is where an uppercut travels anyway — and centre
    /// throws it straight, while up/down picks head, body or low for any of the three.
    ///
    /// It used to take the type from the same deflection that set the height, so a hook was ALWAYS a body hook
    /// and an uppercut was ALWAYS to the chin. LeftHookHead, LeftHookLow, UppercutBody, UppercutLow and the rest
    /// could not be selected by any stick position at all — which is why most of the library never played.
    /// </summary>
    private static void StickBlend(Vector2 stick, int side, out float hook, out float uppercut, out float straight)
    {
        float outward = Mathf.Clamp01(stick.x * side);          // toward this hand's own side → hook
        float inward = Mathf.Clamp01(-stick.x * side);          // across the body → uppercut

        hook = Mathf.Clamp01((outward - ShapeDeadZone) / (1f - ShapeDeadZone));
        uppercut = Mathf.Clamp01((inward - ShapeDeadZone) / (1f - ShapeDeadZone));
        straight = Mathf.Clamp01(1f - hook - uppercut);
    }

    /// <summary>The dominant punch type for the current stick (profiles / trajectory base); the mix stays continuous.</summary>
    private PunchType Classify(Vector2 stick, int side)
    {
        StickBlend(stick, side, out float hook, out float uppercut, out _);
        if (uppercut > hook && uppercut > 0.35f) return PunchType.Uppercut;
        if (hook > 0.35f) return PunchType.Hook;
        if (AimPoint.y < chest.position.y - bodyShotBelowChest) return PunchType.Body;
        return PunchType.Straight;   // neutral stick is ALWAYS a clean straight at the target — for both hands
    }

    /// <summary>
    /// Aim height straight from the stick: up = face, centre = body, low-ish = lower body — with an uppercut
    /// hunting the chin the deeper it coils. Predictable, and it makes every authored height variant reachable.
    /// </summary>
    private float HoldHeight(Vector2 stick, int side)
    {
        // Straight up = head, centre = body, straight down = low — for every punch type, independent of shape.
        // Full range at ~0.9 deflection so the extremes are reachable without having to find a stick corner.
        return Mathf.Clamp01(0.5f + stick.y * 0.55f);
    }

    private bool HasUppercutPose()
    {
        if (poseMixer == null) return false;
        return poseMixer.HasType(1, ReferencePose.Category.Uppercut) || poseMixer.HasType(0, ReferencePose.Category.Uppercut);
    }

    /// <summary>Aim height mapped onto the boxer's own body: 0 = hip height (low), 0.5 = torso, 1 = head.</summary>
    private float AimHeight01(float worldY)
    {
        float low = hipsBone != null ? hipsBone.position.y : transform.position.y + 0.9f;
        float high = head != null ? head.position.y : low + 0.7f;
        return Mathf.Clamp01(Mathf.InverseLerp(low + 0.05f, high, worldY));
    }

    private PunchProfile ProfileFor(Hand hand, PunchType type)
    {
        switch (type)
        {
            case PunchType.Hook:     return hook;
            case PunchType.Body:     return body;
            case PunchType.Uppercut: return uppercut;
            default:                 return hand == rearHand ? straightRear : straightLead;
        }
    }

    private Vector3 PunchDirection(PunchType type, Vector3 shoulder, Vector3 aimPoint, out float distance)
    {
        Vector3 v = aimPoint - shoulder;
        Vector3 flat = Vector3.ProjectOnPlane(v, Vector3.up);
        float horizontal = flat.magnitude;
        if (horizontal < 0.05f) { flat = Vector3.ProjectOnPlane(transform.forward, Vector3.up); horizontal = 0.05f; }
        flat /= flat.magnitude;

        float pitch = Mathf.Atan2(v.y, horizontal) * Mathf.Rad2Deg;
        switch (type)
        {
            case PunchType.Body:     pitch = Mathf.Clamp(pitch, bodyPitchRange.x, bodyPitchRange.y); break;
            case PunchType.Uppercut: pitch = Mathf.Clamp(pitch, uppercutPitchRange.x, uppercutPitchRange.y); break;
            default:                 pitch = Mathf.Clamp(pitch, -straightPitchLimit, straightPitchLimit); break;
        }

        float rad = pitch * Mathf.Deg2Rad;
        Vector3 direction = flat * Mathf.Cos(rad) + Vector3.up * Mathf.Sin(rad);
        distance = horizontal / Mathf.Max(0.2f, Mathf.Cos(rad));
        return direction;
    }

    /// <summary>
    /// Where the fist should point: the hand's MEASURED finger axis turned onto the punch line by the smallest rotation,
    /// keeping the roll the animation / reference pose gave the hand. (Building the rotation from LookRotation assumed
    /// the hand bone's +Z was the fingers; on Mixamo rigs the fingers are +Y, which pointed every glove at the ceiling.)
    /// </summary>
    private Quaternion FistRotation(PunchState p, int hand, Quaternion currentHandRotation)
    {
        Vector3 fingersNow = currentHandRotation * fingerAxisLocal[hand];
        Quaternion correction = Quaternion.FromToRotation(fingersNow, p.direction);
        correction = Quaternion.RotateTowards(Quaternion.identity, correction, wristLimit);   // aim the fist, never break the wrist
        Quaternion rotation = correction * currentHandRotation;
        switch (p.type)
        {
            case PunchType.Hook:     rotation = Quaternion.AngleAxis(hookExtraRoll * p.side, p.direction) * rotation; break;
            case PunchType.Uppercut: rotation = Quaternion.AngleAxis(uppercutExtraPitch, p.right) * rotation; break;
        }
        // Per-throw roll from the variation engine — an overhand corkscrews, and no two fists land identically.
        if (Mathf.Abs(p.rollExtra) > 0.01f) rotation = Quaternion.AngleAxis(p.rollExtra, p.direction) * rotation;
        return rotation * Quaternion.Euler(fistEuler);
    }

    /// <summary>Does this hand have a FULL animation clip authored for the punch type?</summary>
    private bool HasFullClip(int hand, PunchType type)
    {
        AnimationClip[] clips = (hand == 0 ? leftClips : rightClips).For(type);
        if (clips == null) return false;
        foreach (AnimationClip c in clips) if (c != null) return true;
        return false;
    }

    /// <summary>
    /// ONE CLOCK: the full clip is no longer fired-and-forgotten — it is SCRUBBED by the punch timeline (the state
    /// is held at speed 0 and its normalized time set every frame), so the clip's strike frame lands EXACTLY when
    /// x reaches 1, hit-stop freezes it with the fist, and recovery plays it back through the chamber.
    /// </summary>
    private void SelectClip(Hand hand, PunchState p, bool force = false)
    {
        // Pose-driven punches own the whole upper body. A clip playing underneath would be a second animation
        // fighting the chain for the same muscles — the base layer's stance and footwork still play.
        if (punchShape != PunchShape.Clips || upperLayerIndex < 0) { p.clipStateHash = 0; return; }
        if (p.clipStateHash != 0 && p.clipType == p.type && !force) return;   // already holding the right clip

        // Dual-hand flurries: the single animation layer plays ONE clip at a time — the second simultaneous punch
        // rides IK alone instead of snapping the layer mid-swing.
        if (p.clipStateHash == 0 && Time.time - lastClipTime < 0.15f) { p.clipStateHash = 0; return; }

        // Animation-first with graceful fallback: use this type's clips; if the type has none authored yet, borrow
        // the nearest type from the SAME hand (and let the IK correct it harder) so a punch is never shapeless.
        PunchType chosen = p.type;
        AnimationClip[] clips = ClipsFor(hand, chosen);
        if (clips == null)
        {
            foreach (PunchType alt in NearestTypes(p.type))
            {
                clips = ClipsFor(hand, alt);
                if (clips != null) { chosen = alt; break; }
            }
        }
        if (clips == null) { p.clipStateHash = 0; return; }

        p.clipBorrowed = chosen != p.type;
        p.clipType = p.type;
        lastClipTime = Time.time;

        // Clips are ordered high → low (Head, UpperBody, LowerBody): pick the one matching the AIM HEIGHT, with a
        // little jitter between neighbours so repeated punches to the same spot still vary.
        int index = clips.Length == 1 ? 0
            : Mathf.Clamp(Mathf.RoundToInt((1f - p.height01) * (clips.Length - 1) + Rng.Range(-0.35f, 0.35f)), 0, clips.Length - 1);
        while (index > 0 && clips[index] == null) index--;
        if (clips[index] == null) { p.clipStateHash = 0; return; }

        string prefix = hand == Hand.Left ? "L" : "R";
        p.clipStateHash = Animator.StringToHash($"{prefix} {chosen} {index}");

        // THIS clip's own strike frame, measured, with the global as the fallback. One shared
        // value cannot serve a library whose real strike times run 0.15 to 0.73 — the fist
        // would arrive before or after the trajectory says it did, on most of them.
        float measured = clipStrikeTimes != null ? clipStrikeTimes.StrikeOf(clips[index]) : -1f;
        p.clipStrike = measured > 0f ? measured : clipStrikeTime;

        if (animatorParameters.Contains("PunchSpeed")) animator.SetFloat(PunchSpeedHash, 0f);   // scrubbed, never self-advancing
    }

    /// <summary>
    /// ONE CLOCK, applied before the Animator evaluates: every live punch scrubs its clip to the timeline —
    /// wind-up while held, strike frame exactly at x = 1, the clip's own follow-through on the way home.
    /// </summary>
    private void UpdateClips()
    {
        if (upperLayerIndex < 0) return;
        for (int i = 0; i < 2; i++)
        {
            PunchState p = punches[i];
            if (p.clipStateHash == 0 || p.phase < Phase.Control) continue;
            animator.Play(p.clipStateHash, upperLayerIndex, Mathf.Clamp01(ClipPhase(p, i)));
        }
    }

    /// <summary>The clips authored for a hand and type, or null when that slot is empty.</summary>
    private AnimationClip[] ClipsFor(Hand hand, PunchType type)
    {
        AnimationClip[] clips = (hand == Hand.Left ? leftClips : rightClips).For(type);
        if (clips == null || clips.Length == 0) return null;
        foreach (AnimationClip c in clips) if (c != null) return clips;
        return null;
    }

    /// <summary>
    /// Which punch types may stand in for this one when it has no clips yet. ONLY same-shape types substitute
    /// (a straight and a body shot are the same punch at different heights) — a hook clip must never play on a
    /// jab. A type with nothing to borrow simply runs on IK until its animation is authored.
    /// </summary>
    private static PunchType[] NearestTypes(PunchType type)
    {
        switch (type)
        {
            case PunchType.Body:     return new[] { PunchType.Straight };
            case PunchType.Straight: return new[] { PunchType.Body };
            default:                 return System.Array.Empty<PunchType>();
        }
    }

    /// <summary>Tell the user exactly which punches have animation and which are still IK-only, with the names to export.</summary>
    private void ReportClipCoverage()
    {
        List<string> animated = new List<string>();
        List<string> missing = new List<string>();
        foreach (PunchType type in new[] { PunchType.Straight, PunchType.Hook, PunchType.Uppercut, PunchType.Body })
            for (int h = 0; h < 2; h++)
            {
                string name = (h == 0 ? "Left" : "Right") + type;
                AnimationClip[] clips = ClipsFor((Hand)h, type);
                if (clips != null) animated.Add($"{name} ({clips.Length})"); else missing.Add(name);
            }

        string message = $"Boxer ANIMATION-FIRST: poses are off; the clips own the punches.\n  animated: {string.Join(", ", animated)}";
        if (missing.Count > 0)
            message += $"\n  IK-only (no clips yet): {string.Join(", ", missing)}" +
                       "\n  To animate them, export e.g. RightUppercutHeadFull / RightUppercutUpperBodyFull / RightUppercutLowerBodyFull" +
                       " into Assets/Animations, then Tools ▸ Boxer ▸ Auto-Assign Clips By Name.";
        Debug.Log(message, this);
    }

    /// <summary>
    /// Where the scrubbed clip should sit for this punch: the wind-up while held, the strike frame exactly at x = 1,
    /// then the clip's OWN authored follow-through and return during recovery — never a rewind.
    /// </summary>
    private float ClipPhase(PunchState p, int i)
    {
        // Per clip, not one value for the whole library (see PunchClipStrikeTimes).
        float strike = p.clipStrike > 0f ? p.clipStrike : clipStrikeTime;

        if (p.phase == Phase.Recover)
        {
            float t = Mathf.Clamp01(p.time / RecoverDuration(p));
            return Mathf.Lerp(strike, 1f, t);
        }
        return strike * Mathf.Clamp01(timelines[i].X);
    }

    /// <summary>Release the scrubbed clip state back to the layer's guard.</summary>
    private void EndClip(PunchState p)
    {
        if (p.clipStateHash == 0) return;
        p.clipStateHash = 0;
        if (upperLayerIndex >= 0) animator.CrossFadeInFixedTime(GuardStateHash, 0.18f, upperLayerIndex);
    }

    private float RecoverDuration(PunchState p)
    {
        // Getting the hand home takes longer when the legs are gone — the spammer's real cooldown.
        float balancePenalty = useBalance && balance != null
            ? Mathf.Lerp(1f + balanceRecoverPenalty, 1f, balance.Balance01) : 1f;
        return p.profile.recover * p.recoverScale * balancePenalty / Mathf.Max(0.01f, p.speed);
    }

    /// <summary>Contact → sink into the bag → hit-stop (planted) → the profile's hold, during which the fist recoils.</summary>
    private float ImpactDuration(PunchState p) => p.sinkTime + p.hitStopLeft + p.profile.hold / Mathf.Max(0.01f, p.speed) + Mathf.Min(0.06f, p.recoil * 0.5f);

    /// <summary>The glove touched the bag: from here the bag stops the fist, not the controller.</summary>
    private void BeginImpact(PunchState p, int i)
    {
        p.phase = Phase.Impact;
        p.time = 0f;
        // The clock is NOT slammed to 1 here any more — the recorder measured a 21 m/s whole-body pose snap on
        // every landing (a retaken punch could jump x 0.6 → 1.0 in one frame). The Impact phase now sweeps the
        // clock to 1 continuously while the scripted fist sinks in.
        p.impactStart = fistPos[i];
        // A real stop over the remaining depth takes 2·d / v (uniform deceleration) — a couple of frames at punch speed.
        float remaining = Vector3.Distance(p.impactStart, p.target);
        float speed = Mathf.Max(1f, fistVel[i].magnitude);
        p.sinkTime = Mathf.Clamp(2f * remaining / speed, 0.015f, 0.06f);
    }

    /// <summary>
    /// IK mode: land the punch on the exact frame the knuckles cross the bag surface. The trigger sensor only sees the
    /// bag at the physics rate (20 ms — a 9 m/s fist moves 18 cm between steps), which is why the bag used to react a
    /// frame early or late. The trigger stays as a fallback; with the physics body the real collision does this.
    /// </summary>
    /// <summary>
    /// A punch that finished without touching anything, at most once a second: say exactly why. When the bag
    /// makes no sound and shows no effect, this line is the diagnosis — a bag that never built, knuckles
    /// stopping short of the leather, or a sensor that was never armed.
    /// </summary>
    private void LogWhiff(Hand hand, PunchState p, int i)
    {
        if (Time.time - lastWhiffLog < 1f) return;
        lastWhiffLog = Time.time;

        Vector3 knuckles = fistPos[i] + p.direction * FistReach;
        PunchingBag nearest = null;
        float best = float.NegativeInfinity;
        foreach (PunchingBag bag in bags)
        {
            if (bag == null) continue;
            if (!bag.IsBuilt)
            {
                Debug.LogWarning($"Punch whiffed: bag '{bag.name}' is NOT BUILT (no Rigidbody yet) — it cannot " +
                                 "be hit, react, or make a sound. Check the bag's own console errors.", bag);
                continue;
            }
            float depth = bag.Penetration(knuckles, out _, out _);
            if (depth > best) { best = depth; nearest = bag; }
        }
        if (nearest == null) return;

        IPunchSensor sensor = GetSensor(hand);
        Debug.Log($"Punch whiffed ({hand} {p.type}): knuckles finished {-best:0.00} m outside '{nearest.name}', " +
                  $"armed={sensor != null && sensor.Armed}, fist speed {fistVel[i].magnitude:0.0} m/s. " +
                  "Repeated big distances = an aim problem; negative-ish = a landing/sensor problem.", this);
    }

    private void TryLandOnSurface(Hand hand, PunchState p, int i)
    {
        if (physicsBody != null || hitboxes == null || !fistValid[i]) return;
        PunchHitbox sensor = hand == Hand.Left ? hitboxes.LeftHand : hitboxes.RightHand;
        if (sensor == null) return;

        Vector3 knuckles = fistPos[i] + p.direction * FistReach;

        foreach (PunchingBag bag in bags)
        {
            if (bag == null || !bag.IsBuilt) continue;
            if (bag.Penetration(knuckles, out Vector3 surface, out _) < 0f) continue;
            if (sensor.TryHit(bag, surface)) return;
        }

        // …and the same frame-exact test against another fighter's hit zones, so a fast clean shot cannot slip
        // between two physics steps and pass straight through his head.
        int found = Physics.OverlapSphereNonAlloc(knuckles, FistReach * 0.9f, zoneHits, ~0, QueryTriggerInteraction.Collide);
        for (int k = 0; k < found; k++)
        {
            BoxerHitZone zone = zoneHits[k] != null ? zoneHits[k].GetComponentInParent<BoxerHitZone>() : null;
            if (zone == null || zone.Health == null || zone.Health.gameObject == gameObject) continue;
            if (sensor.TryHitBoxer(zone, knuckles)) { p.landed = true; return; }
        }
    }

    /// <summary>IK mode: while the fist sits in the bag, keep the mesh dented under the knuckles (the physical glove does this itself).</summary>
    private void PressIntoBag(PunchState p, int hand)
    {
        if (physicsBody != null || !p.landed || p.landedBag == null || !p.landedBag.IsBuilt) return;
        Vector3 knuckles = fistPos[hand] + p.direction * FistReach;
        float depth = p.landedBag.Penetration(knuckles, out Vector3 surface, out Vector3 inward);
        if (depth > 0f) p.landedBag.SetContact(hand, surface, inward, depth + 0.01f);
        else p.landedBag.ClearContact(hand);
    }

    private void OnPunchLanded(Hand hand, PunchingBag bag, PunchingBag.HitInfo info)
    {
        PunchState p = punches[(int)hand];
        if (p.phase == Phase.Drive || p.phase == Phase.Impact)
        {
            p.hitStopLeft = hitStop * Mathf.Lerp(0.5f, 1.1f, info.cleanliness) * Mathf.Lerp(0.6f, 1.35f, info.strength);   // hard hits plant longer
            p.landed = true;
            p.landedBag = bag;
            p.contactPoint = info.point;
            // PHYSICAL REBOUND: the fist reflects off the bag with a restitution share set by the bag's REAL
            // effective mass at the contact — a 10 m/s cross off the heavy middle kicks back ~3 m/s, a jab or
            // a shot on the light bottom edge barely returns. A fixed 4 cm sine bounced them all the same.
            float bagMass = bag != null && bag.IsBuilt ? bag.PointEffectiveMass(info.point, info.direction) : 40f;
            float share = bagMass / (bagMass + 6f);   // ~6 kg of fist + forearm behind the glove
            p.reboundSpeed = reboundRestitution * Mathf.Max(0f, info.speed) * share;
            p.recoil = Mathf.Clamp(Mathf.Max(p.reboundSpeed * 0.06f, recoilAmount * 0.5f), 0.015f, 0.12f);

            // The body takes the shot too: the reaction runs up the arm into the shoulder and torso — SQUARED,
            // so a tap whispers and only a real punch rocks the silhouette.
            bodyShock = Mathf.Max(bodyShock, Mathf.Lerp(0.15f, 1f, info.strength * info.strength));
            bodyShockVelocity = Mathf.Max(bodyShockVelocity, 8f * info.strength);   // sharp attack into the spring
            shockDirection = info.direction;
            shockSide = p.side;

            if (info.impulse < 1f)
                Debug.Log($"Punch landed on a bag at its speed ceiling: motion absorbed, feedback floored ({hand}, contact speed {info.speed:0.0} m/s).", this);

            // Meeting the bag's swing pays your lungs — timing over spam.
            if (info.closing > 1.2f && info.cleanliness > 0.6f) Stamina = Mathf.Min(maxStamina, Stamina + 3f);

            LastImpactReport = $"IMPACT {info.impulse:0} N·s   speed {info.speed:0.0} m/s   clean {info.cleanliness:0.00}   " +
                               $"align {AlignmentFactor(hand, p):0.00}   bal {(balance != null ? balance.Balance01 : 1f):0.00}" +
                               $"{(p.flavor != null ? "   [" + p.flavor + "]" : "")}";
        }
        Rumble(0.2f + 0.6f * info.strength, 0.6f * info.strength * info.cleanliness, 0.07f + 0.1f * info.strength);
        PunchLanded?.Invoke(hand, bag, info);
    }

    // ------------------------------------------------------------------ Getting hit by the bag

    /// <summary>Called by <see cref="PunchingBag"/> when it swings into the boxer (a glove, the body, or the head).</summary>
    public void ReceiveBagContact(PunchingBag bag, Vector3 point, Vector3 bagVelocity, float impulse, bool glove, bool head = false)
    {
        if (Time.time - lastContactTime < 0.25f) return;
        lastContactTime = Time.time;

        float speed = bagVelocity.magnitude;
        float strength = Mathf.Clamp01(speed / fullHitSpeed);
        bool blocked = glove || IsBlocking;

        Vector3 direction = Vector3.ProjectOnPlane(bagVelocity, Vector3.up);
        if (direction.sqrMagnitude < 0.001f) direction = Vector3.ProjectOnPlane(chest.position - point, Vector3.up);
        direction = direction.sqrMagnitude > 0.001f ? direction.normalized : -transform.forward;

        // SLIP: leaning off the bag's line turns the hit into a graze — the right-stick defense payoff.
        Vector3 leanWorld = transform.right * (-lean.x / Mathf.Max(1f, leanDegrees)) + transform.forward * (lean.y / Mathf.Max(1f, leanDegrees));
        bool slipped = !blocked && Vector3.Dot(leanWorld, -direction) > 0.45f;

        // PERFECT BLOCK: guard raised inside the window — costs nothing, and the bag bounces off YOU.
        bool perfect = blocked && Time.time - blockStartTime < 0.18f && bag.IsBuilt;

        if (slipped) strength *= 0.25f;
        if (slipped || perfect) lastEvadeTime = Time.time;   // an evade opens the counter window — answer the bag

        float knockback = Mathf.Min(speed * knockbackPerSpeed, maxKnockback) * (blocked ? blockKnockback : (slipped ? 0.2f : 1f));
        if (perfect) knockback = 0f;
        if (locomotion != null && knockback > 0f) locomotion.AddImpulse(direction * knockback);

        // The bag meets the guard and comes off it — through the bag's own capped reflection, never a raw
        // impulse. A perfect block is springy (the bag visibly jumps back); an ordinary block is leather.
        if (perfect) bag.Bounce(point, -direction, 0.85f);
        else if (blocked) bag.Bounce(point, -direction, 0.3f);
        if (!perfect) SpendStamina(speed * (blocked || slipped ? bagHitCost.y : bagHitCost.x));

        // With the physics body the torso is really displaced by the bag, so only a little procedural stagger is added.
        float stagger = staggerLean * strength * (blocked || slipped ? 0.3f : 1f) * (physicsBody != null ? 0.4f : 1f);
        if (useBalance && balance != null)
        {
            balance.AddDisturbance(strength * (blocked || slipped ? 0.15f : 0.45f));
            stagger *= 2f - balance.Balance01;   // an already-shaky boxer is far easier to rock

            // NEAR-KNOCKDOWN: a heavy, unblocked shot into an already-broken base. No forced fall — a huge
            // visible stagger, a beat of weakness, and an event for whatever knockdown system comes later.
            if (!blocked && !slipped && strength > 0.55f && balance.Balance01 < knockdownVulnerability)
            {
                balance.AddDisturbance(0.5f);
                impactLean += new Vector2(Vector3.Dot(direction, transform.right) * staggerLean * 1.5f, -staggerLean * 1.2f);
                staggerUntil = Time.time + 0.9f;
                NearKnockdown?.Invoke(strength);
                Rumble(1f, 0.3f, 0.25f);
            }
        }
        impactLean.y -= stagger;
        impactLean.x -= Vector3.Dot(direction, transform.right) * stagger * 0.6f;

        // A clean hit interrupts whatever you were loading; slips and blocks never do.
        if (!blocked && !slipped && strength > 0.5f)
            for (int i = 0; i < 2; i++)
                if (punches[i].phase == Phase.Control) { punches[i].phase = Phase.Recover; punches[i].time = 0f; punches[i].recoverScale = 0.8f; }
        if (!blocked && !slipped && head && physicsBody != null) physicsBody.NotifyHeadHit(speed);

        if (perfect) Rumble(0.15f, 0.85f, 0.07f);
        else if (slipped) Rumble(0.05f, 0.5f, 0.05f);
        else Rumble(blocked ? 0.25f * strength : 0.9f * strength, blocked ? 0.1f : 0.3f * strength, blocked ? 0.08f : 0.18f);
        BagContact?.Invoke(strength, blocked || slipped);
    }

    /// <summary>
    /// Took a punch from another BOXER (routed here by <see cref="BoxerHealth"/>). Same reaction vocabulary as
    /// the bag swinging into you — the knockback is applied by the health component, this owns the body
    /// language: the stagger, the interrupted wind-up, the stun, and the rumble if a human holds the pad.
    /// </summary>
    public void ReceiveStrike(Vector3 point, Vector3 direction, float speed, float power, bool blocked, bool head)
    {
        // A shorter gate than the bag's: a flurry has to land every shot, not one in four.
        if (Time.time - lastContactTime < 0.06f) return;
        lastContactTime = Time.time;

        float strength = Mathf.Clamp01(power);
        Vector3 flat = Vector3.ProjectOnPlane(direction, Vector3.up);
        flat = flat.sqrMagnitude > 0.001f ? flat.normalized : -transform.forward;

        float stagger = staggerLean * strength * (blocked ? 0.3f : 1f) * (physicsBody != null ? 0.4f : 1f);
        impactLean.y -= stagger;
        impactLean.x -= Vector3.Dot(flat, transform.right) * stagger * 0.6f;

        bodyShock = Mathf.Max(bodyShock, strength);
        shockDirection = flat;

        SpendStamina(strength * (blocked ? bagHitCost.y : bagHitCost.x));

        // A clean shot interrupts whatever you were winding up.
        if (!blocked && strength > 0.4f)
            for (int i = 0; i < 2; i++)
                if (punches[i].phase == Phase.Control)
                {
                    punches[i].phase = Phase.Recover;
                    punches[i].time = 0f;
                    punches[i].recoverScale = 0.8f;
                    punches[i].xAtRecover = timelines[i].X;
                }

        if (!blocked && head && physicsBody != null) physicsBody.NotifyHeadHit(speed);

        Rumble(blocked ? 0.25f * strength : 0.9f * strength,
               blocked ? 0.1f : 0.35f * strength,
               blocked ? 0.08f : 0.18f);
        BagContact?.Invoke(strength, blocked);
    }

    /// <summary>Let another system take stamina out of this boxer (body shots, guard work).</summary>
    public void SpendStaminaExternal(float amount) => SpendStamina(amount);

    // ------------------------------------------------------------------ Stamina

    /// <summary>Haptics belong to the human — an AI landing a punch must never buzz the player's controller.</summary>
    private void Rumble(float low, float high, float seconds)
    {
        if (input.IsPlayer) BoxerInput.Rumble(low, high, seconds);
    }

    private void SpendStamina(float amount)
    {
        if (amount <= 0f) return;
        Stamina = Mathf.Max(0f, Stamina - amount);
        lastStaminaSpend = Time.time;
    }

    private void UpdateStamina(float dt)
    {
        if (input.IsPlayer)
            BoxerInput.SetLightBar(Color.Lerp(new Color(1f, 0.15f, 0.1f), new Color(0.1f, 0.9f, 0.3f), Stamina01));
        physicsBody?.SetStrength(IsGassed ? gassedPower : 1f);

        // Idle energy: the stance idle blends High → Mid → Low as stamina drains (Animator float "Energy", smoothed).
        if (animatorParameters.Contains("Energy"))
        {
            energySmoothed = Mathf.SmoothDamp(energySmoothed, Stamina01, ref energyVelocity, 0.6f);
            animator.SetFloat(EnergyHash, energySmoothed);
        }

        if (Time.time - lastStaminaSpend < regenDelay) return;
        float regen = staminaRegen * (IsBlocking ? 0.5f : 1f);
        Stamina = Mathf.Min(maxStamina, Stamina + regen * dt);
    }

    // ------------------------------------------------------------------ Block

    private void UpdateBlock()
    {
        bool wantGuard = allowBlock && input.Block && !IsKnockedDown;          // L1: the Guard reference pose, gloves solid
        bool wantBodyGuard = allowBlock && input.BodyGuard && !IsKnockedDown; // R1: the body-guard pose (falls back to Guard)
        bool wasBlocking = IsBlocking;
        IsBlocking = wantGuard || wantBodyGuard;
        if (IsBlocking && !wasBlocking) blockStartTime = Time.time;                // the perfect-block window opens here
        IsBodyGuard = wantBodyGuard;
        blockBlend = Mathf.MoveTowards(blockBlend, IsBlocking ? 1f : 0f, Time.deltaTime / 0.12f);
        physicsBody?.SetGuard(IsBlocking);
        if (poseMixer != null) poseMixer.Hold = wantBodyGuard ? 10 : (wantGuard ? 1 : -1);   // L1 = exactly key 1, R1 = the body guard

        if (locomotion != null && !IsKnockedDown)
            locomotion.SpeedMultiplier = IsBlocking ? blockMoveSpeed : (IsGassed ? gassedSpeed : 1f);
    }

    // ------------------------------------------------------------------ Aiming

    private void ScanBags()
    {
        if (Time.time >= nextBagScan)
        {
            bags = FindObjectsByType<PunchingBag>();
            nextBagScan = Time.time + 1f;
        }

        AssistTarget = null;
        AssistOpponent = null;
        AssistDistance = float.PositiveInfinity;
        if (!targetAssist) return;

        // A live opponent outranks any bag — you are not throwing at the leather while someone is hitting you.
        if (Time.time >= nextOpponentScan)
        {
            opponents = FindObjectsByType<BoxerHealth>(FindObjectsSortMode.None);
            nextOpponentScan = Time.time + 1f;
        }
        float bestOpponent = assistRange * 1.6f;
        foreach (BoxerHealth other in opponents)
        {
            if (other == null || other.gameObject == gameObject || other.IsDown) continue;
            Vector3 to = Vector3.ProjectOnPlane(other.transform.position - chest.position, Vector3.up);
            float d = to.magnitude;
            if (d > bestOpponent) continue;
            if (Vector3.Dot(to / Mathf.Max(0.001f, d), transform.forward) < 0.1f) continue;
            bestOpponent = d;
            AssistOpponent = other;
        }
        if (AssistOpponent != null)
        {
            AssistDistance = bestOpponent;
            return;
        }

        float best = assistRange;
        Vector3 origin = chest.position;
        foreach (PunchingBag bag in bags)
        {
            if (bag == null || !bag.IsBuilt || bag.BagCollider == null) continue;
            Vector3 to = Vector3.ProjectOnPlane(bag.BagCollider.bounds.center - origin, Vector3.up);
            float distance = to.magnitude;
            if (distance > best) continue;
            if (Vector3.Dot(to / Mathf.Max(0.001f, distance), transform.forward) < 0.1f) continue;
            best = distance;
            AssistTarget = bag;
        }

        if (AssistTarget != null)
        {
            Vector3 surface = AssistTarget.BagCollider.ClosestPoint(origin);
            AssistDistance = Vector3.ProjectOnPlane(surface - origin, Vector3.up).magnitude;
        }
    }

    private void UpdateAim(float dt)
    {
        Vector3 raw = AssistOpponent != null ? AssistedAimBoxer(AssistOpponent)
                    : AssistTarget != null ? AssistedAim(AssistTarget)
                    : FreeAim();
        Vector3 clamped = ClampAim(raw);
        AimPoint = aimSmoothTime > 0f
            ? Vector3.SmoothDamp(AimPoint, clamped, ref aimVelocity, aimSmoothTime, Mathf.Infinity, dt)
            : clamped;
    }

    private Vector3 AssistedAim(PunchingBag bag)
    {
        Bounds b = bag.BagCollider.bounds;
        float radius = Mathf.Max(b.extents.x, b.extents.z);
        Vector3 forward = Vector3.ProjectOnPlane(b.center - chest.position, Vector3.up);
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : transform.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        // Punches aim AT THE TARGET, always: while a hand is held or flying, the lateral offset is ZERO — dead on
        // the bag's centreline, camera and pointer position irrelevant. Stick X only selects the punch shape;
        // stick Y still picks the height. Only the free look-around aim keeps a lateral offset.
        float lateral = MouseOffset.x;
        float vertical = MouseOffset.y;
        for (int i = 0; i < 2; i++)
        {
            if (punches[i].phase == Phase.Control)
            {
                lateral = 0f;
                vertical = punches[i].hasLoadStick ? punches[i].loadStick.y : input.Body.y;
            }
            else if ((punches[i].phase == Phase.Drive || punches[i].phase == Phase.Impact) && punches[i].hasLoadStick)
            {
                lateral = 0f;
                vertical = punches[i].loadStick.y;
            }
        }

        // Height: level with YOUR shoulders, not the bag's centre (a bag hung high or low must not tilt every punch);
        // the stick moves the aim up or down from there, within the bag.
        float shoulderY = 0.5f * ((shoulders[0] != null ? shoulders[0].position.y : chest.position.y) + (shoulders[1] != null ? shoulders[1].position.y : chest.position.y));
        float aimY = Mathf.Clamp(shoulderY + vertical * assistAimHeight, b.min.y + 0.08f, b.max.y - 0.08f);

        Vector3 desired = new Vector3(b.center.x, aimY, b.center.z)
                        + right * (lateral * radius * assistLateralSpread)
                        - forward * (radius + 0.5f);
        return bag.BagCollider.ClosestPoint(desired);
    }

    /// <summary>
    /// Aim at the OTHER FIGHTER. Height comes from the stick exactly as it does on the bag — up for the head,
    /// centre for the body, down for the ribs — mapped onto his actual head and hips so a body shot lands on
    /// his body whatever his height or stance.
    /// </summary>
    private Vector3 AssistedAimBoxer(BoxerHealth other)
    {
        Animator theirs = other.GetComponent<Animator>();
        Transform head = theirs != null && theirs.isHuman ? theirs.GetBoneTransform(HumanBodyBones.Head) : null;
        Transform hips = theirs != null && theirs.isHuman ? theirs.GetBoneTransform(HumanBodyBones.Hips) : null;

        float high = head != null ? head.position.y : other.transform.position.y + 1.6f;
        float low = hips != null ? hips.position.y : other.transform.position.y + 0.9f;

        float vertical = MouseOffset.y;
        for (int i = 0; i < 2; i++)
        {
            if (punches[i].phase == Phase.Control) vertical = punches[i].hasLoadStick ? punches[i].loadStick.y : input.Body.y;
            else if (punches[i].phase >= Phase.Drive && punches[i].hasLoadStick) vertical = punches[i].loadStick.y;
        }

        float t = Mathf.Clamp01(0.5f + vertical * 0.55f);
        Vector3 aim = other.transform.position;
        aim.y = Mathf.Lerp(low - 0.05f, high, t);
        return aim;
    }

    private Vector3 FreeAim()
    {
        Vector3 fallback = chest.position + transform.forward * aimPlaneDistance;

        if (input.IsPlayer && !BoxerInput.GamepadActive && aimCamera != null)
        {
            Ray ray = aimCamera.ScreenPointToRay(BoxerInput.PointerScreenPosition);

            RaycastHit[] hits = Physics.RaycastAll(ray, 50f, aimMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3 bestPoint = Vector3.zero;
            bool found = false;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.transform.IsChildOf(transform)) continue;
                if (hit.collider.GetComponent<PhysicsPuppetPart>() != null) continue;      // our own puppet
                if (hit.distance < best) { best = hit.distance; bestPoint = hit.point; found = true; }
            }
            if (found) return bestPoint;

            Plane plane = new Plane(-transform.forward, fallback);
            if (plane.Raycast(ray, out float distance) && distance < 50f) return ray.GetPoint(distance);
            return fallback;
        }

        // Gamepad: aim in front of the boxer, offset by the body stick.
        Vector3 basePoint = locomotion != null && locomotion.Target != null
            ? new Vector3(locomotion.Target.position.x, chest.position.y, locomotion.Target.position.z)
            : fallback;
        Vector3 side = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(basePoint - chest.position, Vector3.up)).normalized;
        return basePoint + side * (MouseOffset.x * 0.35f) + Vector3.up * (MouseOffset.y * 0.35f);
    }

    private Vector3 ClampAim(Vector3 point)
    {
        Vector3 root = transform.position;

        Vector3 flat = Vector3.ProjectOnPlane(point - root, Vector3.up);
        if (flat.sqrMagnitude < 0.0001f) flat = transform.forward * aimPlaneDistance;

        float angle = Vector3.SignedAngle(transform.forward, flat, Vector3.up);
        float clampedAngle = Mathf.Clamp(angle, -maxAimAngle, maxAimAngle);
        if (!Mathf.Approximately(angle, clampedAngle))
            flat = Quaternion.AngleAxis(clampedAngle - angle, Vector3.up) * flat;

        float distance = flat.magnitude;
        flat *= Mathf.Clamp(distance, 0.3f, maxReach + 1.5f) / distance;

        float height = Mathf.Clamp(point.y - root.y, aimHeightRange.x, aimHeightRange.y);
        return root + flat + Vector3.up * height;
    }

    private float ReachFor(int hand)
    {
        float reach = maxReach;
        if (armLength[hand] > 0.05f) reach = Mathf.Min(reach, armLength[hand] * reachFraction);
        return reach;
    }

    private Vector3 ClampToReach(Vector3 point, Vector3 shoulderPosition, int hand, float minDistance)
    {
        float reach = ReachFor(hand);
        Vector3 v = point - shoulderPosition;
        float d = v.magnitude;
        if (d < 0.001f) return shoulderPosition + transform.forward * Mathf.Max(minDistance, 0.05f);
        if (d > reach) return shoulderPosition + v * (reach / d);
        if (d < minDistance) return shoulderPosition + v * (minDistance / d);
        return point;
    }

    // ------------------------------------------------------------------ Lean & tracking

    private void UpdateLean(float dt)
    {
        float sx = Deflect(MouseOffset.x);
        float sy = Deflect(MouseOffset.y);
        float scale = poseMixer != null && poseMixer.HasGuardPose ? 1f : Mathf.Lerp(1f, blockLeanScale, blockBlend);

        float roll = -sx * leanDegrees * scale;
        float pitch = (sy < 0f
            ? sy * leanDegrees * (leanBackwardPercent / 100f)
            : sy * leanDegrees * (leanForwardPercent / 100f)) * scale;

        if (locomotion != null)
        {
            roll += -locomotion.LocalMove.x * moveLeanDegrees;
            pitch += locomotion.LocalMove.y * moveLeanDegrees * 0.5f;
        }

        Vector2 target = new Vector2(roll, pitch);
        lean = leanSmoothTime > 0f
            ? Vector2.SmoothDamp(lean, target, ref leanVelocity, leanSmoothTime, Mathf.Infinity, dt)
            : target;

        impactLean = Vector2.SmoothDamp(impactLean, Vector2.zero, ref impactLeanVelocity, 0.25f, Mathf.Infinity, dt);
        // Underdamped: the torso is shoved back, then RE-PLANTS through neutral with a small counter-swing —
        // the overshoot is what sells the mass. Every consumer (twist, lean, shove, hip sink) inherits it.
        const float shockOmega = 26f, shockZeta = 0.45f;
        bodyShockVelocity += (-shockOmega * shockOmega * bodyShock - 2f * shockZeta * shockOmega * bodyShockVelocity) * dt;
        bodyShock += bodyShockVelocity * dt;
        if (Mathf.Abs(bodyShock) < 0.001f && Mathf.Abs(bodyShockVelocity) < 0.01f) { bodyShock = 0f; bodyShockVelocity = 0f; }

        // Torso: coils while a hand is loaded, twists into a live punch. BUT when the pose library carries the
        // AUTHORED pivot (the mixer rotates the hips through the shot exactly as posed — a cross travels ~108°),
        // the synthetic twist stands down for that hand: 14° of extra spin on top of the real pivot is a
        // double turn.
        twist = 0f;
        for (int i = 0; i < 2; i++)
        {
            PunchState p = punches[i];
            bool authoredPivot = poseMixer != null && Mathf.Abs(poseMixer.StrikeYaw(i)) > 2f;
            if (p.phase == Phase.Control && !authoredPivot) twist += p.side * loadCoil * p.loadDepth;
            if (p.phase < Phase.Drive || authoredPivot) continue;
            float typeScale = p.type == PunchType.Hook ? 1.3f : 1f;
            // The torso turn LEADS the fist: it runs on the hips' window of the punch clock, so it is finished
            // rotating around the time the hand starts moving. Keyed to the fist (as it used to be) the body
            // arrived with the punch instead of throwing it, which reads as an arm punch every time.
            float lead = p.phase == Phase.Impact ? 1f : Mathf.Clamp01(Mathf.InverseLerp(0f, 0.55f, timelines[i].X));
            twist += -p.side * punchTwistDegrees * typeScale * lead;
        }
        twist += shockSide * impactJolt * 0.5f * bodyShock;   // the landing shoulder is shoved back

        // Idle life: Perlin micro-sway that disappears the instant real motion starts — the guard drifts, never poses.
        maxStrikeWeight = Mathf.Max(StrikeWeight(punches[0], 0), StrikeWeight(punches[1], 1));
        idleWeight = Mathf.Clamp01(1f - maxStrikeWeight - Mathf.Abs(sx) - Mathf.Abs(sy) - (locomotion != null ? locomotion.CurrentSpeed : 0f));
        float time = Time.time;
        sway.x = (Mathf.PerlinNoise(11.3f, time * 0.45f) - 0.5f) * 2f * idleSwayDegrees * idleWeight;
        sway.y = (Mathf.PerlinNoise(37.7f, time * 0.35f) - 0.5f) * 1.4f * idleSwayDegrees * idleWeight;
    }

    /// <summary>
    /// Extra lean from a landed punch: the torso recoils OFF THE PUNCH LINE — a hook rolls it sideways, a
    /// straight rocks it back — instead of the same straight-back nod for every punch.
    /// </summary>
    private Vector2 ShockLean => new Vector2(
        Vector3.Dot(shockDirection, transform.right) * impactJolt * 0.6f * bodyShock,
        -impactJolt * bodyShock * Mathf.Max(0.4f, Mathf.Abs(Vector3.Dot(shockDirection, transform.forward))));

    private float Deflect(float value)
    {
        float magnitude = Mathf.Abs(value);
        float t = Mathf.Clamp01((magnitude - leanDeadZone) / Mathf.Max(0.01f, leanFullAt - leanDeadZone));
        return Mathf.Sign(value) * t;
    }

    private void UpdateLookAt(float dt)
    {
        if (lookAtIK == null) return;

        float target = IsAiming || IsPunching ? 1f : idleTrackingWeight;
        lookWeight = Mathf.MoveTowards(lookWeight, target, dt * 4f);

        Vector3 lookPoint = AimPoint;
        lookPoint.y = Mathf.Max(lookPoint.y, chest.position.y - 0.1f);
        lookPoint.y -= 0.12f * maxStrikeWeight;   // the eyes stay on the target; the chin drops with the strike
        float leaningBack = Mathf.Clamp01(-(lean.y + impactLean.y + ShockLean.y) / Mathf.Max(1f, leanDegrees * leanBackwardPercent / 100f));

        IKSolverLookAt solver = lookAtIK.solver;
        solver.IKPosition = lookPoint;
        solver.IKPositionWeight = lookWeight;
        solver.bodyWeight = torsoAimWeight * (1f - 0.9f * leaningBack);   // aiming must not pull the spine forward against a lean-back
        solver.headWeight = headAimWeight;
        solver.eyesWeight = 0f;
    }

    // ------------------------------------------------------------------ IK (runs after LookAtIK, before the full-body solve)

    private void OnPreFullBodySolve()
    {
        ReanchorFists();
        CaptureFeet();
        ApplyLean();
        ApplyHands();
    }

    /// <summary>
    /// The virtual fists live in world space, but before a punch flies they belong to the BODY: when Bennett walks
    /// or turns while holding a punch (or blocking), carry the held fist along with him — the chambered arm can
    /// never be left behind or twisted by footwork. Flying punches stay world-anchored (the bag is in the world).
    /// </summary>
    private void ReanchorFists()
    {
        if (!rootAnchorValid)
        {
            prevRootPos = transform.position;
            prevRootRot = transform.rotation;
            rootAnchorValid = true;
            return;
        }
        Quaternion deltaRot = transform.rotation * Quaternion.Inverse(prevRootRot);
        for (int i = 0; i < 2; i++)
        {
            if (!fistValid[i]) continue;
            Phase phase = punches[i].phase;
            if (phase == Phase.Drive || phase == Phase.Impact) continue;
            fistPos[i] = transform.position + deltaRot * (fistPos[i] - prevRootPos);
            fistVel[i] = deltaRot * fistVel[i];
        }
        prevRootPos = transform.position;
        prevRootRot = transform.rotation;
    }

    /// <summary>Feet as the ANIMATION placed them, captured before the hip pivot — the legs must never twist along.</summary>
    /// <summary>
    /// THE FINAL WORD on glove containment, after the IK solve — the last writer before rendering. The
    /// pre-solve correction aims the solver out of the leather, but the solver can UNDERSHOOT a deep
    /// correction by centimetres, and a glove is a wide ball whose SIDE leads on hooks. So the SOLVED hand is
    /// measured against the visible surface at three points along the glove (knuckles, glove centre, wrist),
    /// each with its own allowance, and the hand bone is translated straight out of any remaining
    /// penetration. A couple of centimetres at the wrist hides inside the glove cuff; the leather can no
    /// longer swallow the glove no matter what the pose, the solver or the bag did this frame.
    /// </summary>
    private void ClampGlovesPostSolve()
    {
        if (physicsBody != null) return;
        for (int i = 0; i < 2; i++)
        {
            PunchState p = punches[i];
            PunchingBag bag = p.landedBag != null ? p.landedBag : AssistTarget;
            if (bag == null || !bag.IsBuilt) continue;

            IKEffector effector = EffectorFor((Hand)i);
            Transform bone = effector.bone;
            if (bone == null) continue;

            Vector3 fingers = (bone.rotation * fingerAxisLocal[i]).normalized;
            float allowed = GloveDepthAllowance(p);
            float halfReach = FistReach * 0.5f;

            // Three stations along the glove: the deepest violation wins.
            float excess = bag.VisualPenetration(bone.position + fingers * FistReach, out _, out Vector3 inward) - allowed;
            float centreExcess = bag.VisualPenetration(bone.position + fingers * halfReach, out _, out Vector3 inwardC)
                                 - (allowed + halfReach * 0.6f);
            if (centreExcess > excess) { excess = centreExcess; inward = inwardC; }
            float wristExcess = bag.VisualPenetration(bone.position, out _, out Vector3 inwardW) - (allowed + FistReach * 0.8f);
            if (wristExcess > excess) { excess = wristExcess; inward = inwardW; }

            if (excess <= 0.001f) continue;
            // Same rule as the pre-solve pass: a landed hand retreats along its OWN punch line (scaled for
            // oblique contacts) — the bag's curved normal must never deflect it sideways.
            bool clampStriking = p.phase == Phase.Drive || p.phase == Phase.Impact;
            Vector3 outDir;
            if (clampStriking && p.direction.sqrMagnitude > 0.5f)
            {
                outDir = p.direction;
                if (inward.sqrMagnitude > 0.5f)
                    excess /= Mathf.Max(0.35f, Vector3.Dot(p.direction, inward));
            }
            else outDir = inward.sqrMagnitude > 0.5f ? inward : Vector3.zero;
            if (outDir == Vector3.zero) continue;
            bone.position -= outDir * excess;
        }
    }

    /// <summary>How deep this hand's glove may currently sit in the VISIBLE leather (shared by both containment passes).</summary>
    private float GloveDepthAllowance(PunchState p)
    {
        bool striking = p.phase == Phase.Drive || p.phase == Phase.Impact;
        if (striking) return gloveLeatherWrap;
        if (p.phase == Phase.Recover)
        {
            float relax = 1f - Mathf.Clamp01(p.time / Mathf.Max(0.01f, RecoverDuration(p) * 0.7f));
            return Mathf.Lerp(-0.02f, gloveLeatherWrap, relax);
        }
        return -0.02f;
    }

    /// <summary>
    /// LOWER-BODY DRIVE, applied after the IK solve: each striking side's foot pivots on the ball — heel out,
    /// toes toward the target — and the heel lifts, scaled by the same strike weight that runs the kinetic
    /// chain (rear foot fully, lead foot at half). Pure rotations about the foot's own ankle, so the leg chain
    /// is never stretched, the stance never slides, and a foot in the air never pivots.
    /// </summary>
    private void OnPostFullBodySolve()
    {
        ClampGlovesPostSolve();
        if (!lowerBodyDrive || animator == null) return;
        for (int h = 0; h < 2; h++)
        {
            float target = StrikeWeight(punches[h], h) * (h == (int)rearHand ? 1f : 0.5f);
            if (useBalance && balance != null && !balance.FootPlanted(h)) target = 0f;
            heelDrive[h] = Mathf.SmoothDamp(heelDrive[h], target, ref heelDriveVel[h], 0.06f);
            if (heelDrive[h] < 0.01f) continue;

            Transform foot = animator.GetBoneTransform(h == 1 ? HumanBodyBones.RightFoot : HumanBodyBones.LeftFoot);
            if (foot == null) continue;
            float yawSign = h == 1 ? -1f : 1f;   // heel swings OUT, toes turn toward the target
            foot.Rotate(Vector3.up, yawSign * heelPivotDegrees * heelDrive[h], Space.World);
            foot.Rotate(transform.right, heelLiftDegrees * heelDrive[h], Space.World);
        }
    }

    private void CaptureFeet()
    {
        feetValid = false;
        if (bodyIK == null) return;
        Transform lf = bodyIK.solver.leftFootEffector.bone, rf = bodyIK.solver.rightFootEffector.bone;
        if (lf == null || rf == null) return;
        footPos[0] = lf.position; footRot[0] = lf.rotation;
        footPos[1] = rf.position; footRot[1] = rf.rotation;
        feetValid = true;
    }

    private void ApplyLean()
    {
        if (leanBones.Count == 0) return;
        Vector2 total = lean + impactLean + ShockLean + sway;
        float tuck = chinTuckDegrees * maxStrikeWeight;

        // These are written straight onto bone rotations, AFTER the pose mixer has run — so its NaN backstop
        // cannot catch anything that goes wrong here. A single NaN degree would rotate the whole hierarchy into
        // nothing and the character would vanish, so this frame is simply skipped instead.
        if (float.IsNaN(total.x) || float.IsNaN(total.y) || float.IsNaN(twist) || float.IsNaN(tuck)) return;

        if (Mathf.Abs(total.x) < 0.01f && Mathf.Abs(total.y) < 0.01f && Mathf.Abs(twist) < 0.01f && tuck < 0.1f) return;

        Vector3 forward = transform.forward;
        Vector3 right = transform.right;

        // The punch comes from the ground up: the HIPS pivot first (the feet are pinned from the pre-pivot capture).
        if (hipsBone != null && Mathf.Abs(twist) > 0.01f)
            hipsBone.rotation = Quaternion.AngleAxis(twist * hipTwistShare, Vector3.up) * hipsBone.rotation;

        float spineTwist = twist * (1f - hipTwistShare);
        foreach (LeanBone lb in leanBones)
        {
            Quaternion delta = Quaternion.AngleAxis(total.x * lb.share, forward)
                             * Quaternion.AngleAxis(total.y * lb.share, right)
                             * Quaternion.AngleAxis(spineTwist * lb.share, Vector3.up);
            lb.bone.rotation = delta * lb.bone.rotation;
        }

        // Chin tuck while striking: hands up, chin down.
        if (tuck > 0.1f)
        {
            if (neckBone != null) neckBone.rotation = Quaternion.AngleAxis(tuck * 0.45f, right) * neckBone.rotation;
            if (head != null) head.rotation = Quaternion.AngleAxis(tuck * 0.55f, right) * head.rotation;
        }
    }

    private void ApplyHands()
    {
        IKSolverFullBodyBiped solver = bodyIK.solver;
        float dt = Time.deltaTime;
        ApplyHand(punches[0], solver.leftHandEffector, solver.leftArmChain, Hand.Left, dt);
        ApplyHand(punches[1], solver.rightHandEffector, solver.rightArmChain, Hand.Right, dt);

        PinFoot(solver.leftFootEffector, 0);
        PinFoot(solver.rightFootEffector, 1);

        Vector3 offset = Vector3.zero;
        for (int i = 0; i < 2; i++)
        {
            PunchState p = punches[i];
            if (p.phase < Phase.Drive) continue;
            float w = StrikeWeight(p, i);
            offset += Vector3.ProjectOnPlane(p.direction, Vector3.up) * (bodyLean * w * (0.7f + 0.6f * p.overdrive));
            if (p.type == PunchType.Body || p.type == PunchType.Uppercut) offset += Vector3.down * (bodyShotCrouch * w);
        }
        offset += Vector3.up * (Mathf.Sin(Time.time * 3.1f) * idleBob * idleWeight);   // idle bounce
        // WEIGHT TRANSFER: the drive carries real mass toward the LEAD leg — the cross you feel in the floor.
        // Scaled by the same clock as the chain; the feet stay pinned, so it can never become a slide.
        if (lowerBodyDrive && weightTransfer > 0f && hipsBone != null)
        {
            int rear = (int)rearHand;
            float driveLead = Mathf.Max(StrikeWeight(punches[rear], rear),
                                        0.5f * StrikeWeight(punches[1 - rear], 1 - rear));
            if (driveLead > 0.01f)
            {
                Transform leadFoot = animator.GetBoneTransform(rear == 1 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                if (leadFoot != null)
                {
                    Vector3 toLead = Vector3.ProjectOnPlane(leadFoot.position - hipsBone.position, Vector3.up);
                    if (toLead.sqrMagnitude > 0.0004f)
                        offset += toLead.normalized * (weightTransfer * CharacterScale * driveLead);
                }
            }
        }

        if (bodyShock > 0.01f)
        {
            offset -= Vector3.ProjectOnPlane(shockDirection, Vector3.up) * (impactShove * bodyShock);   // the bag pushes back
            offset += Vector3.down * (0.04f * CharacterScale * bodyShock * bodyShock);                  // the weight DROPS into a clean landing — hip sink
        }
        solver.bodyEffector.positionOffset = offset;
    }

    private void PinFoot(IKEffector foot, int index)
    {
        if (!pinFeet || foot.bone == null) { foot.positionWeight = 0f; foot.rotationWeight = 0f; return; }
        foot.position = feetValid ? footPos[index] : foot.bone.position;
        foot.rotation = feetValid ? footRot[index] : foot.bone.rotation;
        foot.positionWeight = 1f;
        foot.rotationWeight = 1f;
    }

    /// <summary>
    /// The virtual fist chases an intent point with limited speed and acceleration. The reference skeleton's hand is
    /// IK'd to it; with the physics body on, the physical glove chases the same point through its muscles.
    /// Guard ▸ control (stick) ▸ drive (target) ▸ impact (recoil) ▸ recover (back to the animated guard).
    /// </summary>
    private void ApplyHand(PunchState p, IKEffector effector, FBIKChain arm, Hand hand, float dt)
    {
        int i = (int)hand;
        Transform shoulder = shoulders[i] != null ? shoulders[i] : chest;
        Vector3 animated = effector.bone != null ? effector.bone.position : shoulder.position;
        Quaternion handRotation = effector.bone != null ? effector.bone.rotation : transform.rotation;
        // Re-aimed every frame from the posed hand WHILE THE PUNCH IS LIVE only. Kept up during recovery it went
        // on forcing the knuckles at a target the punch had already finished with, so the wrist stayed locked on
        // the bag while the arm pulled back — the hand visibly twisting away as it retracted.
        if (p.phase == Phase.Drive || p.phase == Phase.Impact) p.fistRotation = FistRotation(p, i, handRotation);
        float elbow = 0f;
        float weight;
        float drive = 0f;
        float debugSteer = 0f;
        Vector3 debugProjection = Vector3.zero;

        switch (p.phase)
        {
            case Phase.Control:
            {
                timelines[i].ToChamber(dt);   // the clock eases to the chamber mark; release continues from it

                if (poseMixer != null)
                {
                    // POSE-DRIVEN HOLD: the chain is coiling the whole body into the chamber right now. The fist
                    // has nothing to do but be where that coil puts it — no IK, no synthetic chamber offset, so
                    // there is nothing to fight the wind-up and nothing to snap when it releases.
                    SetFist(i, animated);
                    weight = 0f;
                    effector.rotationWeight = 0f;
                    elbow = 0f;
                    break;
                }

                if (p.clipStateHash != 0)
                {
                    // ANIMATION-FIRST HOLD: the punch clip is scrubbed to its own wind-up frame, so the chamber is
                    // authored anatomy — no IK pulling the arm into a synthetic pose, nothing to break.
                    SetFist(i, animated);
                    weight = 0f;
                    effector.rotationWeight = 0f;
                    elbow = 0f;
                    break;
                }

                if (throwMode == ThrowMode.Release && HasLoadedPose(hand))
                {
                    // The Loaded reference pose owns the arm while you hold; the stick only nudges the fist up/down and
                    // sideways (smoothed offset on top of the pose — no chase, so no jiggle). The strike starts from here.
                    Vector2 stick = input.Body;
                    Vector3 desired = Vector3.up * (stick.y * loadAimVertical) + transform.right * (stick.x * loadAimLateral);
                    loadOffset[i] = Vector3.SmoothDamp(loadOffset[i], desired, ref loadOffsetVel[i], loadAimSmoothTime, Mathf.Infinity, dt);
                    SetFist(i, ClampToReach(animated + loadOffset[i], shoulder.position, i, 0f));
                    weight = 1f;
                    effector.rotationWeight = 0f;
                    elbow = 0.6f;
                    break;
                }
                Vector3 intent = throwMode == ThrowMode.Release
                    ? animated + ChamberOffset(p, PunchDirection(p.type, shoulder.position, AimPoint, out _)) * Mathf.Lerp(0.4f, 1f, p.charge)
                    : VolumePoint(p, animated, shoulder.position);
                // The chamber must stay in FRONT of the chest — a fist pulled behind the body folds the arm horribly.
                Vector3 chamberLocal = transform.InverseTransformPoint(intent);
                if (chamberLocal.z < 0.08f) chamberLocal.z = 0.08f;
                intent = transform.TransformPoint(chamberLocal);
                intent = ClampToReach(intent, shoulder.position, i, 0f);
                StepFist(i, intent, controlSpeed, controlAcceleration, dt);
                weight = Mathf.Clamp01(p.time / 0.1f);
                effector.rotationWeight = 0f;
                elbow = weight * 0.85f;
                break;
            }

            case Phase.Drive:
            {
                // THE FIST COMES OFF THE BODY. `animated` is the hand bone AFTER the pose chain has already
                // unwound the whole body this frame (the mixer runs in LateUpdate ahead of the IK solve), so for
                // the first half of the punch the fist simply IS the posed hand: the authored arc, the authored
                // elbow, the authored whip, untouched. Only past Aim Takeover does it steer onto the live target,
                // and even then it is a correction ADDED to the authored motion rather than a rail replacing it.
                //
                // The old code lerped guard → target from x = 0, which is why every punch travelled in a straight
                // line to the bag no matter what the body was doing. That was the "reaching" look.
                float x = timelines[i].Drive(dt, p.speed * Mathf.Lerp(1f, 1.45f, p.overdrive), p.profile.driveCurve);
                float steer = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(aimTakeover, 1f, x));
                debugSteer = steer;

                Vector3 pos;
                if (poseMixer != null)
                {
                    // The synthetic arc is gone: a hook curves because the authored hook pose curves it.
                    // The aim is allowed to CORRECT the authored punch, never to replace it — see Max Aim
                    // Correction. The steer ramp is already continuous in the punch clock, so the correction is
                    // applied DIRECTLY: rate-limiting it here (a briefly-tried fix) starved the takeover — the
                    // correction only has the last ~0.08 s of flight, and a punch whose aim cannot arrive in
                    // time lands wherever the chamber launched it, which is off the boxer's own side. That was
                    // "the right punch throws completely off to the right". The rate limit lives only in the
                    // RECOVERY now, where it belongs.
                    Vector3 correction = Vector3.ClampMagnitude(p.target - animated, maxAimCorrection * CharacterScale) * steer;
                    p.appliedCorrection = correction;
                    pos = animated + correction;

                    // A JAB OR A CROSS TRAVELS STRAIGHT. The authored poses wander sideways on the way out —
                    // correct for a hook, wrong for a straight — and the follow-through key sweeps the hand
                    // ACROSS at the very end, which is the "punch goes straight then the arm darts to the side".
                    // Project the fist onto the LAUNCH → TARGET line by however much of a straight this punch
                    // is: the distance ALONG the line is untouched (the authored acceleration survives), only
                    // the sideways and vertical drift is removed. (The old code projected onto the line from
                    // launch to the fist's own position — mathematically a no-op, which is why straights kept
                    // sweeping.)
                    float straightness = straightPath * Mathf.Clamp01(p.stickStraight);
                    Vector3 preProjection = pos;
                    if (straightness > 0.001f)
                    {
                        Vector3 axis = p.target - p.launchPos;
                        float length = axis.magnitude;
                        if (length > 0.01f)
                        {
                            axis /= length;
                            // Follow-through may run PAST the target — along the same line, never across it.
                            float along = Mathf.Clamp(Vector3.Dot(pos - p.launchPos, axis), 0f, length * 1.2f);
                            Vector3 onLine = p.launchPos + axis * along;
                            pos = Vector3.Lerp(pos, onLine, straightness);
                        }
                    }
                    debugProjection = pos - preProjection;
                }
                else
                {
                    pos = Vector3.Lerp(animated, p.target + PathOffset(p, x) * 0.35f, x * x * (3f - 2f * x));
                }

                fistVel[i] = dt > 0f ? (pos - fistPos[i]) / dt : Vector3.zero;
                fistPos[i] = pos;
                fistValid[i] = true;
                // IK only has work to do once there is a correction to apply; before that the arm is pure pose.
                weight = poseMixer != null
                    ? steer
                    : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.6f, x));
                effector.rotation = p.fistRotation;
                // In pose mode the authored wrist and elbow ARE the punch — the aim is only allowed to correct
                // them as it takes over, so the arm keeps the shape it was posed into for the whole first half.
                effector.rotationWeight = fistRotationWeight * (poseMixer != null ? steer : x * x);
                elbow = poseMixer != null ? 0.15f + 0.85f * steer : 0.35f + 0.65f * x;
                drive = p.overdrive;
                if (debugTrajectory)
                {
                    Debug.DrawLine(p.launchPos, p.target, Color.cyan);                       // the intended punch line
                    Debug.DrawRay(fistPos[i], fistVel[i] * 0.04f, Color.white);              // live fist velocity
                    Debug.DrawLine(p.target - Vector3.up * 0.05f, p.target + Vector3.up * 0.05f, Color.magenta);
                }
                // The posed fist just moved THIS frame — land now, not on next Update's stale position.
                if (!p.landed) TryLandOnSurface(hand, p, i);
                if (p.landed) BeginImpact(p, i);
                break;
            }

            case Phase.Impact:
            {
                // The chain finishes its release CONTINUOUSLY — the fist is planted by the script below, but the
                // rest of the body (elbow, shoulder, chest, follow-through) rides the clock, and slamming that
                // clock to 1 on the contact frame was a visible whole-body jerk toward the punching side.
                timelines[i].Set(Mathf.MoveTowards(timelines[i].X, 1f, 7f * dt));

                // The bag stops the fist: sink to the impact depth (decelerating), stay planted for the hit-stop,
                // then bounce back off the leather over the profile's hold. Scripted, not steered — no easing in.
                float sink = Mathf.Max(0.001f, p.sinkTime);
                float hold = p.hitStopLeft;
                float bounceTime = Mathf.Max(0.02f, p.profile.hold / Mathf.Max(0.01f, p.speed));
                Vector3 pos;
                if (p.time < sink)
                {
                    float t = p.time / sink;
                    pos = Vector3.Lerp(p.impactStart, p.target, 1f - (1f - t) * (1f - t));   // ease-out: fast in, stopped at depth
                }
                else if (p.time < sink + hold)
                {
                    pos = p.target;                                                               // planted
                }
                else
                {
                    // Decaying-velocity rebound: fast off the leather, easing out — an elastic exit, not a
                    // fixed sine. reboundSpeed came from the impact speed and the bag's mass at the contact.
                    float tb = p.time - sink - hold;
                    const float k = 18f;
                    float travel = p.reboundSpeed > 0.01f
                        ? p.reboundSpeed * (1f - Mathf.Exp(-k * tb)) / k
                        : p.recoil * Mathf.Sin(Mathf.Clamp01(tb / bounceTime) * Mathf.PI * 0.5f);
                    pos = p.target - p.direction * Mathf.Min(travel, p.recoil * 2.5f);
                }
                fistVel[i] = dt > 0f ? (pos - fistPos[i]) / dt : Vector3.zero;
                fistPos[i] = pos;
                fistValid[i] = true;
                // Hit-stop = freezing THIS hand's clock (pose and clip hold with the fist; the other hand is free).
                timelines[i].Frozen = p.time >= p.sinkTime && p.time < p.sinkTime + p.hitStopLeft;
                weight = 1f;
                effector.rotation = p.fistRotation;
                effector.rotationWeight = fistRotationWeight;
                elbow = 1f;
                drive = p.overdrive * 0.5f;
                break;
            }

            case Phase.Recover:
            {
                // The clock plays back DOWN from where the punch ended — the body recovers through the chamber
                // along the same authored chain, with a touch of whiff overshoot when the punch hit nothing.
                timelines[i].Frozen = false;
                float t = Mathf.Clamp01(p.time / RecoverDuration(p));
                float x = p.xAtRecover * (1f - Mathf.SmoothStep(0f, 1f, t));
                timelines[i].Set(x);

                // NO STEP OUT OF THE PLANT. The drive/impact fist carried terms the recovery formula does not
                // (the straight-line projection, the scripted plant) — switching formulas used to move the hand
                // 10+ cm in a single frame, which the flight recorder caught as the sideways dart. The recovery
                // now starts from the EXACT point the punch ended and blends onto its own path.
                if (!p.recoverAnchored)
                {
                    p.recoverAnchored = true;
                    p.recoverFrom = fistValid[i] ? fistPos[i] : animated;
                    p.recoverExitVel = fistVel[i];   // the rebound's momentum carries across the phase switch
                }

                Vector3 overshoot = p.missed ? p.direction * (whiffOvershoot * Mathf.Max(0f, 1f - t * 4f)) : Vector3.zero;
                // The chain's WEIGHT is what recovers (the mixer fades it back to the guard), so the posed hand
                // walks itself home; the fist just rides it while the aim correction unwinds — and the punch
                // being OVER, the correction may only LET GO: it can never grow again, so the retreating pose
                // cannot drag the fist back toward the bag on its way home.
                float steerBack = poseMixer != null ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(aimTakeover, 1f, x)) : x * x;
                debugSteer = steerBack;
                Vector3 pos;
                if (poseMixer != null)
                {
                    Vector3 desired = Vector3.ClampMagnitude(p.target - animated, maxAimCorrection * CharacterScale) * steerBack;
                    desired = Vector3.ClampMagnitude(desired, p.appliedCorrection.magnitude);
                    p.appliedCorrection = Vector3.MoveTowards(p.appliedCorrection, desired, aimCorrectionRate * CharacterScale * dt);
                    pos = animated + overshoot + p.appliedCorrection;
                }
                else pos = Vector3.Lerp(animated + overshoot, p.target, x * x);

                // The anchor is BALLISTIC for the first beats: the rebound's exit velocity drifts the hand off
                // the leather while the recovery blend takes over — an elastic exit instead of a dead stop.
                Vector3 anchor = p.recoverFrom + p.recoverExitVel * (Mathf.Min(p.time, 0.06f) * Mathf.Max(0f, 1f - p.time / 0.12f));
                pos = Vector3.Lerp(anchor, pos, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(p.time / 0.12f)));
                fistVel[i] = dt > 0f ? (pos - fistPos[i]) / dt : Vector3.zero;
                fistPos[i] = pos;
                fistValid[i] = true;
                weight = poseMixer != null ? steerBack : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 0.95f, x));
                effector.rotation = p.fistRotation;
                // Let the wrist go early in the recovery — the authored guard should own it on the way home.
                effector.rotationWeight = fistRotationWeight * steerBack * steerBack;
                elbow = x;
                break;
            }

            default:
            {
                p.recoverAnchored = false;
                if (blockBlend > 0f && (poseMixer == null || !poseMixer.HasGuardPose))
                {
                    int side = hand == Hand.Right ? 1 : -1;
                    Vector3 guard = head.position
                                  + transform.right * (blockGuardOffset.x * side)
                                  + Vector3.up * blockGuardOffset.y
                                  + transform.forward * blockGuardOffset.z;
                    StepFist(i, ClampToReach(guard, shoulder.position, i, 0f), controlSpeed, controlAcceleration, dt);
                    weight = blockBlend;
                }
                else
                {
                    weight = 0f;
                    fistValid[i] = false;
                }
                effector.rotationWeight = 0f;
                break;
            }
        }

        KeepGloveOutOfBag(p, i, effector, ref weight);

        if (weight > 0f && fistValid[i]) effector.position = fistPos[i];
        effector.positionWeight = weight;

        // The sensor measures impact with the exact fist speed while a punch is driven (bone sampling under-reads
        // it) — ceiling-checked first: an IK correction can move the virtual fist metres in a frame, and that
        // spike must read as a glitch, never as a superhuman punch.
        IPunchSensor sensor = GetSensor(hand);
        if (sensor != null)
            sensor.DrivenVelocity = p.phase == Phase.Drive && fistValid[i]
                ? Vector3.ClampMagnitude(fistVel[i], maxFistSpeed * CharacterScale)
                : (Vector3?)null;

        // POWER AT THE MOMENT OF IMPACT: balance and arm alignment are measured NOW, not at launch — a fast
        // arm from a shaky base, or a crooked wrist, lands measurably lighter than a planted, lined-up shot.
        if (sensor != null && p.phase == Phase.Drive)
        {
            float live = AlignmentFactor(hand, p);
            if (useBalance && balance != null)
                live *= Mathf.Lerp(balancePowerFloor, 1f, balance.Balance01)
                      * Mathf.Lerp(0.75f, 1f, balance.Plantedness(i));
            sensor.PowerScale = p.powerScale * live;
        }

        // The physical glove chases the same virtual fist; when the hand is idle the muscles just follow the
        // animation. During a punch the fist target goes to the physics for the WHOLE flight, not just once the
        // aim's IK weight rises — in pose mode that weight is zero for the first half, and without a target the
        // muscles were chasing the whipping pose on their resting pin and falling half a metre behind it.
        bool driven = fistValid[i] && (weight > 0f || p.phase == Phase.Drive || p.phase == Phase.Impact);
        Vector3 handoffVelocity = fistValid[i]
            ? Vector3.ClampMagnitude(fistVel[i], maxFistSpeed * CharacterScale)   // a one-frame spike must never become a muscle yank
            : Vector3.zero;
        physicsBody?.SetHand(i, driven, fistValid[i] ? fistPos[i] : animated, handoffVelocity, drive);
        // …and the body needs the punch's own clock: it fires the kinetic chain off it (hips first, fist last),
        // pins the flying arm, and drops that pin the instant the glove lands so the impulse crumples the arm.
        physicsBody?.SetPunch(i, p.phase, timelines[i].X, p.overdrive);

        if (elbow > 0f && elbowGoals[i] != null)
        {
            elbowGoals[i].position = PoseAwareElbowGoal(p, shoulder.position, i);
            arm.bendConstraint.bendGoal = elbowGoals[i];
            arm.bendConstraint.weight = elbowGoalWeight * elbow;
        }
        else
        {
            arm.bendConstraint.weight = 0f;
        }

        // The frame's full story, for PunchDebug: what the pose said, what the aim added, where the fist went.
        pipelineState[i] = new HandPipelineState
        {
            phase = p.phase,
            x = timelines[i].X,
            steer = debugSteer,
            animatedHand = animated,
            virtualFist = fistValid[i] ? fistPos[i] : animated,
            fistVelocity = fistVel[i],
            target = p.target,
            launch = p.launchPos,
            correction = p.appliedCorrection,
            projection = debugProjection,
        };
    }

    /// <summary>Where the controlled hand wants to be for the current stick: back/forward, sideways, and the aim height.</summary>
    private Vector3 VolumePoint(PunchState p, Vector3 guard, Vector3 shoulder)
    {
        Vector2 stick = input.Body;
        Vector3 forward = Vector3.ProjectOnPlane(AimPoint - shoulder, Vector3.up);
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : transform.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        float along = stick.y >= 0f ? stick.y * forwardRange : stick.y * backRange;
        float lateral = stick.x * sideRange;
        float height = Mathf.Clamp((AimPoint.y - guard.y) * 0.5f, -verticalRange, verticalRange);
        if (p.type == PunchType.Uppercut && stick.y < 0f) height -= uppercutDip * -stick.y;

        return guard + forward * along + right * lateral + Vector3.up * height;
    }

    private Vector3 ChamberOffset(PunchState p, Vector3 aimDirection)
    {
        Vector3 back = -Vector3.ProjectOnPlane(aimDirection, Vector3.up).normalized * backRange * 0.35f;   // shallow: real chambers pull the ELBOW, not the fist
        if (back.sqrMagnitude < 0.0001f) back = -transform.forward * backRange * 0.5f;
        Vector3 right = Vector3.Cross(Vector3.up, aimDirection).normalized;
        switch (p.type)
        {
            case PunchType.Hook:     return back * 0.6f + right * (p.side * hookWidth * 0.5f);
            case PunchType.Uppercut: return back * 0.4f + Vector3.down * uppercutDip;
            case PunchType.Body:     return back * 0.8f + Vector3.down * 0.05f;
            default:                 return back;
        }
    }

    /// <summary>Curved part of the drive: hooks arc outward then in, uppercuts dip then rise. Zero at the target.</summary>
    private Vector3 PathOffset(PunchState p, float progress)
    {
        float arc = Mathf.Sin(Mathf.Clamp01(progress) * Mathf.PI);
        // Continuous trajectory: the stick's hook/uppercut mix bends the path, so in-between inputs throw real hybrids.
        float hookAmt = Mathf.Max(p.stickHook, p.type == PunchType.Hook ? 1f : 0f);
        float upperAmt = Mathf.Max(p.stickUpper, p.type == PunchType.Uppercut ? 1f : 0f);
        return p.right * (p.side * hookWidth * p.arcScale * arc * hookAmt) + Vector3.down * (uppercutDip * p.arcScale * arc * upperAmt);
    }

    private void StepFist(int i, Vector3 intent, float maxSpeed, float maxAcceleration, float dt)
    {
        if (!fistValid[i] || dt <= 0f) { SetFist(i, intent); return; }

        Vector3 desired = Vector3.ClampMagnitude((intent - fistPos[i]) / 0.04f, maxSpeed);
        fistVel[i] = Vector3.MoveTowards(fistVel[i], desired, maxAcceleration * dt);
        fistPos[i] += fistVel[i] * dt;
    }

    /// <summary>
    /// IK mode: the glove must never sit inside the bag. During a punch the knuckles may reach Impact Depth into
    /// the LIVE surface (exactly what the mesh dent covers); in guard, they ride on it. Any deeper — the bag swung
    /// in, the boxer walked into it, or the plant outlasted the swing — and the hand is pushed out along the
    /// surface normal, every frame. This is what keeps contact honest from every camera angle; the correction is
    /// continuous (zero at first touch), so it never pops. With the physics body on, the real glove collider does this.
    /// </summary>
    private void KeepGloveOutOfBag(PunchState p, int i, IKEffector effector, ref float weight)
    {
        if (physicsBody != null) return;
        PunchingBag bag = p.landedBag != null ? p.landedBag : AssistTarget;
        if (bag == null || !bag.IsBuilt) return;

        bool striking = p.phase == Phase.Drive || p.phase == Phase.Impact;

        // THE ALLOWANCE MUST NOT STEP. Dropping it from Impact Depth (7 cm) to a hair the instant recovery began
        // meant the glove was suddenly ~6.5 cm too deep, and this correction shoved it straight out along the
        // BAG'S SURFACE NORMAL — sideways to the punch, at 45/s. That is the hand darting off in another
        // direction right after landing a clean shot. It relaxes out with the recovery now.
        // CONTAINMENT AGAINST THE LEATHER THE PLAYER SEES: penetration is measured on the DEFORMED surface
        // (VisualPenetration = collider − carved pocket + squash bulge), so the crater itself grants the sink.
        // A clean heavy plant sits deep in its own crater; a weak punch is held out by its shallow one —
        // automatically. The glove may only ever WRAP a couple of centimetres into the visible skin.
        // (ClampGlovesPostSolve applies the same allowance again AFTER the solve — the final word.)
        float maxDepth = GloveDepthAllowance(p);

        bool driven = weight > 0f && fistValid[i];
        Vector3 bonePos = effector.bone != null ? effector.bone.position : transform.position;
        Vector3 hand = driven ? fistPos[i] : bonePos;
        Vector3 fingers = striking && p.direction.sqrMagnitude > 0.5f
            ? p.direction
            : (effector.bone != null ? effector.bone.rotation * fingerAxisLocal[i] : transform.forward).normalized;
        Vector3 knuckles = hand + fingers * FistReach;

        float depth = bag.VisualPenetration(knuckles, out _, out Vector3 inward);
        // What the player SEES is a blend of the animated bone and the IK target. Whenever the IK does not own
        // the hand outright (a hold at point-blank range, the early flight, partial weight), the BONE can be
        // deeper than the virtual fist — measure the deeper of the two, or the visible glove stays buried.
        if (weight < 0.999f && effector.bone != null)
        {
            float boneDepth = bag.VisualPenetration(bonePos + fingers * FistReach, out _, out _);
            depth = Mathf.Max(depth, boneDepth);
        }
        float excess = Mathf.Max(0f, depth - maxDepth);

        // WHILE STRIKING, THE ESCAPE ROUTE IS THE PUNCH LINE — never the bag's surface normal. On a curved bag
        // the normal points sideways for any off-centre contact, and pushing along it was exactly the hand
        // visibly "changing direction" the instant it landed. Straight back out the way it came in, scaled up
        // for oblique hits so the retreat still clears the surface.
        Vector3 outDir = inward;
        if (striking && p.direction.sqrMagnitude > 0.5f)
        {
            outDir = p.direction;
            if (inward.sqrMagnitude > 0.5f)
                excess /= Mathf.Max(0.35f, Vector3.Dot(p.direction, inward));
        }

        // The arm has inertia: push out near-instantly (with a centimetre of give), release smoothly. The bag moves
        // in 20 ms physics steps — following it rigidly made the hand judder when the bag swung back into the glove.
        float dt = Time.deltaTime;
        float snap = 1f - Mathf.Exp(-45f * dt);
        bagPush[i] = excess > bagPush[i]
            ? Mathf.Lerp(bagPush[i], excess, snap)
            : Mathf.MoveTowards(bagPush[i], excess, 1.2f * dt);
        // THE HARD FLOOR: however fast the bag slams back into the glove, the leather may never swallow it more
        // than ~1 cm past its allowance — the smoothing only shapes that last centimetre, never the whole gap.
        bagPush[i] = Mathf.Max(bagPush[i], excess - 0.008f);
        if (bagPush[i] < 0.0005f) { bagPushDir[i] = Vector3.zero; return; }

        Vector3 direction = excess > 0f ? outDir : bagPushDir[i];
        if (excess > 0f && bagPushDir[i] != Vector3.zero) direction = Vector3.Lerp(bagPushDir[i], outDir, snap).normalized;
        if (direction == Vector3.zero) { bagPush[i] = 0f; return; }
        bagPushDir[i] = direction;

        // Correct from what the player actually SEES (the render is a weight-blend of bone and fist), so the
        // pushed-out hand continues on its own line instead of snapping across to the virtual fist's.
        Vector3 basis = driven ? Vector3.Lerp(bonePos, hand, Mathf.Clamp01(weight)) : hand;
        Vector3 corrected = basis - direction * bagPush[i];
        if (driven)
        {
            fistPos[i] = corrected;
            // The correction must actually WIN the blend: at partial IK weight the solver was quietly ignoring
            // the corrected fist while the posed bone stayed in the leather. Authority rises with the need.
            weight = Mathf.Max(weight, Mathf.Clamp01(bagPush[i] / 0.02f));
        }
        else
        {
            // The animated guard hand drifted into the bag: blend it onto the surface, softly in and out.
            effector.position = corrected;
            weight = Mathf.Max(weight, Mathf.Clamp01(bagPush[i] / 0.02f));
        }
    }

    private void SetFist(int i, Vector3 position)
    {
        fistPos[i] = position;
        fistVel[i] = Vector3.zero;
        fistValid[i] = true;
    }

    /// <summary>
    /// How well the wrist–elbow–shoulder line backs the punch direction at THIS instant. A straight lands
    /// through a stacked arm; a crooked one bleeds force. Hooks are exempt — a bent arm is their correct shape.
    /// </summary>
    private float AlignmentFactor(Hand hand, PunchState p)
    {
        if (p.type == PunchType.Hook) return 1f;
        int i = (int)hand;
        Transform shoulder = shoulders[i];
        Transform elbow = lowerArms[i];
        Transform wrist = EffectorFor(hand).bone;
        if (shoulder == null || elbow == null || wrist == null) return 1f;
        float forearm = Mathf.Clamp01(Vector3.Dot((wrist.position - elbow.position).normalized, p.direction));
        if (p.type == PunchType.Uppercut) return Mathf.Lerp(0.8f, 1f, forearm);
        float upperArm = Mathf.Clamp01(Vector3.Dot((elbow.position - shoulder.position).normalized, p.direction));
        return Mathf.Lerp(0.75f, 1f, forearm * upperArm);
    }

    private float StrikeWeight(PunchState p, int i)
    {
        switch (p.phase)
        {
            case Phase.Drive:
            case Phase.Recover: return Mathf.Clamp01(timelines[i].X);   // one clock drives twist, lean, chin, everything
            case Phase.Impact:  return 1f;
            default:            return 0f;
        }
    }

    private Vector3 ElbowGoal(PunchState p, Vector3 shoulder, int hand)
    {
        int side = hand == 1 ? 1 : -1;
        Vector3 d = p.direction.sqrMagnitude > 0.001f ? p.direction : transform.forward;
        Vector3 r = p.right.sqrMagnitude > 0.001f ? p.right : transform.right;
        switch (p.type)
        {
            case PunchType.Hook:     return shoulder + r * (side * 0.5f) + d * 0.2f + Vector3.up * 0.05f;
            case PunchType.Uppercut: return shoulder + d * 0.3f - Vector3.up * 0.45f + r * (side * 0.1f);
            case PunchType.Body:     return shoulder + d * 0.2f + r * (side * 0.3f) - Vector3.up * 0.45f;
            default:                 return shoulder + d * 0.25f + r * (side * 0.25f) - Vector3.up * 0.35f;
        }
    }

    private IKEffector EffectorFor(Hand hand) => hand == Hand.Left ? bodyIK.solver.leftHandEffector : bodyIK.solver.rightHandEffector;

    private bool HasLoadedPose(Hand hand)
    {
        return poseMixer != null && poseMixer.HasType((int)hand, ReferencePose.Category.Loaded);
    }

    // ------------------------------------------------------------------ Reticle

    private void OnGUI()
    {
        if (!drawReticle || !Application.isPlaying || !IsPlayerControlled) return;
        if (BoxerInput.GamepadActive || !BoxerInput.CursorLocked) return;
        if (reticleTexture == null) reticleTexture = MakeReticle();

        Vector2 p = BoxerInput.PointerScreenPosition;
        float size = 28f;
        Color color = IsPunching ? new Color(1f, 0.3f, 0.2f, 0.9f) : (IsAiming ? new Color(1f, 0.85f, 0.2f, 0.9f) : new Color(1f, 1f, 1f, 0.75f));
        Color old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(p.x - size * 0.5f, Screen.height - p.y - size * 0.5f, size, size), reticleTexture);
        GUI.color = old;
    }

    private static Texture2D MakeReticle()
    {
        const int n = 32;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        Color[] px = new Color[n * n];
        float c = (n - 1) * 0.5f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float ring = 1f - Mathf.Clamp01(Mathf.Abs(d - 11f) - 1f);          // 2px ring at radius 11
                float dot = 1f - Mathf.Clamp01(d - 1.5f);                            // centre dot
                px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Max(ring, dot));
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    // ------------------------------------------------------------------ Reference poses

    /// <summary>
    /// Turn what each hand is doing into pose intent — continuous 0-1 weights, never states. Loaded follows the
    /// wind-up; straight / hook / uppercut follow the strike progress, mixing the classified type with what the
    /// fist's velocity actually looks like (forward, lateral arc, rising); recovery fades them; guard fills the rest.
    /// </summary>
    private void UpdatePoseIntent()
    {
        if (poseMixer == null) return;

        for (int h = 0; h < 2; h++)
        {
            PunchState p = punches[h];
            switch (p.phase)
            {
                case Phase.Control:
                {
                    // HOLDING = COILING. The whole body winds up together into the chamber of whatever punch the
                    // stick is currently shaping, so the shape you see while holding is the shape that fires.
                    // The shape is SMOOTHED on its way to the mixer: the stick may flick, the body may not —
                    // an instantly rebuilt chamber used to step the arm sideways in a single frame.
                    Vector2 shaped = p.hasLoadStick ? p.loadStick : ShapeStick(p, input.Body);
                    StickBlend(shaped, p.side, out float sHook, out float sUpper, out float sStraight);
                    Vector4 wanted = new Vector4(sStraight, sHook, sUpper, HoldHeight(shaped, p.side));
                    coilShape[h] = SmoothDamp4(coilShape[h], wanted, ref coilShapeVelocity[h], 0.09f);
                    // The aim's SIDE, smoothed on the same clock: the 2D loading grid follows the pointer/stick
                    // left-right exactly as the height ladder follows it up-down — by live percentage.
                    coilLateral[h] = Mathf.SmoothDamp(coilLateral[h], Mathf.Clamp(shaped.x, -1f, 1f), ref coilLateralVelocity[h], 0.09f);
                    p.aimLateral = coilLateral[h];
                    float depth = throwMode == ThrowMode.Release ? p.charge : p.loadDepth;

                    // A RETAKE MUST UNWIND, NOT SNAP. Pressing again mid-recovery used to flip the body from a
                    // half-released shape straight to a 40% coil in one frame — the recorder's biggest dart
                    // (15.5 m/s, both arms at once). While the clock is still above the chamber mark the shape
                    // keeps travelling home along the RELEASE path; only at the chamber does it become a coil,
                    // entering at full depth and easing to the held depth.
                    float pwc = p.profile != null ? p.profile.poseWeight : 1f;
                    if (timelines[h].X > PunchTimeline.ChamberX + 0.03f)
                    {
                        p.coilBlend = 1f;
                        poseMixer.SetPunch(h, PunchPoseChain.Stage.Release, Mathf.Clamp01(timelines[h].X), 0f,
                                           coilShape[h].x * pwc, coilShape[h].y * pwc, coilShape[h].z * pwc, coilShape[h].w, coilLateral[h]);
                    }
                    else
                    {
                        float wantedDepth = Mathf.Clamp01(0.4f + 0.6f * depth);
                        if (p.overcooked) wantedDepth = Mathf.Min(1f, wantedDepth + 0.25f);   // overcooked: deep, heavy, telegraphed
                        p.coilBlend = p.coilBlend <= 0f ? wantedDepth
                            : Mathf.MoveTowards(p.coilBlend, wantedDepth, 3f * Time.deltaTime);
                        poseMixer.SetPunch(h, PunchPoseChain.Stage.Coil, 0f, p.coilBlend,
                                           coilShape[h].x * pwc, coilShape[h].y * pwc, coilShape[h].z * pwc, coilShape[h].w, coilLateral[h]);
                    }
                    break;
                }

                case Phase.Drive:
                case Phase.Impact:
                case Phase.Recover:
                {
                    // RELEASED. The chain unwinds hips-first on the punch clock. Recovery latches x where the
                    // punch ended and lets the chain's WEIGHT fade back to the guard — playing x backwards would
                    // rewind the punch, which reads as a mistake rather than a recovery.
                    float x = p.phase == Phase.Recover ? Mathf.Clamp01(p.xAtRecover) : Mathf.Clamp01(timelines[h].X);
                    float pw = p.profile != null ? p.profile.poseWeight : 1f;   // per-type pose authority, in the Inspector
                    poseMixer.SetPunch(h, PunchPoseChain.Stage.Release, x, 0f,
                                       p.stickStraight * pw, p.stickHook * pw, p.stickUpper * pw, p.height01, p.aimLateral);
                    break;
                }

                default:
                    poseMixer.ClearPunch(h);
                    break;
            }
        }
        poseMixer.Frozen = false;   // hit-stop freezes each hand's own timeline, never the whole mixer
    }


    private static Vector4 SmoothDamp4(Vector4 current, Vector4 target, ref Vector4 velocity, float smoothTime)
    {
        return new Vector4(
            Mathf.SmoothDamp(current.x, target.x, ref velocity.x, smoothTime),
            Mathf.SmoothDamp(current.y, target.y, ref velocity.y, smoothTime),
            Mathf.SmoothDamp(current.z, target.z, ref velocity.z, smoothTime),
            Mathf.SmoothDamp(current.w, target.w, ref velocity.w, smoothTime));
    }

    /// <summary>Elbow hint: the built-in per-type hint, pulled toward where the blended reference pose put the elbow.</summary>
    private Vector3 PoseAwareElbowGoal(PunchState p, Vector3 shoulder, int hand)
    {
        Vector3 goal = ElbowGoal(p, shoulder, hand);
        if (poseMixer == null) return goal;

        float influence = Mathf.Max(poseMixer.StrikeInfluence(hand), poseMixer.LoadedWeight(hand)) * poseElbowWeight;
        if (influence < 0.02f) return goal;

        Transform elbow = Bone(hand == 0 ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        if (elbow == null) return goal;
        Vector3 outward = (elbow.position - shoulder).normalized;
        return Vector3.Lerp(goal, elbow.position + outward * 0.05f, influence);
    }
    // ------------------------------------------------------------------ Sensors

    private IPunchSensor GetSensor(Hand hand)
    {
        if (physicsBody != null) return hand == Hand.Left ? physicsBody.LeftFist : physicsBody.RightFist;
        if (hitboxes == null) return null;
        return hand == Hand.Left ? hitboxes.LeftHand : hitboxes.RightHand;
    }

    private void HookSensors()
    {
        if (sensorsHooked) return;

        if (physicsBody != null)
        {
            if (physicsBody.LeftFist == null && physicsBody.RightFist == null) return;
            if (physicsBody.LeftFist != null)
            {
                physicsBody.LeftFist.KnuckleAxisLocal = fingerAxisLocal[0];   // req.md §18: knuckles vs. wrist
                physicsBody.LeftFist.OnLanded.AddListener((fist, bag, info) => OnPunchLanded(Hand.Left, bag, info));
            }
            if (physicsBody.RightFist != null)
            {
                physicsBody.RightFist.KnuckleAxisLocal = fingerAxisLocal[1];
                physicsBody.RightFist.OnLanded.AddListener((fist, bag, info) => OnPunchLanded(Hand.Right, bag, info));
            }
            sensorsHooked = true;
            return;
        }

        if (hitboxes == null || (hitboxes.LeftHand == null && hitboxes.RightHand == null)) return;
        if (hitboxes.LeftHand != null) hitboxes.LeftHand.OnLanded.AddListener((hb, bag, info) => OnPunchLanded(Hand.Left, bag, info));
        if (hitboxes.RightHand != null) hitboxes.RightHand.OnLanded.AddListener((hb, bag, info) => OnPunchLanded(Hand.Right, bag, info));
        sensorsHooked = true;
    }

    // ------------------------------------------------------------------ Setup helpers

    private void MeasureArms()
    {
        shoulders[0] = Bone(HumanBodyBones.LeftUpperArm);
        shoulders[1] = Bone(HumanBodyBones.RightUpperArm);
        armLength[0] = ArmLength(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
        armLength[1] = ArmLength(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
        fingerAxisLocal[0] = FingerAxis(HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftLowerArm);
        fingerAxisLocal[1] = FingerAxis(HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightLowerArm);
    }

    /// <summary>
    /// The direction the fingers point, in the hand bone's local space — from a finger bone when the rig has one,
    /// else along the forearm. Rigs disagree on which local axis runs along a bone (Mixamo: +Y), so it is measured.
    /// </summary>
    private Vector3 FingerAxis(HumanBodyBones handBone, HumanBodyBones middle, HumanBodyBones index, HumanBodyBones lowerArm)
    {
        Transform hand = Bone(handBone);
        if (hand == null) return Vector3.forward;
        Transform finger = Bone(middle);
        if (finger == null) finger = Bone(index);
        Vector3 world = finger != null ? finger.position - hand.position : Vector3.zero;
        if (world.sqrMagnitude < 1e-6f)
        {
            Transform forearm = Bone(lowerArm);
            world = forearm != null ? hand.position - forearm.position : hand.forward;
        }
        Vector3 local = hand.InverseTransformDirection(world);
        return local.sqrMagnitude > 1e-6f ? local.normalized : Vector3.forward;
    }

    private float ArmLength(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand)
    {
        Transform u = Bone(upper), l = Bone(lower), h = Bone(hand);
        if (u == null || l == null || h == null) return 0f;
        return Vector3.Distance(u.position, l.position) + Vector3.Distance(l.position, h.position);
    }

    private void BuildLeanChain()
    {
        leanBones.Clear();
        Vector3 shares = leanShares;
        float total = shares.x + shares.y + shares.z;
        if (total <= 0f) return;
        shares /= total;

        AddLeanBone(HumanBodyBones.Spine, shares.x);

        Transform chestBone = Bone(HumanBodyBones.Chest);
        Transform upperChest = Bone(HumanBodyBones.UpperChest);
        if (chestBone != null && upperChest != null)
        {
            leanBones.Add(new LeanBone { bone = chestBone, share = shares.y * 0.6f });
            leanBones.Add(new LeanBone { bone = upperChest, share = shares.y * 0.4f });
        }
        else AddLeanBone(chestBone != null ? HumanBodyBones.Chest : HumanBodyBones.UpperChest, shares.y);

        Transform neck = Bone(HumanBodyBones.Neck);
        Transform headBone = Bone(HumanBodyBones.Head);
        if (neck != null && headBone != null)
        {
            leanBones.Add(new LeanBone { bone = neck, share = shares.z * 0.4f });
            leanBones.Add(new LeanBone { bone = headBone, share = shares.z * 0.6f });
        }
        else AddLeanBone(HumanBodyBones.Head, shares.z);
    }

    private void AddLeanBone(HumanBodyBones bone, float share)
    {
        Transform t = Bone(bone);
        if (t != null && share > 0f) leanBones.Add(new LeanBone { bone = t, share = share });
    }

    private Transform Bone(HumanBodyBones bone)
    {
        return animator != null && animator.isHuman ? animator.GetBoneTransform(bone) : null;
    }

    private void EnsureIK()
    {
        if (bodyIK == null) bodyIK = GetComponent<FullBodyBipedIK>();
        if (bodyIK == null) bodyIK = gameObject.AddComponent<FullBodyBipedIK>();
        if (bodyIK.references == null || bodyIK.references.pelvis == null)
        {
            BipedReferences references = new BipedReferences();
            if (BipedReferences.AutoDetectReferences(ref references, transform, BipedReferences.AutoDetectParams.Default))
                bodyIK.SetReferences(references, null);
            else
                Debug.LogError("BoxerPunchController: could not auto-detect biped references for Final IK. Set them up on the FullBodyBipedIK component.", this);
        }
        ConfigureFullBody(bodyIK.solver);

        if (lookAtIK == null) lookAtIK = GetComponent<LookAtIK>();
        if (lookAtIK == null) lookAtIK = gameObject.AddComponent<LookAtIK>();
        if (lookAtIK.solver.head == null || lookAtIK.solver.head.transform == null)
        {
            List<Transform> spine = new List<Transform>(3);
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
            {
                Transform t = Bone(bone);
                if (t != null) spine.Add(t);
            }
            Transform headBone = Bone(HumanBodyBones.Head);
            if (headBone != null) lookAtIK.solver.SetChain(spine.ToArray(), headBone, new Transform[0], transform);
            else Debug.LogWarning("BoxerPunchController: no Head bone; LookAtIK left unconfigured.", this);
        }
        lookAtIK.solver.IKPositionWeight = 0f;
        lookAtIK.solver.clampWeight = 0.5f;

        IKExecutionOrder order = GetComponent<IKExecutionOrder>();
        if (order == null) order = gameObject.AddComponent<IKExecutionOrder>();
        if (order.IKComponents == null || order.IKComponents.Length == 0)
            order.IKComponents = new IK[] { lookAtIK, bodyIK };
    }

    /// <summary>Full-body settings that keep the reference upright: hands never drag the body down or forward.</summary>
    public static void ConfigureFullBody(IKSolverFullBodyBiped solver)
    {
        solver.IKPositionWeight = 1f;
        solver.pullBodyVertical = 0f;
        solver.pullBodyHorizontal = 0f;
        solver.spineStiffness = 0.7f;
        foreach (FBIKChain arm in new[] { solver.leftArmChain, solver.rightArmChain })
        {
            // REACH, not pull: the chain extends toward the target and the spine follows into the punch,
            // instead of the hand dragging the body. (Pull stays tiny for a hint of connection.)
            arm.pull = 0.1f;
            arm.reach = 0.45f;
        }
    }

    private void CacheAnimatorInfo()
    {
        animatorParameters.Clear();
        upperLayerIndex = -1;
        if (animator.runtimeAnimatorController == null) return;

        foreach (AnimatorControllerParameter parameter in animator.parameters) animatorParameters.Add(parameter.name);
        upperLayerIndex = animator.GetLayerIndex(upperBodyLayerName);
        if (upperLayerIndex >= 0) animator.SetLayerWeight(upperLayerIndex, animationWeight);
    }

    private static void ResetEffector(IKEffector effector)
    {
        effector.positionWeight = 0f;
        effector.rotationWeight = 0f;
    }

    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = IsPunching ? Color.red : (IsBlocking ? Color.blue : (IsAiming ? Color.yellow : Color.cyan));
        Gizmos.DrawWireSphere(AimPoint, 0.06f);
        for (int i = 0; i < 2; i++)
        {
            if (fistValid[i]) Gizmos.DrawWireSphere(fistPos[i], 0.04f);
            if (punches[i].phase < Phase.Drive) continue;
            Gizmos.DrawLine(shoulders[i] != null ? shoulders[i].position : transform.position, punches[i].target);
            if (elbowGoals[i] != null) Gizmos.DrawWireSphere(elbowGoals[i].position, 0.03f);
        }
    }
}
