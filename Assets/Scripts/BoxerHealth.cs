using System;
using UnityEngine;

/// <summary>
/// Marks one hit zone on a boxer so a glove can tell a jaw from a body from a raised guard (req.md §30).
/// Created by <see cref="BoxerHealth"/>; you never add these by hand.
/// </summary>
public class BoxerHitZone : MonoBehaviour
{
    public BoxerHealth Health;
    public BoxerHealth.Zone Zone = BoxerHealth.Zone.Body;
}

/// <summary>
/// What it takes to hurt a boxer, and what happens when you do. This is what makes a second character a
/// FIGHTER rather than a mannequin: it gives him zones to hit, a chin that can be rocked, a body that drains
/// him, legs that stop holding him up, and a count.
///
/// req.md §30 is the spine of it — different zones feed DIFFERENT systems rather than a single damage number:
///
///     JAW / TEMPLE  →  concussion. Rocks the head, weakens every muscle for a moment, and repeated shots put
///                      him down even while his "health" is fine. This is what actually ends fights.
///     FOREHEAD      →  mostly displacement. Hurts less, moves him more.
///     BODY          →  stamina and breathing. Does not knock anyone out on its own; it takes the legs away so
///                      the head shot lands later.
///     LIVER         →  the body shot that DOES end things — heavy stamina hit plus a delayed buckle.
///     GUARD         →  he blocked it. Costs him stamina and pushes him, and a big enough shot drives his own
///                      glove into his face anyway.
///
/// Nothing here reads input, so it works identically on the player and on an AI opponent.
/// </summary>
[DisallowMultipleComponent]
public class BoxerHealth : MonoBehaviour
{
    public enum Zone { Head, Jaw, Body, Liver, Guard }

    [Header("Condition")]
    [Tooltip("Body condition. Body shots eat this; at zero he cannot hold a guard or a stance any more.")]
    [Min(1f)] [SerializeField] private float maxHealth = 100f;

    [Tooltip("Concussion. Head shots fill it, it drains between exchanges, and a full meter is a knockdown — " +
             "this is what actually ends fights, not the health bar (req.md §30, §31).")]
    [Min(1f)] [SerializeField] private float maxConcussion = 100f;
    [Min(0f)] [SerializeField] private float concussionRecovery = 9f;
    [Min(0f)] [SerializeField] private float healthRecovery = 1.5f;

    [Header("What each zone does (per unit of impact energy)")]
    [SerializeField] private float jawConcussion = 26f;
    [SerializeField] private float headConcussion = 13f;
    [SerializeField] private float jawDamage = 5f;
    [SerializeField] private float headDamage = 4f;
    [SerializeField] private float bodyDamage = 9f;
    [SerializeField] private float liverDamage = 15f;
    [Tooltip("Stamina a body shot takes straight out of the lungs.")]
    [SerializeField] private float bodyStamina = 16f;
    [SerializeField] private float liverStamina = 30f;
    [Tooltip("Fraction of everything that still gets through a blocked punch.")]
    [Range(0f, 1f)] [SerializeField] private float guardLeak = 0.18f;

    [Header("Reaction")]
    [Tooltip("How hard a landed punch shoves him (m/s per unit of impact).")]
    [Min(0f)] [SerializeField] private float knockback = 0.55f;
    [Min(0f)] [SerializeField] private float maxKnockback = 3f;
    [Tooltip("Impact strength that counts as a full-power shot for the reaction.")]
    [Min(0.1f)] [SerializeField] private float fullPowerImpulse = 90f;

    [Header("Going down")]
    [Tooltip("Seconds on the floor before he tries to get up.")]
    [Min(0.5f)] [SerializeField] private float downTime = 3f;
    [Tooltip("How many knockdowns before he stays down. 0 = he always gets up.")]
    [Min(0)] [SerializeField] private int knockdownsToLose = 3;
    [Tooltip("Concussion he gets back on standing up — he is never fresh after a knockdown.")]
    [Range(0f, 1f)] [SerializeField] private float recoveryOnRise = 0.45f;

    [Header("Zones (built at start from the humanoid bones)")]
    [SerializeField] private bool buildZones = true;
    [Min(0.02f)] [SerializeField] private float headRadius = 0.13f;
    [Min(0.02f)] [SerializeField] private float bodyRadius = 0.19f;

    // ------------------------------------------------------------------ State

    public float Health { get; private set; }
    public float Concussion { get; private set; }
    public float Health01 => Mathf.Clamp01(Health / maxHealth);
    public float Concussion01 => Mathf.Clamp01(Concussion / maxConcussion);

    /// <summary>On the floor.</summary>
    public bool IsDown { get; private set; }

    /// <summary>Out of the fight — he is not getting up again.</summary>
    public bool IsOut { get; private set; }

    public int Knockdowns { get; private set; }

    /// <summary>Raised on every landed hit: zone, and how heavy it was (0-1).</summary>
    public event Action<Zone, float> Hurt;

    /// <summary>Raised when he goes down (true) and when he rises (false). Stays true forever on a KO.</summary>
    public event Action<bool> DownChanged;

    /// <summary>Raised once when he is counted out.</summary>
    public event Action KnockedOut;

    private BoxerPunchController boxer;
    private Controller locomotion;
    private Animator animator;
    private AimStudyBoxer study;
    private AimStudyCombat studyCombat;
    private float riseAt;
    private float lastHitTime = -10f;

    // ------------------------------------------------------------------ Setup

    private void Awake()
    {
        boxer = GetComponent<BoxerPunchController>();
        locomotion = GetComponent<Controller>();
        animator = GetComponent<Animator>();
        study = GetComponent<AimStudyBoxer>();
        studyCombat = GetComponent<AimStudyCombat>();
        Health = maxHealth;
        Concussion = 0f;
        if (buildZones) BuildZones();
    }

    /// <summary>
    /// Trigger spheres on the head and torso bones. Triggers, not colliders: they must never push the boxer
    /// around or fight the CharacterController — they exist purely so a glove knows what it just hit.
    /// </summary>
    private void BuildZones()
    {
        if (animator == null || !animator.isHuman)
        {
            Debug.LogWarning("BoxerHealth: needs a Humanoid Animator to build hit zones.", this);
            return;
        }

        float scale = Mathf.Max(0.01f, transform.lossyScale.y);
        AddZone(HumanBodyBones.Head, Zone.Jaw, headRadius, new Vector3(0f, 0.02f, 0.04f));
        AddZone(HumanBodyBones.Head, Zone.Head, headRadius * 1.05f, new Vector3(0f, 0.08f, -0.01f));
        AddZone(HumanBodyBones.Chest, Zone.Body, bodyRadius, Vector3.zero);
        AddZone(HumanBodyBones.Spine, Zone.Liver, bodyRadius * 0.62f, new Vector3(0.07f, -0.02f, 0.03f));
    }

    private void AddZone(HumanBodyBones bone, Zone zone, float radius, Vector3 localOffset)
    {
        Transform t = animator.GetBoneTransform(bone);
        if (t == null) return;

        GameObject go = new GameObject($"Hit Zone ({zone})") { layer = gameObject.layer };
        go.transform.SetParent(t, false);
        go.transform.localPosition = localOffset;

        SphereCollider sphere = go.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        Vector3 s = go.transform.lossyScale;
        float maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z), 0.0001f);
        sphere.radius = radius / maxScale;

        BoxerHitZone tag = go.AddComponent<BoxerHitZone>();
        tag.Health = this;
        tag.Zone = zone;
    }

    // ------------------------------------------------------------------ Taking it

    private void Update()
    {
        float dt = Time.deltaTime;

        if (IsDown)
        {
            if (!IsOut && Time.time >= riseAt) Rise();
            return;
        }

        // Between exchanges he clears his head and gets some of it back. Standing still recovers faster than
        // being under fire, which is what makes backing off a real tactic.
        float quiet = Mathf.Clamp01((Time.time - lastHitTime) / 1.5f);
        Concussion = Mathf.Max(0f, Concussion - concussionRecovery * quiet * dt);
        Health = Mathf.Min(maxHealth, Health + healthRecovery * quiet * dt);
    }

    /// <summary>
    /// A punch landed on this boxer. <paramref name="impulse"/> is the impact energy the glove measured — real
    /// relative velocity and effective mass, never anything derived from input (req.md §6, §16).
    /// </summary>
    public void ReceiveHit(Zone zone, Vector3 point, Vector3 direction, float impulse, float cleanliness,
                           float speed, BoxerPunchController attacker)
    {
        if (IsDown) return;

        float facing = Vector3.Dot(Vector3.ProjectOnPlane(-direction, Vector3.up).normalized, transform.forward);
        float coverage = zone == Zone.Guard ? 1f
            : study != null
                ? PunchPlayback.GuardCoverage(zone == Zone.Jaw || zone == Zone.Head,
                    study.HeadGuardWeight, study.BodyGuardWeight, facing)
                : boxer != null && boxer.IsBlocking && facing > 0.25f ? 1f : 0f;
        bool blocked = coverage >= 0.5f;
        float power = PunchPlayback.GuardedPower(
            Mathf.Clamp01(impulse / fullPowerImpulse) * Mathf.Lerp(0.55f, 1f, cleanliness), coverage, guardLeak);
        lastHitTime = Time.time;

        switch (blocked ? Zone.Guard : zone)
        {
            case Zone.Jaw:
                Concussion += jawConcussion * power;
                Health -= jawDamage * power;
                break;
            case Zone.Head:
                Concussion += headConcussion * power;
                Health -= headDamage * power;
                break;
            case Zone.Body:
                Health -= bodyDamage * power;
                DrainStamina(bodyStamina * power);
                break;
            case Zone.Liver:
                Health -= liverDamage * power;
                DrainStamina(liverStamina * power);
                break;
            case Zone.Guard:
                DrainStamina(bodyStamina * power * 0.6f);
                break;
        }

        Health = Mathf.Max(0f, Health);
        Concussion = Mathf.Min(maxConcussion, Concussion);

        // The physical reaction goes through exactly the same path a bag swinging into him would — knockback,
        // stagger, the interrupted wind-up, the muscle-weakening stun.
        if (boxer != null) boxer.ReceiveStrike(point, direction, speed, power, blocked, zone == Zone.Jaw || zone == Zone.Head);
        if (studyCombat != null)
            studyCombat.ReceiveImpact(point, direction, power, blocked, zone == Zone.Jaw || zone == Zone.Head);
        Vector3 shove = Vector3.ProjectOnPlane(direction, Vector3.up).normalized *
                        Mathf.Min(impulse * knockback * (blocked ? 0.35f : 1f), maxKnockback);
        if (locomotion != null && !IsDown) locomotion.AddImpulse(shove);
        if (study != null) study.AddImpulse(shove);

        Hurt?.Invoke(blocked ? Zone.Guard : zone, power);

        // A full chin, or a body that has nothing left, and the legs go (req.md §33).
        if (Concussion >= maxConcussion || Health <= 0f) GoDown();
    }

    private bool IsInFront(Vector3 direction)
        => Vector3.Dot(Vector3.ProjectOnPlane(-direction, Vector3.up).normalized, transform.forward) > 0.25f;

    private void DrainStamina(float amount)
    {
        if (study != null) study.SpendStaminaExternal(amount);
        else if (boxer != null) boxer.SpendStaminaExternal(amount);
    }

    // ------------------------------------------------------------------ Down and up

    public void GoDown()
    {
        if (IsDown) return;
        IsDown = true;
        Knockdowns++;
        riseAt = Time.time + downTime;

        // The active ragdoll does the falling if there is one; otherwise he simply stops fighting.
        boxer?.PhysicsBody?.KnockDown();
        studyCombat?.SetDown(true);
        if (locomotion != null) locomotion.SpeedMultiplier = 0f;

        if (knockdownsToLose > 0 && Knockdowns >= knockdownsToLose)
        {
            IsOut = true;
            KnockedOut?.Invoke();
        }
        DownChanged?.Invoke(true);
    }

    private void Rise()
    {
        IsDown = false;
        Concussion = maxConcussion * (1f - recoveryOnRise);
        Health = Mathf.Max(Health, maxHealth * 0.25f);
        studyCombat?.SetDown(false);
        if (locomotion != null) locomotion.SpeedMultiplier = 1f;
        DownChanged?.Invoke(false);
    }

    /// <summary>Put him back together (round reset, respawn).</summary>
    public void ResetFighter()
    {
        if (IsDown) Rise();
        Health = maxHealth;
        Concussion = 0f;
        Knockdowns = 0;
        IsOut = false;
    }
}
