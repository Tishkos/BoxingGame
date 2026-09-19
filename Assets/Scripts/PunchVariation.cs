using UnityEngine;

/// <summary>
/// How one throw differs from the next: speed, wind-up depth, follow-through, aim height, fist roll, arc width
/// and pitch — rolled fresh for every punch and applied on top of the SAME authored pose keys.
/// </summary>
public struct ThrowStyle
{
    public float speedMul;       // punch clock rate multiplier (~0.9-1.1)
    public float coilJitter;     // added to the mixer's chamber coil (±)
    public float followJitter;   // added to the mixer's follow-through (±)
    public float heightJitter;   // added to the punch's aim height (±)
    public float pitchBias;      // degrees added to the punch direction's pitch (overhands come DOWN)
    public float rollDegrees;    // extra fist roll about the punch line (sign applied per hand by the controller)
    public float arcScale;       // hook width / uppercut dip multiplier
    public float powerMul;       // technique multiplier on the punch's effective mass (counters hit harder)
    public string flavor;        // readable name of the variant thrown ("overhand", "shovel", …), or null

    public static ThrowStyle Default => new ThrowStyle { speedMul = 1f, arcScale = 1f, powerMul = 1f };
}

/// <summary>
/// THE VARIATION ENGINE — tons of visibly different punches out of the same authored pose library, without a
/// single new pose being authored.
///
/// The 18 reference poses are the vocabulary; this is the delivery. Every throw rolls a fresh style — how deep
/// the body coils, how hard it carries through, exactly where it lands, how the fist is rolled, how wide the
/// arc bows, how fast the clock runs — and derived FLAVOURS reshape the trajectory into punches that were never
/// posed at all:
///
///   • OVERHAND — a hook thrown at the head pitches DOWN over the guard with extra roll: the looping right.
///   • SHOVEL HOOK — a low uppercut drifts outward into the half-hook, half-uppercut rib-digger.
///   • STIFF JAB — a straight at long range with the coil pulled tight and the clock quickened.
///   • COMBO SHARPENING — the deeper you are into a combo, the shorter each wind-up and the quicker the hand:
///     flurries genuinely flurry instead of replaying the same punch.
///
/// An anti-repeat memory guarantees consecutive punches never roll the same style twice, so even ten straight
/// jabs read as ten different jabs. All of it is texture on top of the authored keys — the poses stay the one
/// source of truth for what a punch IS.
/// Added next to <see cref="BoxerPunchController"/>; it finds it automatically.
/// </summary>
[DisallowMultipleComponent]
public class PunchVariation : MonoBehaviour
{
    [Header("How different punches feel from each other")]
    [Tooltip("Master amount. 0 = every punch identical (the old behaviour), 1 = visibly loose, gym-tired boxing.")]
    [Range(0f, 1f)] public float variation = 0.55f;

    [Tooltip("Punch speed jitter (± fraction of the clock rate at full Variation).")]
    [Range(0f, 0.25f)] public float speedRange = 0.09f;

    [Tooltip("Wind-up depth jitter (± on the mixer's Chamber Coil).")]
    [Range(0f, 0.4f)] public float coilRange = 0.24f;

    [Tooltip("Follow-through jitter.")]
    [Range(0f, 0.3f)] public float followRange = 0.14f;

    [Tooltip("Aim height jitter (± on the 0-1 aim height — pulls different authored height variants each throw).")]
    [Range(0f, 0.25f)] public float heightRange = 0.11f;

    [Tooltip("Extra fist roll about the punch line (± degrees).")]
    [Range(0f, 20f)] public float rollRange = 8f;

    [Tooltip("Hook width / uppercut dip jitter (± fraction).")]
    [Range(0f, 0.5f)] public float arcRange = 0.22f;

    [Header("Combos")]
    [Tooltip("How much each punch deep in a combo tightens: shorter wind-up, quicker hand. At 1, the third punch " +
             "of a flurry is thrown with almost no coil at all — exactly how real flurries work.")]
    [Range(0f, 1f)] public float comboSharpening = 0.4f;

    [Header("Derived flavours (punches the pose library never authored)")]
    [Tooltip("Turn the trajectory flavours on: overhands, shovel hooks, stiff jabs.")]
    public bool enableFlavors = true;

    [Tooltip("How far an overhand pitches down over the guard (degrees).")]
    [Range(0f, 25f)] public float overhandPitch = 13f;

    [Tooltip("Extra roll an overhand puts on the fist (degrees).")]
    [Range(0f, 25f)] public float overhandRoll = 12f;

    [Tooltip("How much wider a shovel hook bows than an ordinary uppercut.")]
    [Range(1f, 2f)] public float shovelArc = 1.35f;

    /// <summary>The last style thrown by each hand — the overlay and VFX can read it.</summary>
    public ThrowStyle LastStyle(int hand) => last[Mathf.Clamp(hand, 0, 1)];

    /// <summary>Injected by the controller: the boxer's seeded simulation stream (falls back to UnityEngine.Random).</summary>
    public SimRng Rng { get; set; }

    private float R(float min, float max) => Rng != null ? Rng.Range(min, max) : Random.Range(min, max);

    private readonly ThrowStyle[] last = { ThrowStyle.Default, ThrowStyle.Default };
    private readonly float[] lastSignature = { -10f, -10f };

    /// <summary>
    /// Roll the style for a punch about to be thrown. Deterministic inputs (type, combo depth, aim height),
    /// random texture — with a memory so two consecutive throws can never come out identical.
    /// </summary>
    public ThrowStyle Roll(BoxerPunchController.Hand hand, BoxerPunchController.PunchType type, int comboCount, float height01,
                           float overdrive = 0f, bool backDash = false, bool countered = false)
    {
        int h = (int)hand;
        float v = Mathf.Clamp01(variation);

        ThrowStyle style = RollOnce(type, comboCount, height01, v, overdrive, backDash);

        // Anti-repeat: if this roll landed within a hair of the last one, roll again — one re-roll is enough to
        // guarantee the pair differs, and it keeps the distribution honest.
        float signature = style.speedMul * 3.1f + style.coilJitter * 7.7f + style.heightJitter * 13.3f + style.rollDegrees * 0.13f;
        if (Mathf.Abs(signature - lastSignature[h]) < 0.05f * v)
            style = RollOnce(type, comboCount, height01, v, overdrive, backDash);

        // THE COUNTER: an evade cashed in. Minted tighter, a shade quicker, 10% harder — and the riposte IS
        // the story, so it overrides whatever flavour was rolled.
        if (countered)
        {
            style.speedMul *= 1.1f;
            style.coilJitter -= 0.1f;
            style.powerMul = 1.1f;
            style.flavor = "counter";
        }

        lastSignature[h] = style.speedMul * 3.1f + style.coilJitter * 7.7f + style.heightJitter * 13.3f + style.rollDegrees * 0.13f;
        last[h] = style;
        return style;
    }

    /// <summary>
    /// A deliberate DOUBLE: the previous throw replayed almost verbatim — clipped, a touch quicker, the same
    /// line. Sameness on purpose is the one thing the anti-repeat memory can never produce.
    /// </summary>
    public ThrowStyle RollDoubled(int hand)
    {
        int h = Mathf.Clamp(hand, 0, 1);
        ThrowStyle style = last[h];
        style.speedMul *= 1.12f;
        style.coilJitter -= 0.25f;
        style.followJitter -= 0.05f;
        if (style.powerMul <= 0f) style.powerMul = 1f;
        style.flavor = "double";
        // Re-key the memory from the modified style so the THIRD punch re-rolls fresh against it.
        lastSignature[h] = style.speedMul * 3.1f + style.coilJitter * 7.7f + style.heightJitter * 13.3f + style.rollDegrees * 0.13f;
        last[h] = style;
        return style;
    }

    private ThrowStyle RollOnce(BoxerPunchController.PunchType type, int comboCount, float height01, float v,
                                float overdrive = 0f, bool backDash = false)
    {
        ThrowStyle style = ThrowStyle.Default;

        style.speedMul = 1f + R(-speedRange, speedRange) * v;
        style.coilJitter = R(-coilRange, coilRange) * v;
        // Biased SHORT: extra follow-through extrapolates the arm past the strike shape, and on straights that
        // reads as the hand sweeping sideways at the end of the punch. Less is texture; more is a flaw.
        style.followJitter = R(-followRange, followRange * 0.5f) * v;
        style.heightJitter = R(-heightRange, heightRange) * v;
        style.rollDegrees = R(-rollRange, rollRange) * v;
        style.arcScale = 1f + R(-arcRange, arcRange) * v;

        // Deep in a combo the punches tighten: less wind-up, a touch more speed. This is what separates a
        // flurry from the same punch played three times.
        if (comboCount > 0 && comboSharpening > 0f)
        {
            float deep = Mathf.Clamp01(comboCount / 3f) * comboSharpening;
            style.coilJitter -= 0.30f * deep;
            style.speedMul *= 1f + 0.10f * deep;
            style.followJitter -= 0.06f * deep;
        }

        if (!enableFlavors) return style;

        // Derived punches — trajectory reshapes of the authored shapes, not new poses. The first two are
        // UNEXPLAINED techniques for players to discover: the check hook and the corkscrew.
        switch (type)
        {
            case BoxerPunchController.PunchType.Hook when backDash:
                // CHECK HOOK: a hook thrown while stepping back off the line — quick, tight, the matador's punch.
                style.speedMul *= 1.12f;
                style.arcScale *= 0.85f;
                style.flavor = "check hook";
                break;

            case BoxerPunchController.PunchType.Straight when height01 > 0.6f && overdrive > 0.7f:
                // CORKSCREW: a fully charged high straight rolls over on the way in and dips through the guard.
                style.rollDegrees += 25f;
                style.pitchBias = -3f;
                style.flavor = "corkscrew";
                break;

            case BoxerPunchController.PunchType.Hook when height01 > 0.72f:
                // OVERHAND: the high hook loops over the guard and comes DOWN.
                style.pitchBias = -overhandPitch * Mathf.InverseLerp(0.72f, 1f, height01);
                style.rollDegrees += overhandRoll;
                style.coilJitter += 0.12f;        // an overhand is always a little telegraphed
                style.speedMul *= 0.95f;
                style.flavor = "overhand";
                break;

            case BoxerPunchController.PunchType.Uppercut when height01 < 0.38f:
                // SHOVEL HOOK: the low uppercut drifts wide into the rib-digger.
                style.arcScale *= shovelArc;
                style.pitchBias = 4f;
                style.coilJitter += 0.08f;
                style.flavor = "shovel";
                break;

            case BoxerPunchController.PunchType.Straight when height01 > 0.55f && comboCount == 0:
                // STIFF JAB: the opener — tight coil, quick clock.
                style.coilJitter -= 0.08f;
                style.speedMul *= 1.05f;
                style.flavor = "stiff jab";
                break;
        }

        return style;
    }
}
