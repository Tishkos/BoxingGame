using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Blends single-frame <see cref="ReferencePose"/>s INTO the reference skeleton every frame — after the Animator,
/// before Final IK and before the physics puppet captures its muscle targets — weighted by punch intent per hand
/// (loaded / straight / hook / uppercut) with Guard filling the rest. Strike poses come in up to three HEIGHT
/// variants (Head / Body / Low) and are blended by where the punch is aimed, so a body hook and a head hook use
/// different authored anatomy. The poses never move the fist through space and never own timing: they reshape the
/// body toward a trained boxer's structure, while the virtual fist and Final IK produce the actual punch.
///
/// Blending happens in humanoid muscle space with a per-pose region mask: a pose built around the RIGHT hand
/// reshapes the right arm fully (Final IK then places the hand exactly), the torso partially, the LEFT arm strongly
/// toward its defensive placement, the head lightly, the legs not at all by default (Legs Animator owns them).
///
/// Intents whose pose slot is EMPTY are zeroed before smoothing (an absent pose must never steal weight from the
/// guard), and the guard breathes softly so the boxer is never a statue. Poses auto-load from
/// <c>Resources/ReferencePoses</c>. Debug: number keys preview poses, an overlay shows live weights.
/// </summary>
[RequireComponent(typeof(Animator))]
// LateUpdate order matters and is tight: Legs Animator is -7, IKExecutionOrder is 0. Sitting at -40 meant the
// poses were applied and then Legs Animator wrote the legs again a moment later, so anything a reference pose
// said about stance, weight shift or footwork was silently thrown away — only the upper body ever survived.
// At -6 the chain lands AFTER Legs Animator and still well before the IK solve, so the whole body follows.
[DefaultExecutionOrder(-6)]
public class ReferencePoseMixer : MonoBehaviour
{
    private enum Region { Arm, Torso, Head, Legs, None }

    /// <summary>The three aim-height variants of one strike shape. Any of them may be empty.</summary>
    [Serializable]
    public class PoseTriple
    {
        [Tooltip("Aimed at the head / face.")] public ReferencePose head;
        [Tooltip("Aimed at the torso.")] public ReferencePose body;
        [Tooltip("Aimed low (below the ribs).")] public ReferencePose low;

        public bool Any => Has(head) || Has(body) || Has(low);

        public ReferencePose Get(ReferencePose.TargetHeight h)
        {
            switch (h)
            {
                case ReferencePose.TargetHeight.Head: return head;
                case ReferencePose.TargetHeight.Low: return low;
                default: return body;
            }
        }

        public void Set(ReferencePose.TargetHeight h, ReferencePose pose)
        {
            switch (h)
            {
                case ReferencePose.TargetHeight.Head: head = pose; break;
                case ReferencePose.TargetHeight.Low: low = pose; break;
                default: body = pose; break;
            }
        }
    }

    [Serializable]
    public class HandPoses
    {
        [Tooltip("Coiled / prepared structure before this hand fires (optional, legacy single slot).")] public ReferencePose loaded;

        [Tooltip("THE WIND-UP LADDER, ordered LOW → HIGH. Authored load poses for this hand; the mixer blends " +
                 "continuously between the rungs by where the punch is aimed, so loading a body shot and loading " +
                 "a head shot are genuinely different shapes. Leave empty to have the chamber synthesised.")]
        public ReferencePose[] loadedLadder = new ReferencePose[0];

        [Tooltip("LEFT-AIM wind-up column, ordered LOW → HIGH. With the centre ladder and the right column this " +
                 "forms the 2D LOADING GRID: while holding a punch, the aim's up/down AND left/right position " +
                 "blends the wind-up between the authored poses by exact percentage. Four corner poses " +
                 "(left-down, left-up, right-down, right-up) are enough for the full live blend. Auto-filled " +
                 "from poses whose names carry Left/Right + Up/Down (e.g. 'LoadingLeftUp').")]
        public ReferencePose[] loadedLadderLeft = new ReferencePose[0];

        [Tooltip("RIGHT-AIM wind-up column, ordered LOW → HIGH (see the left column).")]
        public ReferencePose[] loadedLadderRight = new ReferencePose[0];

        /// <summary>The rungs that actually exist, low → high (the legacy single slot counts as one rung).</summary>
        public int LadderCount
        {
            get
            {
                int n = 0;
                if (loadedLadder != null)
                    foreach (ReferencePose p in loadedLadder) if (p != null && p.IsValid) n++;
                if (n == 0 && loaded != null && loaded.IsValid) n = 1;
                return n;
            }
        }

        /// <summary>Rung i, low → high, skipping empty entries.</summary>
        public ReferencePose Rung(int i)
        {
            if (loadedLadder != null)
            {
                int n = 0;
                foreach (ReferencePose p in loadedLadder)
                {
                    if (p == null || !p.IsValid) continue;
                    if (n == i) return p;
                    n++;
                }
                if (n > 0) return null;
            }
            return i == 0 ? loaded : null;
        }

        [Tooltip("Straight (jab / cross) striking structure, by aim height.")] public PoseTriple straight = new PoseTriple();
        [Tooltip("Hook striking structure, by aim height.")] public PoseTriple hook = new PoseTriple();
        [Tooltip("Uppercut striking structure, by aim height.")] public PoseTriple uppercut = new PoseTriple();

        public PoseTriple For(ReferencePose.Category category)
        {
            switch (category)
            {
                case ReferencePose.Category.Straight: return straight;
                case ReferencePose.Category.Hook: return hook;
                case ReferencePose.Category.Uppercut: return uppercut;
                default: return null;
            }
        }
    }

    [Header("Pose library (single-frame references)")]
    [Tooltip("Home structure: stance, both hands up, chin tucked (the head guard).")]
    public ReferencePose guard;
    [Tooltip("Low / body-protecting guard (R1).")]
    public ReferencePose guardBody;
    public HandPoses left = new HandPoses();
    public HandPoses right = new HandPoses();

    [Tooltip("Fill empty slots from Resources/<folder> at start (assets baked by the Reference Pose Baker).")]
    public bool autoLoadFromResources = true;
    public string resourcesFolder = "ReferencePoses";

    [Header("Region influence — how far a pose may reshape each region (0-1)")]
    [Tooltip("The striking arm of a hand pose: 1 = take the pose's arm shape fully; Final IK still puts the hand on the target.")]
    [Range(0f, 1f)] public float punchingArm = 1f;
    [Tooltip("The other arm of a hand pose, and both arms of Guard: defensive discipline.")]
    [Range(0f, 1f)] public float guardArm = 0.85f;
    [Tooltip("Hips/spine/chest: chest rotation, hip involvement, posture. Kept high — the torso leading is the " +
             "difference between throwing a punch and reaching with an arm.")]
    [Range(0f, 1f)] public float torso = 0.85f;
    [Tooltip("Neck/head: chin tuck. Kept light so LookAtIK can still track.")]
    [Range(0f, 1f)] public float head = 0.35f;
    [Tooltip("Legs/feet. The reference poses carry real stance, weight shift and footwork, so this is high: a " +
             "punch thrown off the back foot should LOOK like it. Only applies while a punch is live (it scales " +
             "with the chain's weight), so Legs Animator still owns the idle and the walking.")]
    [Range(0f, 1f)] public float legWork = 0.8f;

    [Header("Lead hand (the library is mirrored — the boxer is not)")]
    [Tooltip("The LEAD hand's poses are mirrors of the rear hand's, so their legwork describes the OTHER " +
             "stance: southpaw feet under an orthodox boxer — the visible stance flip when jabbing. This scales " +
             "how much of the lead poses' LEG work applies. Low = the feet stay orthodox through a jab.")]
    [Range(0f, 1f)] public float leadLegWork = 0.15f;

    [Tooltip("Same story for the lead pose's OTHER arm (the guard hand): mirrored, it describes a southpaw " +
             "guard. Low = the rear hand keeps its orthodox guard while the lead punches.")]
    [Range(0f, 1f)] public float leadOffArm = 0.35f;

    /// <summary>Which hand is the REAR (power) hand — 1 = right (orthodox). Set by the controller.</summary>
    public int RearHandIndex { get; set; } = 1;

    [Header("Guard")]
    [Tooltip("How much the Guard pose corrects the idle animation (0 = animation only, 1 = the authored guard exactly).")]
    [Range(0f, 1f)] public float guardBaseline = 0.5f;

    [Tooltip("The guard breathes: baseline swells by this much at ~15 breaths/min so the boxer is never a statue.")]
    [Range(0f, 0.2f)] public float guardBreathing = 0.08f;

    [Header("The punch chain (req.md §6, §8 — see PunchPoseChain)")]
    [Tooltip("How far the wind-up reaches PAST the guard, away from the strike. This is the whole difference " +
             "between a punch and a reach: 0 = no chamber at all (the old two-key blend), 0.5 = a compact " +
             "boxer's load, 1 = a telegraphed haymaker.")]
    [Range(0f, 1f)] public float chamberCoil = 0.5f;

    [Tooltip("How far the punch travels THROUGH the target past the strike pose. Small — this is follow-through, " +
             "not overswinging.")]
    [Range(0f, 0.6f)] public float followThrough = 0.22f;

    [Tooltip("The most any single joint may wind PAST the guard. Without this the extrapolation drives joints " +
             "into their anatomical stops — the elbow folds shut and the glove ends up held up beside the head. " +
             "Raise for a bigger telegraph, lower for a tighter one.")]
    [Range(0.05f, 0.6f)] public float maxCoilDeviation = 0.22f;

    [Tooltip("The least the torso must wind up, even when the strike pose was authored square-on. Your crosses " +
             "have almost no chest rotation, so without this their wind-up has nowhere to come from but the arm.")]
    [Range(0f, 0.5f)] public float torsoCoilFloor = 0.18f;

    [Tooltip("How much of the body the punch chain is allowed to own while it fires. Below 1 the animation " +
             "shows through; 1 means the authored punch anatomy wins outright.")]
    [Range(0f, 1f)] public float poseAuthority = 1f;

    [Tooltip("THE AUTHORED PIVOT. The punches were posed with the whole body rotating through the shot (the " +
             "guard stands bladed at ~+36°, a cross finishes at ~-71° — a ~108° hip pivot), and that rotation " +
             "is stored in each pose's body rotation. This is how much of it the punch applies: at 0 the old " +
             "behaviour (shapes only, which pointed every straight off to the boxer's own side); at 1 the " +
             "punch turns the body exactly as it was authored, and the fist faces the bag because the pivot " +
             "carries it there.")]
    [Range(0f, 1f)] public float pivotAuthority = 1f;

    [Tooltip("The most the body may pivot for any punch (degrees). The poses were authored rotating ~108° — " +
             "all the way through to the OPPOSITE blade, which read as the stance switching sides on every " +
             "cross. Capped here, a cross drives the rear shoulder to just past square and stops: full power " +
             "rotation, but the lead arm and lead foot stay the lead. ~50 is a textbook cross.")]
    [Range(10f, 120f)] public float maxPivot = 50f;

    [Tooltip("How much of the pivot the PELVIS takes; the rest is applied to the spine, which turns only the " +
             "upper body. The legs hang off the pelvis, so a pelvis that swings the full 50° of a cross reads " +
             "as the boxer switching from orthodox to southpaw. At 0.3 the hips still lead the shot, the chest " +
             "still finishes square, and the stance never changes.")]
    [Range(0f, 1f)] public float pelvisPivotShare = 0.3f;

    [Tooltip("Put the thighs back where the animation had them after the pelvis pivots, so the feet and the " +
             "stance never follow the punch. Untick only if you want the whole body to turn.")]
    public bool legsKeepStance = true;

    [Tooltip("Damping on the net pivot (seconds). Everything that turns the torso funnels through one damped " +
             "value, so a second punch, a released hold or a retake can only EASE the body — never snap it " +
             "sideways in a single frame (which read as the hand changing direction mid-punch).")]
    [Range(0.01f, 0.2f)] public float pivotSmoothTime = 0.05f;

    [Header("Response (seconds)")]
    [Tooltip("Blend time into the Loaded pose / guard poses — the wind-up should feel weighty.")]
    [Range(0.02f, 0.6f)] public float loadSmoothTime = 0.18f;
    [Tooltip("Blend time into a strike pose. Near-instant: the shape must arrive WITH the fist, not after it.")]
    [Range(0.01f, 0.4f)] public float strikeBlendTime = 0.03f;
    [Tooltip("Blend time back out of any pose (recovery).")]
    [Range(0.02f, 0.8f)] public float recoverSmoothTime = 0.25f;
    [Tooltip("Seconds for the aim-height blend (head/body/low variants) to follow the aim.")]
    [Range(0.02f, 0.3f)] public float heightSmoothTime = 0.07f;

    [Header("Debug")]
    [Tooltip("Number keys preview a pose at full weight: 1 Guard · 2 R Loaded · 3 R Hook · 4 L Loaded · 5 L Hook · 6 R Straight · 7 R Uppercut · 8 L Straight · 9 L Uppercut · 0 off.")]
    public bool previewKeys = true;
    [Tooltip("Show the live pose weights on screen.")]
    public bool showOverlay = true;

    // ------------------------------------------------------------------ Public state

    public float GuardWeight { get; private set; }
    public float GuardBodyWeight { get; private set; }
    public bool HasGuardPose => Has(guard);

    /// <summary>How deeply this hand is wound up right now (0-1); 0 once the punch has been released.</summary>
    public float LoadedWeight(int hand)
        => stage[hand] == PunchPoseChain.Stage.Coil ? chainWeight[hand] : 0f;

    /// <summary>How much of the body this hand's punch chain currently owns (0-1). Weights the elbow hints.</summary>
    public float StrikeInfluence(int hand) => Mathf.Clamp01(chainWeight[hand]);

    /// <summary>
    /// The authored body pivot of this hand's current punch (degrees, relative to the guard's stance yaw;
    /// a right cross is ~-108°). The controller checks this to stand its own generic twist down — when the
    /// poses carry the real pivot, a synthetic 14° on top would be a double turn.
    /// </summary>
    public float StrikeYaw(int hand) => chainYaw[Mathf.Clamp(hand, 0, 1)];

    /// <summary>Every punch has an authored shape now — the chain synthesises the chamber it winds up into.</summary>
    public bool HasChamber(int hand) => chains[hand].IsValid;

    public bool HasPoses => Has(guard) || HasAny(left) || HasAny(right);

    /// <summary>Does this hand have any pose of the given strike category (Loaded checks the loaded slot)?</summary>
    public bool HasType(int hand, ReferencePose.Category category)
    {
        HandPoses hp = hand == 0 ? left : right;
        if (category == ReferencePose.Category.Loaded) return hp.LadderCount > 0;
        PoseTriple triple = hp.For(category);
        return triple != null && triple.Any;
    }

    /// <summary>Name of the pose being previewed with the number keys, or null.</summary>
    public string PreviewName { get; private set; }

    /// <summary>Hold a pose exactly like the number keys do (1 Guard … 9, 10 = body guard). -1 = none. Set every frame by the controller for L1 / R1.</summary>
    public int Hold { get; set; } = -1;

    /// <summary>Hit-stop: while true the pose weights stop moving (the blend still applies) — the arm stays planted in the bag.</summary>
    public bool Frozen { get; set; }

    // ------------------------------------------------------------------ Private state

    /// <summary>A plain pose held at a weight — the guards. Punches are not slots any more; they are chains.</summary>
    private struct Slot
    {
        public ReferencePose pose;
        public float weight;
        public float[] mask;
    }

    private Animator animator;
    private HumanPoseHandler handler;
    private HumanPose pose;
    private float[] maskGuard, maskLeftHand, maskRightHand;
    private readonly List<Slot> slots = new List<Slot>(4);

    // One punch chain per hand: Guard → Chamber → Strike → Follow, sampled per body group (see PunchPoseChain).
    private readonly PunchPoseChain[] chains = { new PunchPoseChain(), new PunchPoseChain() };
    private readonly PunchPoseChain.Stage[] stage = new PunchPoseChain.Stage[2];
    private readonly float[] chainX = new float[2];
    private readonly float[] coilAmount = new float[2];
    private readonly float[] chainWeight = new float[2];
    private readonly float[] vChainWeight = new float[2];

    // What each chain was last built from, so it is rebuilt only when the punch's shape actually changes.
    private readonly Vector4[] builtFrom = { new Vector4(-1f, -1f, -1f, -1f), new Vector4(-1f, -1f, -1f, -1f) };

    // Scratch for blending the strike target across punch type and aim height before the chain is derived.
    private float[] blendValue, blendWeight;
    private bool[] blendAuthored;

    // The authored pivot: each strike pose's body yaw relative to the guard's, blended with the same weights
    // as the muscles. This is the rotation the mixer used to throw away — and it IS the punch's direction.
    private float blendYawSum, blendYawWeightSum;
    /// <summary>Injected by the controller: the boxer's seeded simulation stream (falls back to UnityEngine.Random).</summary>
    public SimRng Rng { get; set; }

    private float RngRange(float min, float max) => Rng != null ? Rng.Range(min, max) : UnityEngine.Random.Range(min, max);

    private readonly float[] chainYaw = new float[2];
    private readonly float[] chainLateral = new float[2]; // signed aim side (-1 left … +1 right) for the loading grid
    private readonly float[] builtLateral = new float[2];
    private readonly float[] releaseYaw = new float[2];   // yaw latched at the instant of release — one punch, one pivot
    private float appliedPivot, appliedPivotVelocity;     // the net pivot is DAMPED so it can never step in one frame
    private float guardYaw;
    private bool guardYawValid;

    // …and the same for the authored wind-up ladder.
    private float[] chamberValue, chamberWeight;
    private bool[] chamberAuthored;

    private int[] muscleGroup;
    private bool[] isLegMuscle;
    private int[] armSide;   // -1 = a left-arm muscle, +1 = right-arm, 0 = everything else
    private float vGuard, vGuardBody;
    private readonly Vector4[] lastMix = new Vector4[2];   // straight / hook / uppercut / height, for the overlay
    private int badMuscles;
    private float lastBadLog = -10f;

    private int preview = -1;               // -1 none, else 1..9
    private float overrideGuard, overrideGuardBody;   // set by the controller while L1 / R1 are held
    private GUIStyle overlayStyle;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        if (animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogWarning("ReferencePoseMixer: needs a Humanoid avatar.", this);
            enabled = false;
            return;
        }

        if (autoLoadFromResources) LoadFromResources();

        handler = new HumanPoseHandler(animator.avatar, transform);
        pose = new HumanPose();
        RebuildMasks();
    }

    private void OnDestroy()
    {
        handler?.Dispose();
    }

    /// <summary>Fill empty slots from Resources/<see cref="resourcesFolder"/> by category, hand and height.</summary>
    public void LoadFromResources()
    {
        ReferencePose[] found = Resources.LoadAll<ReferencePose>(resourcesFolder);
        int assigned = 0;
        List<ReferencePose> leftLoads = new List<ReferencePose>();
        List<ReferencePose> rightLoads = new List<ReferencePose>();
        // The 2D loading grid's side columns, per hand: [hand 0/1] × aim-left / aim-right.
        List<ReferencePose>[] aimLeftCol = { new List<ReferencePose>(), new List<ReferencePose>() };
        List<ReferencePose>[] aimRightCol = { new List<ReferencePose>(), new List<ReferencePose>() };
        foreach (ReferencePose p in found)
        {
            if (p == null || !p.IsValid) continue;
            if (p.category == ReferencePose.Category.Guard) { if (guard == null) { guard = p; assigned++; } continue; }
            if (p.category == ReferencePose.Category.GuardBody) { if (guardBody == null) { guardBody = p; assigned++; } continue; }

            HandPoses set = p.hand == BoxerPunchController.Hand.Left ? left : right;
            if (p.category == ReferencePose.Category.Loaded)
            {
                // A load pose whose name carries an AIM SIDE ("LoadingLeftUp", "LoadDownRight"…) belongs to a
                // side column of the 2D grid — and to BOTH hands, unless the name pins a hand ("...RightHand").
                int aimSide = AimSideOf(p.name, out bool explicitHand);
                if (aimSide != 0)
                {
                    List<ReferencePose>[] column = aimSide < 0 ? aimLeftCol : aimRightCol;
                    if (explicitHand) column[(int)p.hand].Add(p);
                    else { column[0].Add(p); column[1].Add(p); }
                    assigned++;
                    continue;
                }
                (p.hand == BoxerPunchController.Hand.Left ? leftLoads : rightLoads).Add(p);
                assigned++;
                continue;
            }
            PoseTriple triple = set.For(p.category);
            if (triple == null) continue;
            // Every strike pose also joins the variant pool — extra bakes of the same slot become random variants.
            int key = VariantKey(p.hand, p.category, p.height);
            if (!variantPool.TryGetValue(key, out List<ReferencePose> pool)) variantPool[key] = pool = new List<ReferencePose>();
            if (!pool.Contains(p)) pool.Add(p);
            if (triple.Get(p.height) == null) { triple.Set(p.height, p); assigned++; }
        }

        // The wind-up ladder, ordered LOW → HIGH. loadLevel is authoritative (the baker reads it off the file
        // name); anything without one falls back to alphabetical, so Load1..Load4 still land in order.
        if (left.LadderCount == 0) left.loadedLadder = SortLadder(leftLoads);
        if (right.LadderCount == 0) right.loadedLadder = SortLadder(rightLoads);
        if (ColumnCount(left.loadedLadderLeft) == 0) left.loadedLadderLeft = SortColumn(aimLeftCol[0]);
        if (ColumnCount(left.loadedLadderRight) == 0) left.loadedLadderRight = SortColumn(aimRightCol[0]);
        if (ColumnCount(right.loadedLadderLeft) == 0) right.loadedLadderLeft = SortColumn(aimLeftCol[1]);
        if (ColumnCount(right.loadedLadderRight) == 0) right.loadedLadderRight = SortColumn(aimRightCol[1]);
        if (found.Length > 0)
            Debug.Log($"ReferencePoseMixer: loaded {found.Length} pose(s) from Resources/{resourcesFolder}, assigned {assigned} to empty slots. " +
                      $"Guard:{Name(guard)} GuardBody:{Name(guardBody)} | L straight {TripleNames(left.straight)} hook {TripleNames(left.hook)} upper {TripleNames(left.uppercut)} | " +
                      $"R straight {TripleNames(right.straight)} hook {TripleNames(right.hook)} upper {TripleNames(right.uppercut)}", this);
    }

    /// <summary>
    /// Which SIDE of the loading grid a pose's name aims at: -1 left, +1 right, 0 centre/none. Hand tokens
    /// ("lefthand"/"righthand") and type words containing the letters ("uppercut", "lowerbody") are stripped
    /// first, so only a real aim word counts. Sets <paramref name="explicitHand"/> when the name pinned a hand.
    /// </summary>
    private static int AimSideOf(string poseName, out bool explicitHand)
    {
        string s = Compact(poseName);
        explicitHand = s.Contains("lefthand") || s.Contains("righthand");
        s = s.Replace("lefthand", "").Replace("righthand", "").Replace("uppercut", "").Replace("lowerbody", "");
        bool l = s.Contains("left");
        bool r = s.Contains("right");
        if (l == r) return 0;
        return l ? -1 : 1;
    }

    private static string Compact(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Order a grid column bottom → top by the name's vertical word; loadLevel and name break ties.</summary>
    private static ReferencePose[] SortColumn(List<ReferencePose> loads)
    {
        if (loads.Count == 0) return new ReferencePose[0];
        loads.Sort((a, b) =>
        {
            int va = VerticalRank(a.name), vb = VerticalRank(b.name);
            if (va != vb) return va.CompareTo(vb);
            int la = a.loadLevel >= 0 ? a.loadLevel : int.MaxValue;
            int lb = b.loadLevel >= 0 ? b.loadLevel : int.MaxValue;
            return la != lb ? la.CompareTo(lb) : string.CompareOrdinal(a.name, b.name);
        });
        return loads.ToArray();
    }

    private static int VerticalRank(string poseName)
    {
        string s = Compact(poseName).Replace("uppercut", "");
        if (s.Contains("down") || s.Contains("low") || s.Contains("bottom")) return 0;
        if (s.Contains("up") || s.Contains("high") || s.Contains("top") || s.Contains("head") || s.Contains("face")) return 2;
        return 1;
    }

    private static ReferencePose[] SortLadder(List<ReferencePose> loads)
    {
        if (loads.Count == 0) return new ReferencePose[0];
        loads.Sort((a, b) =>
        {
            int la = a.loadLevel >= 0 ? a.loadLevel : int.MaxValue;
            int lb = b.loadLevel >= 0 ? b.loadLevel : int.MaxValue;
            return la != lb ? la.CompareTo(lb) : string.CompareOrdinal(a.name, b.name);
        });
        return loads.ToArray();
    }

    private static string Name(ReferencePose p) => p != null ? p.name : "-";
    private static string TripleNames(PoseTriple t) => $"[{Name(t.head)}/{Name(t.body)}/{Name(t.low)}]";

    private readonly Dictionary<int, List<ReferencePose>> variantPool = new Dictionary<int, List<ReferencePose>>();

    private static int VariantKey(BoxerPunchController.Hand hand, ReferencePose.Category category, ReferencePose.TargetHeight height)
        => ((int)hand * 97 + (int)category) * 7 + (int)height;

    /// <summary>
    /// Pick a random authored variant of this hand+category for every height slot that has more than one bake
    /// (e.g. RightHookBody + RightHookBody2). Called per throw — no two punches look identical.
    /// </summary>
    /// <summary>
    /// Per-throw jitter, rolled once when a punch is released. Two punches of the same type at the same height
    /// were previously the same shape to the last decimal, which is what makes a flurry look mechanical: this
    /// varies how deep the wind-up loads, how far it follows through, and nudges the aim height enough to pull a
    /// different mix of the authored variants each time. Small numbers — it is texture, not randomness.
    /// </summary>
    [Header("Variation")]
    [Tooltip("How much each throw differs from the last: coil depth, follow-through and aim height. 0 = every " +
             "punch identical, 1 = visibly loose.")]
    [Range(0f, 1f)] public float variation = 0.3f;

    private readonly float[] coilJitter = new float[2];
    private readonly float[] followJitter = new float[2];
    private readonly float[] heightJitter = new float[2];

    /// <summary>Roll this hand's variation for the punch about to be thrown.</summary>
    public void RollVariation(int hand)
    {
        coilJitter[hand] = RngRange(-0.22f, 0.22f) * variation;
        followJitter[hand] = RngRange(-0.10f, 0.14f) * variation;
        heightJitter[hand] = RngRange(-0.13f, 0.13f) * variation;
        builtFrom[hand] = new Vector4(-1f, -1f, -1f, -1f);   // force the chain to rebuild with the new jitter
    }

    /// <summary>
    /// The variation engine hands this hand its per-throw jitter directly (overriding <see cref="RollVariation"/>'s
    /// own roll): coil depth, follow-through and aim height offsets for the punch about to fire.
    /// </summary>
    public void SetThrowJitter(int hand, float coil, float follow, float height)
    {
        coilJitter[hand] = coil;
        followJitter[hand] = follow;
        heightJitter[hand] = height;
        builtFrom[hand] = new Vector4(-1f, -1f, -1f, -1f);   // rebuild the chain with the new style
    }

    public void RollVariants(int hand, ReferencePose.Category category)
    {
        RollVariation(hand);
        HandPoses hp = hand == 0 ? left : right;
        PoseTriple triple = hp.For(category);
        if (triple == null) return;
        for (int h = 0; h < 3; h++)
        {
            ReferencePose.TargetHeight height = (ReferencePose.TargetHeight)h;
            int key = VariantKey((BoxerPunchController.Hand)hand, category, height);
            if (variantPool.TryGetValue(key, out List<ReferencePose> pool) && pool.Count > 1)
                triple.Set(height, pool[Rng != null ? Rng.Range(0, pool.Count) : UnityEngine.Random.Range(0, pool.Count)]);
        }
    }

    /// <summary>Hold the full Guard pose (L1) or the body guard (R1). 0 = release.</summary>
    public void SetGuardOverride(float guardWeight, float bodyGuardWeight)
    {
        overrideGuard = Mathf.Clamp01(guardWeight);
        overrideGuardBody = Mathf.Clamp01(bodyGuardWeight);
    }

    /// <summary>
    /// Tell a hand what it is doing. This replaces the old four-weights-and-hope intent: the punch's SHAPE (which
    /// types, which aim height) and its CLOCK are now separate things, because they are separate things.
    ///
    ///  • <paramref name="punchStage"/> — coiling into the chamber, or released and unwinding.
    ///  • <paramref name="x"/> — the punch clock, 0 chamber … 1 full strike. Drives the kinetic chain.
    ///  • <paramref name="coil"/> — how deep the wind-up is while holding, 0-1.
    ///  • straight / hook / uppercut — the continuous type mix; a stick between two of them throws a real hybrid.
    ///  • <paramref name="height"/> — 0 low · 0.5 body · 1 head, blending the authored height variants.
    ///
    /// Types with no authored pose are dropped, and if a hand ends up with nothing authored the chain goes
    /// inactive and the guard simply keeps the body — an absent pose can never collapse the stance.
    /// </summary>
    public void SetPunch(int hand, PunchPoseChain.Stage punchStage, float x, float coil,
                         float straight, float hook, float uppercut, float height, float lateral = 0f)
    {
        HandPoses hp = hand == 0 ? left : right;

        float s = hp.straight.Any ? Mathf.Clamp01(straight) : 0f;
        float k = hp.hook.Any ? Mathf.Clamp01(hook) : 0f;
        float u = hp.uppercut.Any ? Mathf.Clamp01(uppercut) : 0f;
        float total = s + k + u;
        if (total <= 0.0001f)
        {
            // Nothing authored for what the player asked for. Fall back to whatever this hand DOES have, so a
            // punch still has a shape instead of dropping to a bare IK reach.
            if (hp.straight.Any) s = 1f;
            else if (hp.hook.Any) k = 1f;
            else if (hp.uppercut.Any) u = 1f;
            else { stage[hand] = PunchPoseChain.Stage.None; return; }
            total = 1f;
        }
        s /= total; k /= total; u /= total;
        height = Mathf.Clamp01(height + heightJitter[hand]);

        lastMix[hand] = new Vector4(s, k, u, height);
        PunchPoseChain.Stage was = stage[hand];
        stage[hand] = punchStage;
        chainX[hand] = Mathf.Clamp01(x);
        coilAmount[hand] = Mathf.Clamp01(coil);

        chainLateral[hand] = Mathf.Clamp(lateral, -1f, 1f);

        // Rebuilding the keys is only needed when the SHAPE changes (type mix, aim height, aim side) — not
        // every frame of a punch.
        Vector4 key = new Vector4(s, k, u, height);
        if (!chains[hand].IsValid || (key - builtFrom[hand]).sqrMagnitude > 0.0004f
            || Mathf.Abs(chainLateral[hand] - builtLateral[hand]) > 0.02f)
        {
            if (BuildStrikeBlend(hp, s, k, u, height))
            {
                // The authored pivot rides with the shape: same poses, same weights.
                chainYaw[hand] = blendYawWeightSum > 0.0005f ? blendYawSum / blendYawWeightSum : 0f;

                // The wind-up ladder is blended by the SAME aim height as the strike, so the load you see while
                // holding is the load for the punch you are about to throw.
                bool posedChamber = BuildChamberBlend(hp, height, chainLateral[hand]);
                chains[hand].Rebuild(guard, blendValue, blendAuthored,
                                     posedChamber ? chamberValue : null,
                                     posedChamber ? chamberAuthored : null,
                                     Mathf.Clamp01(chamberCoil + coilJitter[hand]),
                                     Mathf.Max(0f, followThrough + followJitter[hand]),
                                     maxCoilDeviation, torsoCoilFloor, hand == 0 ? -1 : 1);
                builtFrom[hand] = key;
                builtLateral[hand] = chainLateral[hand];
            }
        }

        // A PUNCH FLIES WITH ONE PIVOT. The yaw is latched the moment the hand is released; live blend updates
        // (a retake reshaping mid-recovery, a hybrid drifting between types) can no longer re-aim a punch that
        // is already in the air — that was the hand suddenly heading the other way in the middle of a punch.
        if (punchStage == PunchPoseChain.Stage.Release && was != PunchPoseChain.Stage.Release)
            releaseYaw[hand] = chainYaw[hand];
    }

    /// <summary>Stop driving this hand; the guard takes the body back.</summary>
    public void ClearPunch(int hand) => stage[hand] = PunchPoseChain.Stage.None;

    /// <summary>
    /// Mix the authored strike poses of one hand into a single target shape: across punch type by the stick's
    /// continuous blend, and across the three aim heights. Per muscle, only the poses that actually authored it
    /// contribute, so a pose library with gaps degrades smoothly instead of dragging muscles toward zero.
    /// </summary>
    private bool BuildStrikeBlend(HandPoses hp, float straight, float hook, float uppercut, float height)
    {
        int count = HumanTrait.MuscleCount;
        if (blendValue == null || blendValue.Length != count)
        {
            blendValue = new float[count];
            blendWeight = new float[count];
            blendAuthored = new bool[count];
        }
        Array.Clear(blendValue, 0, count);
        Array.Clear(blendWeight, 0, count);
        Array.Clear(blendAuthored, 0, count);
        blendYawSum = 0f;
        blendYawWeightSum = 0f;

        bool any = false;
        any |= AccumulateTriple(hp.straight, straight, height);
        any |= AccumulateTriple(hp.hook, hook, height);
        any |= AccumulateTriple(hp.uppercut, uppercut, height);
        if (!any) return false;

        for (int m = 0; m < count; m++)
        {
            if (blendWeight[m] <= 0.0001f) continue;
            blendValue[m] /= blendWeight[m];
            blendAuthored[m] = true;
        }
        return true;
    }

    /// <summary>Add one punch type's height variants into the strike blend.</summary>
    private bool AccumulateTriple(PoseTriple triple, float weight, float height)
    {
        if (triple == null || weight <= 0.001f) return false;

        float sHead = Mathf.Clamp01(2f * height - 1f);
        float sLow = Mathf.Clamp01(1f - 2f * height);
        float sBody = Mathf.Clamp01(1f - sHead - sLow);

        if (!Has(triple.head)) sHead = 0f;
        if (!Has(triple.body)) sBody = 0f;
        if (!Has(triple.low)) sLow = 0f;
        float sum = sHead + sBody + sLow;
        if (sum <= 0.0001f)
        {
            // No variant in this height band — use the nearest one that exists rather than nothing.
            ReferencePose fallback = height >= 0.5f
                ? (Has(triple.head) ? triple.head : (Has(triple.body) ? triple.body : triple.low))
                : (Has(triple.low) ? triple.low : (Has(triple.body) ? triple.body : triple.head));
            return Accumulate(fallback, weight);
        }

        bool any = false;
        any |= Accumulate(triple.head, weight * sHead / sum);
        any |= Accumulate(triple.body, weight * sBody / sum);
        any |= Accumulate(triple.low, weight * sLow / sum);
        return any;
    }

    /// <summary>
    /// Blend the authored wind-up ladder for one hand at this aim height. Four rungs spread across 0-1 give
    /// positions 0, ⅓, ⅔, 1; an aim between two rungs mixes exactly those two, so the load slides continuously
    /// from the low wind-up to the high one instead of snapping between them. Returns false when the hand has no
    /// authored loads at all, and the chain synthesises its chamber as before.
    /// </summary>
    /// <summary>
    /// THE 2D LOADING GRID. The wind-up blends across up to three authored COLUMNS — aim-left, centre and
    /// aim-right, each a low→high ladder — by where the punch is aimed on BOTH axes. Four corner poses
    /// (left-down, left-up, right-down, right-up) already give the full live bilinear blend: drag the aim from
    /// left-up toward left-down and the chamber follows by exact percentage. Columns that do not exist hand
    /// their share to the ones that do, so any subset of authored poses still blends cleanly.
    /// </summary>
    private bool BuildChamberBlend(HandPoses hp, float height, float lateral)
    {
        int count = HumanTrait.MuscleCount;
        if (chamberValue == null || chamberValue.Length != count)
        {
            chamberValue = new float[count];
            chamberWeight = new float[count];
            chamberAuthored = new bool[count];
        }
        Array.Clear(chamberValue, 0, count);
        Array.Clear(chamberWeight, 0, count);
        Array.Clear(chamberAuthored, 0, count);

        float u = Mathf.Clamp(lateral, -1f, 1f);
        float wLeft = ColumnCount(hp.loadedLadderLeft) > 0 ? Mathf.Max(0f, -u) : 0f;
        float wRight = ColumnCount(hp.loadedLadderRight) > 0 ? Mathf.Max(0f, u) : 0f;
        float wCentre = hp.LadderCount > 0 ? 1f - Mathf.Abs(u) : 0f;

        // No centre column authored? The two side columns share the middle between them — no dead zone.
        if (hp.LadderCount == 0 && wLeft + wRight > 0f && ColumnCount(hp.loadedLadderLeft) > 0 && ColumnCount(hp.loadedLadderRight) > 0)
        {
            wLeft = Mathf.Clamp01(0.5f - 0.5f * u);
            wRight = 1f - wLeft;
        }

        float sum = wLeft + wCentre + wRight;
        if (sum <= 0.0001f) return false;

        bool any = false;
        if (wLeft > 0.0005f) any |= AccumulateColumn(hp.loadedLadderLeft, ColumnCount(hp.loadedLadderLeft), wLeft / sum, height, false, hp);
        if (wRight > 0.0005f) any |= AccumulateColumn(hp.loadedLadderRight, ColumnCount(hp.loadedLadderRight), wRight / sum, height, false, hp);
        if (wCentre > 0.0005f) any |= AccumulateColumn(null, hp.LadderCount, wCentre / sum, height, true, hp);
        if (!any) return false;

        for (int m = 0; m < count; m++)
        {
            if (chamberWeight[m] <= 0.0001f) continue;
            chamberValue[m] /= chamberWeight[m];
            chamberAuthored[m] = true;
        }
        return true;
    }

    /// <summary>Vertical blend inside one column (low → high), weighted by that column's lateral share.</summary>
    private bool AccumulateColumn(ReferencePose[] column, int rungs, float weight, float height, bool centre, HandPoses hp)
    {
        if (rungs <= 0) return false;
        float position = Mathf.Clamp01(height) * (rungs - 1);
        int lower = Mathf.Clamp(Mathf.FloorToInt(position), 0, rungs - 1);
        int upper = Mathf.Min(lower + 1, rungs - 1);
        float t = position - lower;

        ReferencePose lowPose = centre ? hp.Rung(lower) : ColumnRung(column, lower);
        ReferencePose highPose = centre ? hp.Rung(upper) : ColumnRung(column, upper);
        bool any = AccumulateInto(lowPose, weight * (1f - t), chamberValue, chamberWeight);
        if (upper != lower) any |= AccumulateInto(highPose, weight * t, chamberValue, chamberWeight);
        return any;
    }

    /// <summary>Valid rungs in a grid column.</summary>
    private static int ColumnCount(ReferencePose[] column)
    {
        int n = 0;
        if (column != null) foreach (ReferencePose p in column) if (p != null && p.IsValid) n++;
        return n;
    }

    /// <summary>Rung i of a column, low → high, skipping empty entries.</summary>
    private static ReferencePose ColumnRung(ReferencePose[] column, int i)
    {
        if (column == null) return null;
        foreach (ReferencePose p in column)
        {
            if (p == null || !p.IsValid) continue;
            if (i == 0) return p;
            i--;
        }
        return null;
    }

    private bool Accumulate(ReferencePose p, float weight)
    {
        bool added = AccumulateInto(p, weight, blendValue, blendWeight);
        if (added)
        {
            blendYawSum += RelativeYaw(p) * weight;
            blendYawWeightSum += weight;
        }
        return added;
    }

    /// <summary>Yaw of a pose's authored body rotation (degrees about world up).</summary>
    private static float YawOf(ReferencePose p)
    {
        Vector3 forward = p.bodyRotation * Vector3.forward;
        return forward.sqrMagnitude > 1e-6f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : 0f;
    }

    /// <summary>
    /// How far a pose's body is turned from the GUARD's stance (degrees). The guard stands bladed (~+36°); a
    /// right cross was authored finishing at ~-71° — the difference (~-108°) IS the punch's pivot.
    /// </summary>
    private float RelativeYaw(ReferencePose p)
    {
        if (!guardYawValid)
        {
            guardYaw = Has(guard) ? YawOf(guard) : 0f;
            guardYawValid = true;
        }
        float yaw = Mathf.DeltaAngle(guardYaw, YawOf(p));
        return float.IsNaN(yaw) || float.IsInfinity(yaw) ? 0f : yaw;
    }

    private static bool AccumulateInto(ReferencePose p, float weight, float[] values, float[] weights)
    {
        if (!Has(p) || weight <= 0.0005f) return false;
        float[] poseValues = p.Resolved;
        bool[] poseAuthored = p.Authored;
        for (int m = 0; m < values.Length; m++)
        {
            if (!poseAuthored[m]) continue;
            values[m] += poseValues[m] * weight;
            weights[m] += weight;
        }
        return true;
    }

    private void Update()
    {
        if (!previewKeys) { preview = -1; return; }
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        if (kb.digit0Key.wasPressedThisFrame) preview = -1;
        for (int i = 1; i <= 9; i++)
            if (kb[(Key)((int)Key.Digit1 + i - 1)].wasPressedThisFrame) preview = preview == i ? -1 : i;
    }

    /// <summary>
    /// Something else owns the update order. PuppetMaster in Animate Physics drives Animator → IK → Read inside
    /// FixedUpdate, so a LateUpdate here would pose the body AFTER the puppet had already read it — the poses
    /// would be a frame stale and then overwritten by the mapping. <see cref="BoxerPhysics"/> sets this and calls
    /// <see cref="Apply"/> ahead of the solvers instead.
    /// </summary>
    public bool ExternalUpdate { get; set; }

    private void LateUpdate()
    {
        if (!ExternalUpdate) Apply(Time.deltaTime);
    }

    /// <summary>Pose the body for this step. Must run AFTER the Animator and BEFORE the IK solve.</summary>
    public void Apply(float dt)
    {
        if (handler == null || !isActiveAndEnabled) return;

        // Preview / hold overrides intent: the chosen pose at full weight, everything else off (L1 = exactly key 1).
        int active = preview > 0 ? preview : Hold;
        if (active <= 0 && overrideGuardBody > 0.5f) active = 10;
        if (active <= 0 && overrideGuard > 0.5f) active = 1;
        bool previewing = active > 0;
        PreviewName = previewing ? PreviewLabel(active) : null;
        if (previewing) ApplyPreview(active);

        // How much of the body each hand's punch chain is taking. It rises fast (the shape has to arrive WITH the
        // fist, not after it) and falls at the recovery rate — the fade back out IS the recovery.
        float punchTotal = 0f;
        for (int h = 0; h < 2; h++)
        {
            // The coil's DEPTH is carried by the shape (guard → chamber), never by the weight as well — applying
            // it in both places quietly halved the wind-up, which is part of why it read as no wind-up at all.
            float target = chains[h].IsValid && stage[h] != PunchPoseChain.Stage.None ? poseAuthority : 0f;

            if (!Frozen)
                chainWeight[h] = Smooth(chainWeight[h], target, ref vChainWeight[h],
                                        stage[h] == PunchPoseChain.Stage.Coil ? loadSmoothTime : strikeBlendTime,
                                        recoverSmoothTime, dt);
            punchTotal += chainWeight[h];
        }

        bool bodyGuardActive = active == 10 && Has(guardBody);
        float breathe = guardBreathing * 0.5f * (1f + Mathf.Sin(Time.time * 1.55f));   // ~15 breaths a minute
        float guardTarget = previewing
            ? ((active == 1 || (active == 10 && !Has(guardBody))) ? 1f : 0f)
            : Mathf.Clamp01(guardBaseline + breathe) * (1f - Mathf.Min(1f, punchTotal));
        float bodyGuardTarget = bodyGuardActive ? 1f : 0f;
        if (!Frozen)
        {
            GuardWeight = Smooth(GuardWeight, guardTarget, ref vGuard, loadSmoothTime, recoverSmoothTime, dt);
            GuardBodyWeight = Smooth(GuardBodyWeight, bodyGuardTarget, ref vGuardBody, loadSmoothTime, recoverSmoothTime, dt);
        }

        slots.Clear();
        AddSlot(guard, GuardWeight, maskGuard);
        AddSlot(guardBody, GuardBodyWeight, maskGuard);
        if (slots.Count == 0 && punchTotal <= 0.002f) return;

        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Vector3 hipsPos = hips != null ? hips.position : Vector3.zero;
        Quaternion hipsRot = hips != null ? hips.rotation : Quaternion.identity;

        handler.GetHumanPose(ref pose);
        int count = HumanTrait.MuscleCount;
        for (int m = 0; m < count; m++)
        {
            // Per muscle: a weighted average of the guards that authored it plus whatever each hand's punch chain
            // says it should be right now, then lerp the animation toward that by the total masked weight (never
            // more than 1, so nothing can over-add).
            float sumW = 0f, sumV = 0f;

            for (int s = 0; s < slots.Count; s++)
            {
                ReferencePose p = slots[s].pose;
                if (!p.Authored[m]) continue;
                float w = slots[s].weight * slots[s].mask[m];
                if (w <= 0f) continue;
                sumW += w;
                sumV += w * p.Resolved[m];
            }

            int group = muscleGroup != null ? muscleGroup[m] : -1;
            for (int h = 0; h < 2; h++)
            {
                if (chainWeight[h] <= 0.002f || !chains[h].Drives(m)) continue;
                float w = chainWeight[h] * (h == 0 ? maskLeftHand[m] : maskRightHand[m]);

                // LEAD-HAND REALITY CHECK. The lead library is a MIRROR of the rear one, so its legwork and its
                // guard arm describe the OTHER stance — southpaw feet and a southpaw guard under an orthodox
                // boxer, which read as the stance flipping every jab. The lead punch keeps its mirrored ARM and
                // torso; the legs and the rear guard stay home.
                if (h != RearHandIndex)
                {
                    if (isLegMuscle[m]) w *= leadLegWork;
                    else if (armSide[m] != 0 && armSide[m] != (h == 0 ? -1 : 1)) w *= leadOffArm;
                }

                if (w <= 0f) continue;
                sumW += w;
                sumV += w * chains[h].Sample(m, group, stage[h], chainX[h], coilAmount[h]);
            }

            if (sumW <= 0.0001f) continue;

            // LAST LINE OF DEFENCE. One NaN muscle makes SetHumanPose write NaN bones, which makes the mesh
            // bounds invalid, which makes the renderer cull the character entirely — he simply disappears, and
            // the only clue is a wall of "Invalid AABB" assertions. Whatever is upstream, it does not get to do
            // that: a value that is not a real number is dropped and the muscle keeps its animated value.
            float target = sumV / sumW;
            if (float.IsNaN(target) || float.IsInfinity(target)) { badMuscles++; continue; }
            pose.muscles[m] = Mathf.Lerp(pose.muscles[m], target, Mathf.Min(1f, sumW));
        }

        if (badMuscles > 0 && Time.unscaledTime - lastBadLog > 5f)
        {
            lastBadLog = Time.unscaledTime;
            Debug.LogWarning($"ReferencePoseMixer: dropped {badMuscles} non-finite muscle value(s) this frame — " +
                             "something upstream produced NaN (check the punch clock and the pose assets). The " +
                             "character is being kept visible, but the punch shape is wrong until it is fixed.", this);
        }
        badMuscles = 0;

        handler.SetHumanPose(ref pose);
        if (hips != null) hips.SetPositionAndRotation(hipsPos, hipsRot);   // poses shape the body; they never TRANSLATE it

        // THE AUTHORED PIVOT. The punches were posed with the whole body rotating through the shot (guard
        // bladed ~+36°, a cross finishing ~-71° — a ~108° hip pivot). Discarding it left only the arm shapes,
        // and without the pivot a right straight points off to the boxer's own right — which is exactly what
        // the previews and the misses showed. The pivot fires on the HIPS' window of the punch clock (first to
        // move, done before the fist flies), coils slightly the other way during a hold, and unwinds with the
        // chain weight during recovery. Feet are captured and pinned AFTER this runs, so they stay planted the
        // way the authored footwork put them.
        if (hips != null && pivotAuthority > 0.001f)
        {
            float pivot = 0f;
            for (int h = 0; h < 2; h++)
            {
                if (chainWeight[h] <= 0.002f || !chains[h].IsValid) continue;
                // Capped: the authored rotation runs through to the opposite blade (~108°); the punch takes the
                // FIRST part of it — the drive to square — and never switches the stance. A released punch uses
                // the yaw it was LATCHED with; only a held coil follows the live blend.
                float yaw = Mathf.Clamp(stage[h] == PunchPoseChain.Stage.Release ? releaseYaw[h] : chainYaw[h],
                                        -maxPivot, maxPivot);
                if (float.IsNaN(yaw) || Mathf.Abs(yaw) < 0.01f) continue;

                float progress;
                if (stage[h] == PunchPoseChain.Stage.Coil)
                    progress = -0.25f * Mathf.Clamp01(coilAmount[h]);   // the blade deepens into the load
                else if (stage[h] == PunchPoseChain.Stage.Release)
                    progress = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.55f, chainX[h]));
                else continue;

                pivot += yaw * progress * Mathf.Clamp01(chainWeight[h]) * pivotAuthority;
            }

            // ONLY THE UPPER BODY TURNS. The pelvis leads the shot with a share of the pivot and the spine
            // carries the rest, so the torso still finishes square — but the thighs are restored to the
            // rotation the animation gave them, so the feet, the knees and the STANCE never follow the punch.
            // (A pelvis taking the whole authored rotation is what read as orthodox flipping to southpaw.)
            // THE NET PIVOT IS DAMPED. Both hands (and a hold's counter-coil) feed one number, and any of them
            // can appear or vanish between frames — a landed punch, a guard hand starting to coil, a retake.
            // Damped, those become a fast ease instead of the torso (and the fist riding it) jerking sideways.
            if (float.IsNaN(pivot)) pivot = 0f;
            appliedPivot = Mathf.SmoothDamp(appliedPivot, pivot, ref appliedPivotVelocity, pivotSmoothTime);

            if (Mathf.Abs(appliedPivot) > 0.01f)
            {
                float pelvisPart = appliedPivot * Mathf.Clamp01(pelvisPivotShare);
                float spinePart = appliedPivot - pelvisPart;

                if (Mathf.Abs(pelvisPart) > 0.01f)
                {
                    Transform leftLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                    Transform rightLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                    Quaternion leftWas = leftLeg != null ? leftLeg.rotation : Quaternion.identity;
                    Quaternion rightWas = rightLeg != null ? rightLeg.rotation : Quaternion.identity;

                    hips.rotation = Quaternion.AngleAxis(pelvisPart, Vector3.up) * hips.rotation;

                    if (legsKeepStance)
                    {
                        if (leftLeg != null) leftLeg.rotation = leftWas;
                        if (rightLeg != null) rightLeg.rotation = rightWas;
                    }
                }

                Transform spineBone = animator.GetBoneTransform(HumanBodyBones.Spine);
                if (spineBone != null && Mathf.Abs(spinePart) > 0.01f)
                    spineBone.rotation = Quaternion.AngleAxis(spinePart, Vector3.up) * spineBone.rotation;
            }
        }
    }

    /// <summary>
    /// Number keys / L1 / R1: hold one authored shape still so it can be judged. A strike preview parks its
    /// chain at full extension, so what you are looking at is the real strike key, not an approximation of it.
    /// </summary>
    private void ApplyPreview(int active)
    {
        for (int h = 0; h < 2; h++) stage[h] = PunchPoseChain.Stage.None;

        int hand;
        ReferencePose.Category category;
        switch (active)
        {
            case 2: hand = 1; category = ReferencePose.Category.Loaded; break;
            case 3: hand = 1; category = ReferencePose.Category.Hook; break;
            case 4: hand = 0; category = ReferencePose.Category.Loaded; break;
            case 5: hand = 0; category = ReferencePose.Category.Hook; break;
            case 6: hand = 1; category = ReferencePose.Category.Straight; break;
            case 7: hand = 1; category = ReferencePose.Category.Uppercut; break;
            case 8: hand = 0; category = ReferencePose.Category.Straight; break;
            case 9: hand = 0; category = ReferencePose.Category.Uppercut; break;
            default: return;   // 1 = guard, 10 = body guard: handled by the guard weights alone
        }

        // "Loaded" now means the synthesised chamber of that hand's straight — there is no authored load pose,
        // and this is exactly what the punch will wind up into.
        // A preview shows the AUTHORED KEY: jitters zeroed and follow-through parked at 0 for the rebuild, so
        // what you judge on screen is the strike itself, not a jittered take extrapolated past it.
        bool chamberPreview = category == ReferencePose.Category.Loaded;
        SetThrowJitter(hand, 0f, 0f, 0f);
        float keepThrough = followThrough;
        followThrough = 0f;
        SetPunch(hand,
                 chamberPreview ? PunchPoseChain.Stage.Coil : PunchPoseChain.Stage.Release,
                 1f, 1f,
                 chamberPreview || category == ReferencePose.Category.Straight ? 1f : 0f,
                 category == ReferencePose.Category.Hook ? 1f : 0f,
                 category == ReferencePose.Category.Uppercut ? 1f : 0f,
                 0.5f);
        followThrough = keepThrough;
    }

    /// <summary>Rising weights use the rising time; falling weights the falling time — poses arrive with intent and leave with weight.</summary>
    private static float Smooth(float current, float target, ref float velocity, float risingTime, float fallingTime, float dt)
    {
        float time = target > current ? risingTime : fallingTime;
        return Mathf.SmoothDamp(current, target, ref velocity, time, Mathf.Infinity, dt);
    }

    private static string PreviewLabel(int key)
    {
        switch (key)
        {
            case 1: return "Guard";
            case 2: return "Right Loaded";
            case 3: return "Right Hook";
            case 4: return "Left Loaded";
            case 5: return "Left Hook";
            case 6: return "Right Straight";
            case 7: return "Right Uppercut";
            case 8: return "Left Straight";
            case 9: return "Left Uppercut";
            case 10: return "Body Guard";
            default: return null;
        }
    }

    private void AddSlot(ReferencePose p, float weight, float[] mask)
    {
        if (!Has(p) || weight <= 0.002f) return;
        slots.Add(new Slot { pose = p, weight = weight, mask = mask });
    }

    private static bool Has(ReferencePose p) => p != null && p.IsValid;
    private static bool HasAny(HandPoses h) => h != null && (h.LadderCount > 0 || h.straight.Any || h.hook.Any || h.uppercut.Any);

    // ------------------------------------------------------------------ Overlay

    private void OnGUI()
    {
        if (!showOverlay || !Application.isPlaying || handler == null) return;
        if (overlayStyle == null)
        {
            overlayStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
            overlayStyle.normal.textColor = Color.white;
        }

        float x = 12f, y = 12f, w = 260f;
        GUI.Box(new Rect(x - 6f, y - 6f, w + 12f, 226f), GUIContent.none);
        string header = PreviewName != null ? $"<b>Reference poses</b>  — preview: <color=#ffd54a>{PreviewName}</color>" : "<b>Reference poses</b>  (1-9 preview · 0 off)";
        GUI.Label(new Rect(x, y, w, 20f), header, overlayStyle);
        y += 20f;

        Row(ref y, x, w, "Guard", GuardWeight, Has(guard));
        Row(ref y, x, w, "Guard Body", GuardBodyWeight, Has(guardBody));
        Row(ref y, x, w, "L chain", chainWeight[0], HasAny(left));
        Row(ref y, x, w, "R chain", chainWeight[1], HasAny(right));
        y += 4f;
        GUI.Label(new Rect(x, y, w, 18f), ChainLine(0), overlayStyle); y += 18f;
        GUI.Label(new Rect(x, y, w, 18f), ChainLine(1), overlayStyle); y += 18f;
        GUI.Label(new Rect(x, y, w, 18f), $"coil {chamberCoil:0.00} · through {followThrough:0.00}", overlayStyle);
    }

    /// <summary>One line per hand: which stage the chain is in and where along it, so a bad punch is readable.</summary>
    private string ChainLine(int hand)
    {
        string side = hand == 0 ? "L" : "R";
        if (!chains[hand].IsValid) return $"<color=#888888>{side}  no authored punch</color>";
        switch (stage[hand])
        {
            case PunchPoseChain.Stage.Coil:
            {
                HandPoses hp = hand == 0 ? left : right;
                string src = chains[hand].HasAuthoredChamber ? $"posed load x{hp.LadderCount}" : "synthesised";
                return $"{side}  <color=#4aa3ff>coil</color> {coilAmount[hand]:0.00}  <color=#99ddff>({src})</color>";
            }
            case PunchPoseChain.Stage.Release:
                return $"{side}  <color=#ffd54a>release</color> x {chainX[hand]:0.00}   {MixLabel(hand)}   pivot {Mathf.Clamp(chainYaw[hand], -maxPivot, maxPivot):0}°";
            default:
                return $"<color=#888888>{side}  guard</color>";
        }
    }

    /// <summary>
    /// Which punch is actually being thrown, in words. "It only throws jabs" should be something you can read
    /// off the screen and confirm, not something you have to judge by eye.
    /// </summary>
    private string MixLabel(int hand)
    {
        Vector4 m = lastMix[hand];
        string shape = m.y >= m.x && m.y >= m.z ? "HOOK" : m.z >= m.x ? "UPPER" : "straight";
        string height = m.w > 0.66f ? "head" : m.w < 0.33f ? "low" : "body";
        return $"<color=#9fe08f>{shape} {height}</color>";
    }

    private void Row(ref float y, float x, float w, string label, float weight, bool present)
    {
        string text = present ? $"{label,-11} {weight:0.00}" : $"<color=#888888>{label,-11} —</color>";
        GUI.Label(new Rect(x, y, 120f, 18f), text, overlayStyle);
        if (present)
        {
            Rect bar = new Rect(x + 120f, y + 5f, w - 120f, 8f);
            GUI.Box(bar, GUIContent.none);
            Color old = GUI.color;
            GUI.color = new Color(0.9f, 0.3f, 0.25f);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(weight), bar.height), Texture2D.whiteTexture);
            GUI.color = old;
        }
        y += 18f;
    }

    // ------------------------------------------------------------------ Masks

    /// <summary>Re-read the region sliders (call after changing them at runtime).</summary>
    public void RebuildMasks()
    {
        int count = HumanTrait.MuscleCount;
        maskGuard = new float[count];
        maskLeftHand = new float[count];
        maskRightHand = new float[count];
        muscleGroup = new int[count];
        isLegMuscle = new bool[count];
        armSide = new int[count];

        for (int m = 0; m < count; m++)
        {
            int boneIndex = HumanTrait.BoneFromMuscle(m);
            if (boneIndex < 0) { muscleGroup[m] = -1; continue; }
            HumanBodyBones bone = (HumanBodyBones)boneIndex;
            muscleGroup[m] = GroupOf(bone);
            Region region = RegionOf(bone, out bool isLeftSide);

            switch (region)
            {
                case Region.Arm:
                    maskGuard[m] = guardArm;
                    maskLeftHand[m] = isLeftSide ? punchingArm : guardArm;
                    maskRightHand[m] = isLeftSide ? guardArm : punchingArm;
                    armSide[m] = isLeftSide ? -1 : 1;
                    break;
                case Region.Torso: maskGuard[m] = maskLeftHand[m] = maskRightHand[m] = torso; break;
                case Region.Head:  maskGuard[m] = maskLeftHand[m] = maskRightHand[m] = head; break;
                case Region.Legs:
                    maskGuard[m] = maskLeftHand[m] = maskRightHand[m] = legWork;
                    isLegMuscle[m] = true;
                    break;
            }
        }
    }

    /// <summary>Kinetic-chain group of a bone (index into GroupWindows; -1 = not part of the chain, e.g. legs).</summary>
    private static int GroupOf(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.Hips: return 0;
            case HumanBodyBones.Spine: return 1;
            case HumanBodyBones.Chest:
            case HumanBodyBones.UpperChest:
            case HumanBodyBones.LeftShoulder:
            case HumanBodyBones.RightShoulder: return 2;
            case HumanBodyBones.LeftUpperArm:
            case HumanBodyBones.RightUpperArm: return 3;
            case HumanBodyBones.LeftLowerArm:
            case HumanBodyBones.RightLowerArm:
            case HumanBodyBones.LeftHand:
            case HumanBodyBones.RightHand: return 4;
            case HumanBodyBones.Neck:
            case HumanBodyBones.Head:
            case HumanBodyBones.Jaw: return 5;
            default:
                if (bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.RightLittleDistal) return 4;
                return -1;
        }
    }

    private static Region RegionOf(HumanBodyBones bone, out bool isLeftSide)
    {
        isLeftSide = false;
        switch (bone)
        {
            case HumanBodyBones.Hips:
            case HumanBodyBones.Spine:
            case HumanBodyBones.Chest:
            case HumanBodyBones.UpperChest:
                return Region.Torso;
            case HumanBodyBones.Neck:
            case HumanBodyBones.Head:
            case HumanBodyBones.Jaw:
            case HumanBodyBones.LeftEye:
            case HumanBodyBones.RightEye:
                return Region.Head;
            case HumanBodyBones.LeftShoulder:
            case HumanBodyBones.LeftUpperArm:
            case HumanBodyBones.LeftLowerArm:
            case HumanBodyBones.LeftHand:
                isLeftSide = true;
                return Region.Arm;
            case HumanBodyBones.RightShoulder:
            case HumanBodyBones.RightUpperArm:
            case HumanBodyBones.RightLowerArm:
            case HumanBodyBones.RightHand:
                return Region.Arm;
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.LeftLowerLeg:
            case HumanBodyBones.LeftFoot:
            case HumanBodyBones.LeftToes:
            case HumanBodyBones.RightUpperLeg:
            case HumanBodyBones.RightLowerLeg:
            case HumanBodyBones.RightFoot:
            case HumanBodyBones.RightToes:
                return Region.Legs;
            default:
                if (bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.LeftLittleDistal) { isLeftSide = true; return Region.Arm; }
                if (bone >= HumanBodyBones.RightThumbProximal && bone <= HumanBodyBones.RightLittleDistal) return Region.Arm;
                return Region.None;
        }
    }
}
