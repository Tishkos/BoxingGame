using System;
using UnityEngine;

/// <summary>
/// The physical body of a boxer, as <see cref="BoxerPunchController"/> and <see cref="PhysicalFist"/> see it.
///
/// req.md's pipeline is  input → punch intent → PHYSICAL BODY → collision → damage, and the controller only ever
/// speaks intent: "this hand wants to be here, moving this fast, with this much drive behind it". Whatever is
/// underneath — <see cref="BoxerPhysics"/> (PuppetMaster active ragdoll, the real one) or the older
/// <see cref="BoxerPhysicsBody"/> (self-built ConfigurableJoint puppet) — answers with muscles, momentum and
/// collisions. Nothing above this interface knows or cares which.
/// </summary>
public interface IBoxerBody
{
    /// <summary>The puppet exists and is simulating.</summary>
    bool IsBuilt { get; }

    /// <summary>The boxer is down; the controller stops taking punches and footwork.</summary>
    bool IsKnockedDown { get; }

    /// <summary>The physical gloves — these are the hit sensors once the body is on.</summary>
    PhysicalFist LeftFist { get; }
    PhysicalFist RightFist { get; }

    /// <summary>Raised when the boxer goes down (true) and when he is back up (false).</summary>
    event Action<bool> KnockdownChanged;

    /// <summary>Where a hand is trying to be. Not a teleport — the muscles chase it (req.md §2, §42).</summary>
    void SetHand(int hand, bool active, Vector3 target, Vector3 velocity, float drive);

    /// <summary>Which phase that hand's punch is in, and how far along (0 guard · 1 full strike) — the kinetic
    /// chain, the arm's pin and the impact crumple all key off this (req.md §8, §14).</summary>
    void SetPunch(int hand, BoxerPunchController.Phase phase, float x, float overdrive);

    /// <summary>Guard is physical: blocking stiffens the arms rather than zeroing damage (req.md §14, §26).</summary>
    void SetGuard(bool blocking);

    /// <summary>Global muscle multiplier from outside — stamina, gassed, scripted weakness (req.md §31).</summary>
    void SetStrength(float strength);

    /// <summary>A shot to the head: enough speed weakens the muscles (stun), a lot of it puts him down.</summary>
    void NotifyHeadHit(float speed);

    /// <summary>How much of the body is really moving behind this fist, in kg (req.md §7, §19).</summary>
    float EffectiveMass(int hand, Vector3 direction, float impactSpeed);

    /// <summary>Put the boxer down now.</summary>
    void KnockDown();
}
