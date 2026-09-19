using UnityEngine;

/// <summary>
/// What the punch controller needs from a glove — implemented by the trigger sensor (<see cref="PunchHitbox"/>)
/// and by the physical fist of the active ragdoll (<see cref="PhysicalFist"/>).
///
/// The contract that keeps the bag on its chain: a sensor may only deliver an IMPULSE while <see cref="Armed"/>
/// (the controller arms a hand for the strike window of a thrown punch, and re-arming re-opens the one-hit
/// latch). Any other contact must go through <see cref="PunchingBag.Lean"/> — a capped push, never a punch.
/// </summary>
public interface IPunchSensor
{
    /// <summary>True while the controller is driving a punch with this hand — the only time a hit may land.</summary>
    bool Armed { get; set; }

    /// <summary>Effective-mass multiplier from technique: overdrive, footwork, rhythm, stamina…</summary>
    float PowerScale { get; set; }

    /// <summary>
    /// The velocity the controller is driving the glove at, or null to measure it from motion. The virtual fist knows
    /// its exact speed; measuring the bone at the physics rate under-reads a fast punch by a fifth.
    /// </summary>
    Vector3? DrivenVelocity { get; set; }
}
