using UnityEngine;

/// <summary>
/// THE SIMULATION SEAMS — the boxing pipeline as six separable stages, so prediction, replay and (later)
/// networking have clean joints to cut at. No networking exists yet; this file is the CONTRACT.
///
///   1. PLAYER INPUT        — <see cref="BoxerInput"/>: device polling; produces sticks and buttons, nothing else.
///   2. REQUESTED ACTION    — <see cref="PunchCommand"/>, through BoxerPunchController.RequestPunch:
///                            every punch enters the simulation through one logged, replayable gate.
///   3. POSE EVALUATION     — ReferencePoseMixer + PunchPoseChain: a function of command, clock and seed.
///   4. PHYSICS EXECUTION   — PunchTimeline clocks, IK targets, the bag's rigidbody and energy budget.
///   5. COLLISION RESULT    — PunchHitbox / PhysicalFist → PunchingBag.Hit → HitInfo: one record per contact.
///   6. DAMAGE CALCULATION  — consumers of HitInfo (BoxerHealth zones for fighters; a bag has no health).
///
/// DETERMINISM: each boxer owns a seeded <see cref="SimRng"/>; every simulation-affecting roll draws from it
/// (variation styles, pose variants, scatter, stumbles). Set a non-zero Simulation Seed on the controller and
/// the same inputs replay the same fight. Presentation (VFX, audio, camera) may keep UnityEngine.Random —
/// it is allowed to diverge between machines. Known non-determinism that remains: the punch clocks integrate
/// on frame time (clamped to 1/30 s max step) and PhysX itself — both acceptable until real netcode arrives.
/// </summary>
public struct PunchCommand
{
    public BoxerPunchController.Hand hand;
    public float overdrive;     // charge at release, 0-1
    public Vector2 stick;       // the aim/type stick AS RELEASED — the identity of the punch
    public float time;          // Time.time when issued — replay ordering

    public override string ToString() => $"{hand} od{overdrive:0.00} stick({stick.x:0.00},{stick.y:0.00}) @{time:0.000}";
}
