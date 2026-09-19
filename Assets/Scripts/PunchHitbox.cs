using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// The glove sensor for the IK-driven boxer (no active ragdoll): a trigger sphere riding the hand bone at the
/// glove's striking face. Rewritten around the same rule as <see cref="PhysicalFist"/>:
///
///     A PUNCH IS SOMETHING YOU THREW. Impulses only while <see cref="Armed"/> — the strike window of a punch
///     the controller actually fired — and at most one per punch. Everything else (guard raised into the
///     leather, the animation drifting a hand through the bag, footwork brushing it) is a LEAN: a soft capped
///     push through <see cref="PunchingBag.Lean"/> that eases the bag away and keeps a dent under the glove.
///
/// When a punch lands, the measured hand speed is verified against a human ceiling before it becomes momentum —
/// an IK snap can move a bone at 40 m/s for one frame; a fist cannot, and one bad frame must never launch the
/// bag. The bag then applies its own surface-speed budget on top: two independent layers of sanity.
///
/// The old "Solid" mode (a kinematic collider the bag bounced off while blocking) is gone — a kinematic sphere
/// popping into existence inside the bag was exactly the depenetration bomb that sent it flying on L1/R1. The
/// bag bouncing off a raised guard is now handled by <see cref="BoxerPunchController.ReceiveBagContact"/> via
/// <see cref="PunchingBag.Bounce"/>, which is capped like everything else.
/// Created by <see cref="BoxerHitboxes"/>, which places it at the glove's striking face.
/// </summary>
[RequireComponent(typeof(SphereCollider))]
public class PunchHitbox : MonoBehaviour, IPunchSensor
{
    [Serializable] public class LandedEvent : UnityEvent<PunchHitbox, PunchingBag, PunchingBag.HitInfo> { }

    [Tooltip("Sensor radius in metres (the glove's striking face is ~0.055).")]
    [Min(0.01f)]
    [SerializeField] private float radius = 0.055f;

    [Tooltip("Mass behind the punch in kg (fist + glove + forearm + the shoulder driving them). 5-8 for a boxer.")]
    [Min(0.1f)]
    [SerializeField] private float effectiveMass = 6f;

    [Tooltip("Relative speed below this (m/s) is not a punch.")]
    [Min(0f)]
    [SerializeField] private float minSpeed = 2.5f;

    [Tooltip("Scales the resulting impulse. 1 = physically plausible; raise for arcade feel.")]
    [Min(0f)]
    [SerializeField] private float power = 1f;

    [Tooltip("Hard cap on the impulse (N·s). The bag applies its own surface-speed budget after this.")]
    [Min(1f)]
    [SerializeField] private float maxImpulse = 140f;

    [Tooltip("The fastest a HUMAN fist travels (m/s at 1x character scale; elite crosses peak ~11-12). Measured " +
             "speeds above this are glitches — an IK snap, a solver spike — and are clamped, never trusted.")]
    [Min(1f)]
    [SerializeField] private float maxImpactSpeed = 13f;

    [Tooltip("Seconds before this hand may land again.")]
    [Min(0f)]
    [SerializeField] private float cooldown = 0.15f;

    [Tooltip("Raised when a punch lands.")]
    public LandedEvent OnLanded = new LandedEvent();

    /// <summary>Armed = inside the strike window of a thrown punch. Re-arming re-opens the one-hit latch.</summary>
    public bool Armed
    {
        get => armed;
        set
        {
            if (value && !armed) landedThisStrike = false;
            armed = value;
        }
    }

    /// <summary>Runtime multiplier on the impulse (charge, footwork, rhythm, stamina…). Reset to 1 after each punch.</summary>
    public float PowerScale { get; set; } = 1f;

    /// <summary>Exact velocity the punch controller is driving this glove at; null = measure from bone motion.</summary>
    public Vector3? DrivenVelocity { get; set; }

    /// <summary>Smoothed world-space velocity of the hand measured from motion (m/s).</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>Raised when this glove lands on another BOXER: zone and how heavy it was.</summary>
    public Action<BoxerHealth, BoxerHealth.Zone, float> OnLandedOnBoxer;

    /// <summary>0 = left, 1 = right. Set by <see cref="BoxerHitboxes"/>; falls back to the object name.</summary>
    public int Hand { get; set; } = -1;

    private BoxerPunchController owner;   // whose glove this is, so we never punch ourselves
    private BoxerHealth ownerHealth;
    private SphereCollider sensor;
    private Rigidbody body;
    private Vector3 lastPosition;
    private bool hasLastPosition;
    private bool armed;
    private bool landedThisStrike;
    private float nextHitTime;
    private readonly RaycastHit[] sweepHits = new RaycastHit[32];
    private readonly Collider[] overlapHits = new Collider[32];

    /// <summary>Character scale — speed ceilings are defined at 1x and scaled up for a 2x rig.</summary>
    private float SpeedScale
    {
        get
        {
            Vector3 s = transform.lossyScale;
            return Mathf.Max(0.1f, Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))));
        }
    }

    /// <summary>Configure from code (used by <see cref="BoxerHitboxes"/>).</summary>
    public void Configure(float radius, float effectiveMass, float minSpeed, float power, float maxImpulse, float cooldown)
    {
        this.radius = radius;
        this.effectiveMass = effectiveMass;
        this.minSpeed = minSpeed;
        this.power = power;
        this.maxImpulse = maxImpulse;
        this.cooldown = cooldown;
        ApplyRadius();
    }

    private void Awake()
    {
        owner = GetComponentInParent<BoxerPunchController>();
        ownerHealth = GetComponentInParent<BoxerHealth>();
        sensor = GetComponent<SphereCollider>();
        sensor.isTrigger = true;
        ApplyRadius();

        // A kinematic body makes the moving trigger generate reliable events.
        body = GetComponent<Rigidbody>();
        if (body == null) body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.None;
    }

    private void OnEnable()
    {
        hasLastPosition = false;
        Velocity = Vector3.zero;
    }

    private void OnDisable()
    {
        armed = false;
        hasLastPosition = false;
        Velocity = Vector3.zero;
    }

    private void FixedUpdate()
    {
        Vector3 position = transform.position;
        if (hasLastPosition)
        {
            Vector3 raw = (position - lastPosition) / Time.fixedDeltaTime;
            Velocity = Vector3.Lerp(Velocity, raw, 0.6f); // light smoothing; animation sampling is a bit steppy
            SweepBoxerZones(position);
        }
        lastPosition = position;
        hasLastPosition = true;
    }

    /// <summary>
    /// A fast glove can cross a whole hit zone between trigger samples, so an armed punch also sweeps the
    /// segment it travelled this step — and re-checks the spot it started from, for a glove that was already
    /// inside a zone when the window opened. Every candidate still goes through <see cref="TryHitBoxer"/>, so
    /// the cooldown and the one-hit-per-strike latch stay the authority.
    /// </summary>
    private void SweepBoxerZones(Vector3 position)
    {
        if (!armed || landedThisStrike || Time.time < nextHitTime) return;
        if (ownerHealth == null) ownerHealth = GetComponentInParent<BoxerHealth>();

        Vector3 delta = position - lastPosition;
        float distance = delta.magnitude;
        float maxStep = Mathf.Max(0.25f, maxImpactSpeed * SpeedScale * Mathf.Max(Time.fixedDeltaTime, 0.0001f) * 2f);
        if (distance > maxStep) return;   // a teleport is not a swing — never sweep across it

        if (distance > 0.0001f)
        {
            int count = Physics.SphereCastNonAlloc(lastPosition, radius, delta / distance, sweepHits,
                distance, ~0, QueryTriggerInteraction.Collide);
            BoxerHitZone best = null;
            Vector3 bestPoint = Vector3.zero;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                BoxerHitZone zone = sweepHits[i].collider != null
                    ? sweepHits[i].collider.GetComponentInParent<BoxerHitZone>() : null;
                if (zone == null || zone.Health == null || zone.Health == ownerHealth || zone.Health.IsDown) continue;
                if (sweepHits[i].distance < bestDistance)
                {
                    bestDistance = sweepHits[i].distance;
                    best = zone;
                    bestPoint = sweepHits[i].point;
                }
            }
            if (best != null && TryHitBoxer(best, bestPoint)) return;
        }

        int overlaps = Physics.OverlapSphereNonAlloc(position, radius, overlapHits, ~0, QueryTriggerInteraction.Collide);
        BoxerHitZone nearest = null;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < overlaps; i++)
        {
            BoxerHitZone zone = overlapHits[i] != null
                ? overlapHits[i].GetComponentInParent<BoxerHitZone>() : null;
            if (zone == null || zone.Health == null || zone.Health == ownerHealth || zone.Health.IsDown) continue;
            float d = (overlapHits[i].ClosestPoint(position) - position).sqrMagnitude;
            if (d < nearestDistance) { nearestDistance = d; nearest = zone; }
        }
        if (nearest != null) TryHitBoxer(nearest, position);
    }

    // Stay rather than Enter: a guard hand may already be inside the bag's collider when the punch starts.
    private void OnTriggerStay(Collider other)
    {
        if (!isActiveAndEnabled) return;
        PunchingBag bag = other.GetComponentInParent<PunchingBag>();
        if (bag != null && bag.IsBuilt)
        {
            if (armed && !landedThisStrike && Time.time >= nextHitTime)
            {
                if (Evaluate(bag, other, other.ClosestPoint(transform.position))) return;
            }

            // Not a punch: the glove is simply against the leather. Ease the bag away, keep the dent alive.
            float depth = bag.Penetration(transform.position, out Vector3 surface, out Vector3 inward);
            if (depth > -radius)
                bag.Lean(HandIndex(), surface, inward, Mathf.Max(0.01f, depth + radius * 0.5f), CurrentVelocity());
            return;
        }

        // …or another fighter. Same measurement, different receiver.
        if (armed && !landedThisStrike && Time.time >= nextHitTime)
        {
            BoxerHitZone zone = other.GetComponentInParent<BoxerHitZone>();
            if (zone != null && zone.Health != null) EvaluateBoxer(zone, other.ClosestPoint(transform.position));
        }
    }

    private void OnTriggerExit(Collider other)
    {
        PunchingBag bag = other.GetComponentInParent<PunchingBag>();
        if (bag != null) bag.ClearContact(HandIndex());
    }

    private int HandIndex() => Hand >= 0 ? Hand : (name.Contains("Left") ? 0 : 1);

    /// <summary>The hand's velocity, ceiling-checked: the controller's exact drive if it gave one, else measured.</summary>
    private Vector3 CurrentVelocity()
    {
        Vector3 v = DrivenVelocity ?? Velocity;
        return Vector3.ClampMagnitude(v, maxImpactSpeed * SpeedScale);
    }

    /// <summary>
    /// Land now at a known surface point, if the glove is moving into the bag fast enough. The punch controller
    /// calls this on the exact frame the knuckles cross the bag surface: the trigger only sees the bag at the
    /// physics rate, and a 9 m/s fist moves 18 cm between physics steps. The trigger remains as a fallback.
    /// </summary>
    public bool TryHit(PunchingBag bag, Vector3 contactPoint)
    {
        if (!armed || landedThisStrike || Time.time < nextHitTime) return false;
        if (bag == null || !bag.IsBuilt || bag.BagCollider == null) return false;
        return Evaluate(bag, bag.BagCollider, contactPoint, true);
    }

    private bool Evaluate(PunchingBag bag, Collider bagCollider, Vector3 point, bool surfaceConfirmed = false)
    {
        Vector3 velocity = CurrentVelocity();
        float handSpeed = velocity.magnitude;
        if (handSpeed < minSpeed)
        {
            // Frame-exact SURFACE contact on an armed punch TAPS instead of whiffing — the knuckles are
            // provably in the leather, so a slow finish still transfers something. Plain trigger overlaps
            // (no surface proof) keep the strict gate so guard drift can never punch.
            if (!surfaceConfirmed) return false;
            Vector3 fallback = handSpeed > 0.2f ? velocity / handSpeed
                             : (bagCollider.bounds.center - transform.position).normalized;
            velocity = fallback * minSpeed;
            handSpeed = minSpeed;
        }
        Vector3 direction = velocity / handSpeed;

        // Must be moving into the bag, not pulling back out of it.
        Vector3 toCenter = bagCollider.bounds.center - transform.position;
        if (Vector3.Dot(direction, toCenter) <= 0f) return false;

        // Timing: what matters is fist speed relative to the bag surface along the punch line. A punch CHASING
        // a bag that is swinging away still connects — it lands soft, it never lands silent. Only a fist that
        // is not actually CLOSING on the surface is rejected (the hand-speed gate above already proved this is
        // a thrown punch, not a drifting guard).
        float relativeSpeed = Vector3.Dot(velocity - bag.PointVelocity(point), direction);
        if (relativeSpeed <= 0.25f) return false;
        relativeSpeed = Mathf.Min(Mathf.Max(relativeSpeed, 1f), maxImpactSpeed * SpeedScale);

        // Placement: 1 = straight through the (horizontal) centre, 0 = glancing.
        Vector3 flatDir = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
        Vector3 flatToCenter = Vector3.ProjectOnPlane(toCenter, Vector3.up).normalized;
        float cleanliness = flatDir.sqrMagnitude > 0f && flatToCenter.sqrMagnitude > 0f
            ? Mathf.Clamp01(Vector3.Dot(flatDir, flatToCenter))
            : 1f;

        float impulse = Mathf.Min(effectiveMass * relativeSpeed * power * PowerScale, maxImpulse);
        int hand = HandIndex();
        float overdrive = owner != null ? owner.Overdrive((BoxerPunchController.Hand)hand) : 0f;

        float applied = bag.Hit(point, direction, impulse, cleanliness, relativeSpeed, hand, overdrive);
        landedThisStrike = true;
        nextHitTime = Time.time + cooldown;

        // ONE HitInfo per hit: the bag's own record (set inside Hit) carries the counter/surface fields and
        // the feedback-floored strength — hand-building a second, poorer copy here starved every listener.
        OnLanded.Invoke(this, bag, bag.LastHit);
        return true;
    }

    /// <summary>
    /// Land on a boxer NOW at a known point. The punch controller calls this on the exact frame the knuckles
    /// cross him: the trigger only samples at the physics rate and a 9 m/s glove crosses 9 cm between steps, so
    /// a fast clean punch could otherwise pass straight through a head without ever registering.
    /// </summary>
    public bool TryHitBoxer(BoxerHitZone zone, Vector3 point)
    {
        if (!armed || landedThisStrike || Time.time < nextHitTime) return false;
        return zone != null && EvaluateBoxer(zone, point);
    }

    /// <summary>
    /// A punch on another boxer, measured the same way as a punch on the bag: relative speed along the punch
    /// line, effective mass, how squarely it went in. The zone decides what that energy does to him, not this.
    /// </summary>
    private bool EvaluateBoxer(BoxerHitZone zone, Vector3 point)
    {
        BoxerHealth health = zone != null ? zone.Health : null;
        if (ownerHealth == null) ownerHealth = GetComponentInParent<BoxerHealth>();
        if (!PunchPlayback.CanDamageFighter(ownerHealth != null ? 1 : 0, health != null ? 1 : 0,
                armed, landedThisStrike, health != null && health.IsDown)
            || ReferenceEquals(health, ownerHealth)) return false;
        // Never punch yourself: the guard hand sits inside your own head zone all round.
        if (owner != null && health.GetComponent<BoxerPunchController>() == owner) return false;

        Vector3 velocity = CurrentVelocity();
        float handSpeed = velocity.magnitude;
        if (handSpeed < minSpeed) return false;
        Vector3 direction = velocity / handSpeed;

        Vector3 toTarget = point - transform.position;
        if (toTarget.sqrMagnitude < 0.000001f)
        {
            Collider zoneCollider = zone.GetComponent<Collider>();
            toTarget = (zoneCollider != null ? zoneCollider.bounds.center : health.transform.position)
                - transform.position;
        }
        if (Vector3.Dot(direction, toTarget) <= 0f) return false;   // pulling out, not driving in

        // A moving opponent changes the timing exactly like a swinging bag does.
        Vector3 targetVelocity = Vector3.zero;
        AimStudyBoxer studyTarget = health.GetComponent<AimStudyBoxer>();
        if (studyTarget != null) targetVelocity = studyTarget.MovementVelocity;
        else
        {
            Controller moving = health.GetComponent<Controller>();
            if (moving != null) targetVelocity = moving.MoveDirection * moving.CurrentSpeed;
        }
        float relativeSpeed = Vector3.Dot(velocity - targetVelocity, direction);
        if (relativeSpeed < minSpeed) return false;
        relativeSpeed = Mathf.Min(relativeSpeed, maxImpactSpeed * SpeedScale);

        Vector3 flatDir = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
        Vector3 flatToTarget = Vector3.ProjectOnPlane(health.transform.position - transform.position, Vector3.up).normalized;
        float cleanliness = flatDir.sqrMagnitude > 0f && flatToTarget.sqrMagnitude > 0f
            ? Mathf.Clamp01(Vector3.Dot(flatDir, flatToTarget))
            : 1f;

        float impulse = Mathf.Min(effectiveMass * relativeSpeed * power * PowerScale, maxImpulse);
        landedThisStrike = true;
        nextHitTime = Time.time + cooldown;

        health.ReceiveHit(zone.Zone, point, direction, impulse, cleanliness, relativeSpeed, owner);
        OnLandedOnBoxer?.Invoke(health, zone.Zone, Mathf.Clamp01(impulse / maxImpulse));
        return true;
    }

    private void ApplyRadius()
    {
        if (sensor == null) sensor = GetComponent<SphereCollider>();
        if (sensor == null) return;
        // SphereCollider radius is scaled by the largest axis of lossyScale; compensate so 'radius' is in metres.
        Vector3 s = transform.lossyScale;
        float maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z), 0.0001f);
        sensor.radius = radius / maxScale;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Armed ? new Color(1f, 0.3f, 0.3f, 0.8f) : new Color(1f, 1f, 1f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, radius);
    }
}
