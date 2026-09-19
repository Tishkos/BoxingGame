using UnityEngine;

/// <summary>
/// A PUNCH AS A SEQUENCE OF SHAPES — the core of the pose-driven rewrite.
///
/// The old system blended the guard straight into the strike pose: two keys, one lerp. That is why punches read
/// as "reaching" — a real punch is not a body morphing toward its end shape, it is a body that COILS and then
/// UNWINDS from the ground up, with the fist arriving last because everything behind it threw it.
///
/// So a punch here is four shapes, in muscle space:
///
///     GUARD  ──coil──▶  CHAMBER  ──whip──▶  STRIKE  ──through──▶  FOLLOW
///       │                  │                   │                     │
///     authored        synthesised           authored            synthesised
///
/// CHAMBER is the anti-strike: <c>guard + coil · (guard − strike)</c>. That is not a trick — the wind-up of a
/// punch genuinely is its mirror. A right cross ends with the chest turned in and the arm out, so it starts with
/// the chest turned away and the elbow back, and that falls out of the arithmetic for free, per muscle, for every
/// punch, without anyone authoring 18 more poses. FOLLOW does the same past the end: <c>strike + through ·
/// (strike − guard)</c>, so the punch travels THROUGH the target instead of stopping on it.
///
/// The chain is then sampled PER BODY GROUP with its own window over the punch clock (hips 0-0.55 … fist
/// 0.55-1.0). Coiling is whole-body and simultaneous — you load as one piece. Releasing is staggered — the hips
/// are already at the strike shape while the hand is still chambered. That stagger, over a real chamber key, is
/// the whip; over the old two-key lerp it did almost nothing, which is why it never looked like much.
///
/// Nothing here knows about time, targets or physics. It answers one question: "for this muscle, at this point
/// in the punch, what shape should the body be in?" <see cref="ReferencePoseMixer"/> asks it every frame.
/// </summary>
public class PunchPoseChain
{
    /// <summary>What the hand is doing. Recovery is not a stage — it is this shape fading back to the guard.</summary>
    public enum Stage
    {
        /// <summary>Nothing; the guard owns the body.</summary>
        None,
        /// <summary>Holding the trigger: the whole body coils into the chamber together.</summary>
        Coil,
        /// <summary>Thrown: each group leaves the chamber inside its own window — the kinetic chain.</summary>
        Release,
    }

    /// <summary>
    /// Where each group sits in the release, as a window over the punch clock. The hips are done unwinding
    /// before the fist has started, which is the whole point.
    /// </summary>
    public static readonly Vector2[] GroupWindows =
    {
        new Vector2(0.00f, 0.55f),   // 0 hips — fires first, from the ground
        new Vector2(0.10f, 0.70f),   // 1 spine
        new Vector2(0.20f, 0.85f),   // 2 chest + shoulders
        new Vector2(0.35f, 0.95f),   // 3 upper arms
        new Vector2(0.55f, 1.00f),   // 4 forearms + hands — the fist arrives last
        new Vector2(0.15f, 0.60f),   // 5 head (the chin turns in early and stays tucked)
    };

    /// <summary>
    /// How much of a SYNTHESISED coil each group takes. A real wind-up loads the hips and the torso; the hand
    /// just stays at the chin. Letting the arm wind up as hard as the body is what folded the elbow shut and
    /// parked the glove beside the head. Authored load poses ignore this entirely — they are posed, not derived.
    /// </summary>
    private static readonly float[] GroupCoil = { 1f, 1f, 0.9f, 0.55f, 0.4f, 0.3f };

    /// <summary>
    /// How much follow-through each group takes. The TORSO carrying on through the target is what sells a punch;
    /// the arm doing it just hyperextends the elbow, so the further down the chain, the less of it.
    /// </summary>
    private static readonly float[] GroupFollow = { 1f, 1f, 0.9f, 0.5f, 0.35f, 0.6f };

    /// <summary>Where inside a group's window the strike shape is reached; the short tail past it is follow-through.</summary>
    private const float StrikeU = 0.9f;

    // ------------------------------------------------------------------ Keys

    private float[] guard;
    private float[] chamber;
    private float[] strike;
    private float[] follow;
    private bool[] authored;
    private bool[] chamberIsAuthored;

    /// <summary>True when at least one muscle's wind-up came from a posed load rather than the arithmetic.</summary>
    public bool HasAuthoredChamber { get; private set; }

    /// <summary>True once <see cref="Rebuild"/> has produced usable keys.</summary>
    public bool IsValid { get; private set; }

    /// <summary>How many muscles this chain actually drives (the rest are left to the animation).</summary>
    public int AuthoredCount { get; private set; }

    // ------------------------------------------------------------------ Building

    /// <summary>
    /// Rebuild the four keys for one hand's punch. <paramref name="strikeBlend"/> is the already-mixed strike
    /// shape — one array blended across punch type (a stick between "hook" and "straight" throws a real hybrid)
    /// and across the three aim heights. Blending the TARGET and then deriving one chain is both cheaper and far
    /// better behaved than running three chains and averaging them, which used to let a hook's chamber fight a
    /// straight's chamber.
    /// </summary>
    /// <param name="guardPose">The home shape. Muscles it does not author fall back to the strike.</param>
    /// <param name="strikeBlend">Blended strike values, indexed by muscle.</param>
    /// <param name="strikeAuthored">Which muscles the strike blend actually covers.</param>
    /// <param name="coil">How far past the guard the wind-up reaches. ~0.5 is a compact boxer's load.</param>
    /// <param name="through">How far past the strike the follow-through reaches.</param>
    /// <param name="chamberBlend">
    /// Authored wind-up shape, already blended across the load ladder by aim height — or null to synthesise it.
    /// Where the user has posed the chamber themselves it is used verbatim: no extrapolation, no deviation cap,
    /// no per-group scaling. A posed chamber is ground truth; the arithmetic only ever existed to stand in for
    /// one that did not exist.
    /// </param>
    public void Rebuild(ReferencePose guardPose, float[] strikeBlend, bool[] strikeAuthored,
                        float[] chamberBlend, bool[] chamberAuthored,
                        float coil, float through, float maxDeviation, float torsoFloor, int side)
    {
        int count = HumanTrait.MuscleCount;
        EnsureSize(count);
        EnsureMuscleTables();

        if (strikeBlend == null || strikeAuthored == null)
        {
            IsValid = false;
            AuthoredCount = 0;
            return;
        }

        HasAuthoredChamber = false;

        float[] guardValues = guardPose != null && guardPose.IsValid ? guardPose.Resolved : null;
        bool[] guardAuthored = guardPose != null && guardPose.IsValid ? guardPose.Authored : null;

        AuthoredCount = 0;
        for (int m = 0; m < count; m++)
        {
            if (!strikeAuthored[m]) { authored[m] = false; continue; }

            // A muscle the guard never authored has no "home" of its own — the strike is both ends of it, so it
            // simply holds still rather than swinging out of nowhere.
            float g = guardAuthored != null && guardAuthored[m] ? guardValues[m] : strikeBlend[m];
            float s = strikeBlend[m];

            guard[m] = g;
            strike[m] = s;

            if (chamberAuthored != null && chamberAuthored[m])
            {
                // Posed by hand: take it exactly as authored.
                chamber[m] = Mathf.Clamp(chamberBlend[m], -1f, 1f);
                chamberIsAuthored[m] = true;
                HasAuthoredChamber = true;
            }
            else
            {
                // THE CAP. Extrapolation is unbounded, and muscle space is not: without this the elbow of a
                // straight goes to -1.00 (folded shut against the shoulder) and the glove ends up held up by the
                // head. A wind-up is a loaded posture, not a joint driven into its stop, so no synthesised
                // muscle may travel further than this past the guard.
                float deviation = Mathf.Clamp(coil * (g - s), -maxDeviation, maxDeviation);
                chamber[m] = isFinger[m] ? g : Mathf.Clamp(g + deviation, -1f, 1f);
                chamberIsAuthored[m] = false;
            }

            follow[m] = Mathf.Clamp(s + through * (s - g), -1f, 1f);
            authored[m] = true;
            AuthoredCount++;
        }

        if (AuthoredCount > 0) ApplyTorsoFloor(torsoFloor, side);
        IsValid = AuthoredCount > 0;
    }

    /// <summary>
    /// Guarantee the body winds up even when the pose does not ask it to. Hooks and uppercuts carry real torso
    /// rotation, so the arithmetic already coils them correctly — but a cross authored with the chest square
    /// gives the synthesis nothing to work with, and the wind-up then has to come out of the arm, which is
    /// exactly the wrong place. This puts the punching shoulder back by at least a set amount and leaves the
    /// authored coil alone wherever it is already deeper.
    /// </summary>
    private void ApplyTorsoFloor(float torsoFloor, int side)
    {
        if (torsoFloor <= 0.0001f || side == 0) return;

        // Direction taken from the authored hooks: a right hook turns the chest positive at the strike, so the
        // right shoulder going BACK is negative — hence away from the punching side.
        float wanted = -side * torsoFloor;

        for (int m = 0; m < isTorsoTwist.Length; m++)
        {
            if (!isTorsoTwist[m] || !authored[m] || chamberIsAuthored[m]) continue;   // a posed load knows better
            float have = chamber[m] - guard[m];
            bool deeper = wanted > 0f ? have >= wanted : have <= wanted;
            if (!deeper) chamber[m] = Mathf.Clamp(guard[m] + wanted, -1f, 1f);
        }
    }

    // Muscle classification, resolved once from HumanTrait's names.
    private static bool[] isFinger;
    private static bool[] isTorsoTwist;

    private static void EnsureMuscleTables()
    {
        if (isFinger != null) return;
        string[] names = HumanTrait.MuscleName;
        isFinger = new bool[names.Length];
        isTorsoTwist = new bool[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            string n = names[i];
            // A fist is a fist. Every finger muscle is already at its limit in these poses, so synthesising a
            // chamber for them only ever opens the glove.
            isFinger[i] = n.Contains("Thumb") || n.Contains("Index") || n.Contains("Middle")
                       || n.Contains("Ring") || n.Contains("Little");
            isTorsoTwist[i] = n.EndsWith("Twist Left-Right")
                           && (n.StartsWith("Spine") || n.StartsWith("Chest") || n.StartsWith("UpperChest"));
        }
    }

    private void EnsureSize(int count)
    {
        if (guard != null && guard.Length == count) return;
        guard = new float[count];
        chamber = new float[count];
        strike = new float[count];
        follow = new float[count];
        authored = new bool[count];
        chamberIsAuthored = new bool[count];
    }

    // ------------------------------------------------------------------ Sampling

    /// <summary>Does this chain drive that muscle at all?</summary>
    public bool Drives(int muscle) => IsValid && authored != null && muscle < authored.Length && authored[muscle];

    /// <summary>
    /// The shape this muscle should be in. <paramref name="group"/> is its kinetic-chain group (see
    /// <see cref="GroupWindows"/>); pass -1 for anything outside the chain and it moves with the whole body.
    ///
    /// COIL is simultaneous: you do not wind up your hips, then your chest, then your arm — you load as one
    /// piece, which is exactly why the release can be a chain at all.
    /// RELEASE is staggered: each group crosses chamber → strike → follow inside its own window.
    /// </summary>
    public float Sample(int muscle, int group, Stage stage, float x, float coilAmount)
    {
        // The chamber THIS group actually reaches. Both stages have to agree on it: sampling the release from
        // the unscaled chamber (as it first did) meant the arm snapped into a fully folded elbow on the frame
        // the punch fired, undoing the whole point of winding the body up instead of the hand.
        float scale = chamberIsAuthored != null && chamberIsAuthored[muscle] ? 1f : CoilScale(group);
        float chamberHere = Mathf.LerpUnclamped(guard[muscle], chamber[muscle], scale);

        if (stage == Stage.Coil)
            return Mathf.LerpUnclamped(guard[muscle], chamberHere, Mathf.Clamp01(coilAmount));

        Vector2 window = group >= 0 && group < GroupWindows.Length ? GroupWindows[group] : new Vector2(0f, 1f);
        float u = Mathf.Clamp01(Mathf.InverseLerp(window.x, window.y, x));

        if (u <= StrikeU)
        {
            // Chamber → strike. Eased so the group accelerates out of the coil rather than sliding off it.
            float t = u / StrikeU;
            return Mathf.LerpUnclamped(chamberHere, strike[muscle], t * t * (3f - 2f * t));
        }

        // Strike → follow: short, and it keeps going the way the punch was already going. Scaled per group so the
        // chest turns through the shot while the arm stops at the shape that was actually authored.
        float tail = (u - StrikeU) / (1f - StrikeU) * FollowScale(group);
        return Mathf.LerpUnclamped(strike[muscle], follow[muscle], tail);
    }

    private static float CoilScale(int group)
        => group >= 0 && group < GroupCoil.Length ? GroupCoil[group] : 1f;

    private static float FollowScale(int group)
        => group >= 0 && group < GroupFollow.Length ? GroupFollow[group] : 1f;

    /// <summary>
    /// Where the chain has got to overall, 0-1, ignoring the per-group stagger. Feedback systems (twist, lean,
    /// chin tuck) read this so they agree with the body instead of running on their own clock.
    /// </summary>
    public static float Progress(Stage stage, float x, float coilAmount)
        => stage == Stage.Coil ? -Mathf.Clamp01(coilAmount) : Mathf.Clamp01(x);
}
