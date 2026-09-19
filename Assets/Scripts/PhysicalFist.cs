using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// The hit analyzer on a physical glove of the active ragdoll (req.md §6, §17–§19) — rewritten around one rule:
/// A PUNCH IS SOMETHING YOU THREW. Only a glove inside its armed strike window may deliver an impulse; every
/// other touch (guard raised against the leather, an idle hand brushed by a swinging bag, a pose blend sweeping
/// the arm through the volume) is a LEAN — a soft, capped push the bag eases away from. This is the fix for the
/// bag flying when L1/R1 snapped the gloves up through it.
///
/// When a punch DOES land, damage is never computed from input: the glove reads the real relative velocity at
/// the contact, verifies it is humanly possible (an IK snap can move a muscle at 40 m/s for one step — a fist
/// cannot), reads how squarely it hit and how much of the body was moving behind it (effective mass), and hands
/// the bag the impulse the plain rigidbody collision could not know about — the mass of the arm, chest and hips
/// that drove the fist. The bag then applies its OWN energy budget on top. Two independent layers of sanity.
///
/// It also lands punches on another FIGHTER's hit zones (the trigger spheres <see cref="BoxerHealth"/> builds),
/// measured exactly the same way — the physics body used to have no path to hit an opponent at all.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class PhysicalFist : MonoBehaviour, IPunchSensor
{
    [Serializable] public class LandedEvent : UnityEvent<PhysicalFist, PunchingBag, PunchingBag.HitInfo> { }

    [Tooltip("Relative impact speed below this (m/s) is contact, not a punch.")]
    [Min(0f)] public float minSpeed = 2f;

    [Tooltip("Seconds before this glove may land again.")]
    [Min(0f)] public float cooldown = 0.15f;

    [Tooltip("Hard cap on the added impulse (N·s). The bag applies its own surface-speed budget after this.")]
    [Min(1f)] public float maxImpulse = 140f;

    [Tooltip("The fastest a HUMAN fist travels, in m/s at 1x character scale (elite crosses peak ~11-12). " +
             "Anything the physics reports above this — solver spikes, depenetration, an IK snap — is clamped: " +
             "a glitch may cost a punch some speed, it may never add any.")]
    [Min(1f)] public float maxImpactSpeed = 13f;

    [Tooltip("Ceiling on the effective mass behind a punch (kg at 1x scale). Fist + forearm + arm + committed " +
             "chest and hips of a heavyweight is ~16.")]
    [Min(1f)] public float maxEffectiveMass = 16f;

    [Tooltip("How much a sloppy wrist costs (req.md §18). 0 = knuckle alignment is ignored; 1 = a punch landing " +
             "sideways transfers almost nothing.")]
    [Range(0f, 1f)] public float wristPenalty = 0.5f;

    public LandedEvent OnLanded = new LandedEvent();

    /// <summary>0 = left, 1 = right.</summary>
    public int Hand { get; set; }

    /// <summary>The physical body this glove belongs to — PuppetMaster or the older self-built puppet.</summary>
    public IBoxerBody Body { get; set; }

    /// <summary>Character scale — speeds and masses are validated at 1x and scaled up for a 2x rig.</summary>
    public float SpeedScale { get; set; } = 1f;

    /// <summary>Which way the knuckles point, in this rigidbody's local space. Set by the body at bring-up;
    /// zero means "not measured", and wrist alignment is skipped rather than guessed.</summary>
    public Vector3 KnuckleAxisLocal { get; set; }

    public Rigidbody Rigidbody { get; private set; }

    /// <summary>Armed = inside the strike window of a thrown punch. Only an armed glove can punch;
    /// arming afresh re-opens the one-hit-per-punch latch.</summary>
    public bool Armed
    {
        get => armed;
        set
        {
            if (value && !armed) landedThisStrike = false;
            armed = value;
        }
    }

    public float PowerScale { get; set; } = 1f;

    /// <summary>Accepted for the interface; a physical glove always uses its real rigidbody velocity.</summary>
    public Vector3? DrivenVelocity { get; set; }

    /// <summary>Speed of the glove (m/s).</summary>
    public float Speed => Rigidbody != null ? Rigidbody.linearVelocity.magnitude : 0f;

    private bool armed;
    private bool landedThisStrike;
    private float nextHitTime;
    private BoxerPunchController owner;

    private void Awake()
    {
        Rigidbody = GetComponent<Rigidbody>();
        // The scripts meter what they see; PhysX meters the rest. A glove waking up inside the bag must ooze
        // out, not detonate.
        Rigidbody.maxDepenetrationVelocity = 3f;
        PhysicsPuppetPart part = GetComponent<PhysicsPuppetPart>();
        owner = part != null ? part.Owner : GetComponentInParent<BoxerPunchController>();
    }

    private void OnCollisionEnter(Collision collision) { Touch(collision); }
    private void OnCollisionStay(Collision collision) { Touch(collision); }

    private void OnCollisionExit(Collision collision)
    {
        PunchingBag bag = collision.collider.GetComponentInParent<PunchingBag>();
        if (bag != null) bag.ClearContact(Hand);
    }

    /// <summary>Another fighter's hit zones are triggers — the glove flies through them.</summary>
    private void OnTriggerStay(Collider other)
    {
        if (!armed || landedThisStrike || Time.time < nextHitTime) return;
        BoxerHitZone zone = other.GetComponentInParent<BoxerHitZone>();
        if (zone != null && zone.Health != null) TryHitBoxer(zone, other.ClosestPoint(transform.position));
    }

    // ---------------------------------------------------------------- Contact routing

    private void Touch(Collision collision)
    {
        if (collision.contactCount == 0) return;
        PunchingBag bag = collision.collider.GetComponentInParent<PunchingBag>();
        if (bag == null || !bag.IsBuilt) return;

        ContactPoint contact = collision.GetContact(0);

        if (armed && !landedThisStrike && Time.time >= nextHitTime)
        {
            if (Strike(bag, contact)) return;
        }

        // Not a punch (or the punch already spent): the glove is simply against the bag. A soft push, a dent —
        // and NOTHING that could launch it. This branch is where L1/R1 used to detonate the bag.
        float depth = Mathf.Max(0.01f, -contact.separation + 0.02f);
        bag.Lean(Hand, contact.point, -contact.normal, depth, Rigidbody.GetPointVelocity(contact.point));
    }

    /// <summary>An armed glove met the bag: measure the collision and land the punch.</summary>
    private bool Strike(PunchingBag bag, ContactPoint contact)
    {
        Vector3 point = contact.point;

        // Relative velocity at the actual contact: fist minus bag surface (timing lives here).
        Vector3 relative = Rigidbody.GetPointVelocity(point) - bag.PointVelocity(point);
        float speed = relative.magnitude;
        if (speed < minSpeed) return false;
        Vector3 direction = relative / speed;

        // A human ceiling on what physics reports: one solver spike must never read as a superhuman punch.
        speed = Mathf.Min(speed, maxImpactSpeed * Mathf.Max(0.1f, SpeedScale));

        // Square hit vs glancing blow: velocity against the contact normal.
        float square = Mathf.Clamp01(Mathf.Abs(Vector3.Dot(direction, contact.normal)));
        float impactSpeed = speed * square;
        if (impactSpeed < minSpeed * 0.7f) return false;

        // Effective mass: fist + whatever part of the arm/torso/hips is really moving with it (req.md §7, §19).
        float fistMass = Rigidbody.mass;
        float effectiveMass = Body != null ? Body.EffectiveMass(Hand, direction, speed) : fistMass;
        effectiveMass = Mathf.Min(effectiveMass, maxEffectiveMass * Mathf.Max(0.1f, SpeedScale));

        // Wrist alignment (req.md §18): knuckles behind the punch transfer the energy, a wrist turned sideways
        // grazes. Only applied when the body actually measured the knuckle axis off the rig.
        float wrist = 1f;
        if (KnuckleAxisLocal != Vector3.zero)
        {
            Vector3 knuckles = transform.TransformDirection(KnuckleAxisLocal).normalized;
            wrist = Mathf.Lerp(1f, Mathf.Clamp01(Vector3.Dot(knuckles, direction)), wristPenalty);
        }

        // The impulse the raw collision could not know about: everything behind the fist except the fist itself
        // (PhysX already transferred the fist's own momentum in the contact this call is servicing).
        float technique = PowerScale * wrist;
        float extra = Mathf.Min(Mathf.Max(0f, effectiveMass - fistMass) * impactSpeed * technique, maxImpulse);

        float applied = bag.Hit(point, direction, extra, square, impactSpeed, Hand,
                                owner != null ? owner.Overdrive((BoxerPunchController.Hand)Hand) : 0f);
        landedThisStrike = true;
        nextHitTime = Time.time + cooldown;

        // ONE HitInfo per hit: reuse the bag's own record (set inside Hit) so the counter/surface fields and
        // the feedback-floored strength reach every listener identically.
        OnLanded.Invoke(this, bag, bag.LastHit);
        return true;
    }

    /// <summary>
    /// An armed glove crossed another fighter's hit zone: same measurement as a punch on the bag — real relative
    /// velocity, verified speed, effective mass, squareness — and the zone decides what that energy does to him.
    /// </summary>
    private bool TryHitBoxer(BoxerHitZone zone, Vector3 point)
    {
        BoxerHealth health = zone.Health;
        if (health == null || health.IsDown) return false;
        // Never punch yourself: the guard hand sits inside your own head zone all round.
        if (owner != null && health.GetComponent<BoxerPunchController>() == owner) return false;

        Vector3 velocity = Rigidbody.linearVelocity;
        float speed = velocity.magnitude;
        if (speed < minSpeed) return false;
        Vector3 direction = velocity / speed;
        speed = Mathf.Min(speed, maxImpactSpeed * Mathf.Max(0.1f, SpeedScale));

        Vector3 toTarget = point - transform.position;
        if (Vector3.Dot(direction, toTarget) <= 0f) return false;   // pulling out, not driving in

        // A moving opponent changes the timing exactly like a swinging bag does.
        Vector3 targetVelocity = Vector3.zero;
        Controller moving = health.GetComponent<Controller>();
        if (moving != null) targetVelocity = moving.MoveDirection * moving.CurrentSpeed;
        float relativeSpeed = Mathf.Max(0f, Vector3.Dot(velocity - targetVelocity, direction));
        relativeSpeed = Mathf.Min(relativeSpeed, maxImpactSpeed * Mathf.Max(0.1f, SpeedScale));
        if (relativeSpeed < minSpeed) return false;

        float fistMass = Rigidbody.mass;
        float effectiveMass = Body != null ? Body.EffectiveMass(Hand, direction, speed) : fistMass;
        effectiveMass = Mathf.Min(effectiveMass, maxEffectiveMass * Mathf.Max(0.1f, SpeedScale));

        float wrist = 1f;
        if (KnuckleAxisLocal != Vector3.zero)
        {
            Vector3 knuckles = transform.TransformDirection(KnuckleAxisLocal).normalized;
            wrist = Mathf.Lerp(1f, Mathf.Clamp01(Vector3.Dot(knuckles, direction)), wristPenalty);
        }

        Vector3 flatDir = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
        Vector3 flatToTarget = Vector3.ProjectOnPlane(health.transform.position - transform.position, Vector3.up).normalized;
        float cleanliness = flatDir.sqrMagnitude > 0f && flatToTarget.sqrMagnitude > 0f
            ? Mathf.Clamp01(Vector3.Dot(flatDir, flatToTarget)) * wrist
            : wrist;

        float impulse = Mathf.Min(effectiveMass * relativeSpeed * PowerScale * wrist, maxImpulse);
        landedThisStrike = true;
        nextHitTime = Time.time + cooldown;

        health.ReceiveHit(zone.Zone, point, direction, impulse, cleanliness, relativeSpeed, owner);
        return true;
    }
}
