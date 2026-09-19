using UnityEngine;

/// <summary>
/// The MASTER CLOCK of one hand's punch — the heart of the "Released Chain" rewrite. A single progress value
/// x ∈ [0, 1] owns everything about a punch: 0 = guard, ~0.18 = chamber, 1 = full strike. The pose mixer samples
/// the kinetic chain from it (hips fire first, fist last), the fist position converges on the target with it, the
/// full animation clip is SCRUBBED by it, and hit-stop simply freezes it — per hand, never globally.
///
/// The drive advances x on a BELL-shaped rate (slow launch anticipation, screaming mid-flight, decelerating into
/// the target) — the velocity profile of a real punch, instead of a point gliding on a rail at constant speed.
/// </summary>
public class PunchTimeline
{
    /// <summary>Punch progress: 0 guard · ~0.18 chamber · 1 full strike.</summary>
    public float X { get; private set; }

    /// <summary>Hit-stop: while true the clock (and therefore the pose, fist and clip of THIS hand) stands still.</summary>
    public bool Frozen { get; set; }

    /// <summary>Where the chamber hold sits on the timeline.</summary>
    public const float ChamberX = 0.18f;

    /// <summary>
    /// Bell-shaped drive rate (x per second at rate multiplier 1): gentle out of the chamber, peak speed through
    /// the middle of the flight, easing as the arm reaches full extension. Integrates to roughly a 0.16 s punch.
    /// </summary>
    private static float Rate(float x)
    {
        // Mathf.Sin(Mathf.PI) is NOT zero in float arithmetic — it is -8.74e-08. Mathf.Pow of a NEGATIVE base
        // returns NaN, so the moment the clock reached exactly 1 this produced NaN, Mathf.Min(1, NaN) is NaN,
        // and the clock carried NaN into every muscle the pose chain drives. The character's bones then went
        // NaN, its bounds went invalid, and it vanished. Clamping the sine to zero costs nothing and fixes it.
        float sine = Mathf.Max(0f, Mathf.Sin(Mathf.Clamp01(x) * Mathf.PI));
        return 3.1f * (0.45f + 2.55f * Mathf.Pow(sine, 0.8f));
    }

    /// <summary>Anything that is not a real number is treated as 0 — the clock must never carry NaN.</summary>
    private static float Safe(float value, float fallback = 0f)
        => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

    /// <summary>Advance toward the chamber while the punch is held. Smooth from any direction (retakes fall back).</summary>
    public void ToChamber(float dt)
    {
        if (Frozen) return;
        X = Safe(Mathf.MoveTowards(X, ChamberX, 1.6f * Safe(dt)), X);
    }

    /// <summary>
    /// Advance the drive; rateMultiplier carries punch speed / overdrive. An optional SHAPE curve (from the
    /// punch type's profile) replaces the built-in bell — per-type velocity profiles, authored in the
    /// Inspector: a jab that snaps flat-out, a cross that keeps accelerating into the target.
    /// </summary>
    public float Drive(float dt, float rateMultiplier, AnimationCurve shape = null)
    {
        if (Frozen) return X;
        float rate = shape != null && shape.length >= 2
            ? 3.1f * Mathf.Max(0.05f, Safe(shape.Evaluate(Mathf.Clamp01(X)), 1f))
            : Rate(X);
        // Mathf.Min(1, NaN) returns NaN, so a single bad step used to poison the clock permanently. Guarded.
        X = Safe(Mathf.Min(1f, X + rate * Mathf.Max(0.05f, Safe(rateMultiplier, 1f)) * Safe(dt)), X);
        return X;
    }

    /// <summary>Set the clock directly (impact plants it at 1; recovery plays it back down from wherever it was).</summary>
    public void Set(float x)
    {
        if (Frozen) return;
        X = Mathf.Clamp01(Safe(x));
    }

    public void Reset()
    {
        X = 0f;
        Frozen = false;
    }
}
