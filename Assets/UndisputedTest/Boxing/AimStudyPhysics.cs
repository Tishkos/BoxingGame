using FIMSpace.FProceduralAnimation;
using RootMotion.Dynamics;
using UnityEngine;

/// <summary>
/// The bridge between the aim-study boxer and its PuppetMaster ragdoll.
///
/// Alive, the mapping is deliberately asymmetric: hips, legs and feet stay pure animation
/// (mappingWeightMlp 0) because Legs Animator is the ONLY thing placing feet — a mapped leg
/// would fight the glue targets. Head gets a light map, torso and arms a firm one, so a landed
/// shot visibly rocks him without the simulation ever owning the stance. Pins keep the ragdoll
/// upright under the animation; muscle force is what punches push against.
///
/// On a knockdown every muscle maps fully and drops its pin, Legs Animator is told it is
/// ragdolled, and the real colliders do the falling. A watchdog watches for the puppet drifting
/// implausibly far from its targets — a sign the rig needs tuning — and falls back to kinematic
/// rather than letting a broken puppet explode the pose.
/// </summary>
[RequireComponent(typeof(AimStudyBoxer))]
[RequireComponent(typeof(BoxerHealth))]
[DefaultExecutionOrder(-10)]
[DisallowMultipleComponent]
public class AimStudyPhysics : MonoBehaviour
{
    public PuppetMaster Puppet;
    public LegsAnimator Legs;

    /// <summary>True once the watchdog has parked the puppet — the rig needs its tuning checked.</summary>
    public bool SafetyFallback { get; private set; }

    private AimStudyBoxer boxer;
    private BoxerHealth health;
    private CharacterController controller;
    private Animator animator;
    private float weakenUntil;
    private float worstSince = -1f;
    private bool collisionsIgnored;
    private bool down;

    private void Awake()
    {
        boxer = GetComponent<AimStudyBoxer>();
        health = GetComponent<BoxerHealth>();
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();
    }

    private void Start()
    {
        if (Legs == null) Legs = GetComponentInChildren<LegsAnimator>(true);
        if (Puppet == null)
        {
            Debug.LogError("AimStudyPhysics: no PuppetMaster assigned — run Tools > Undisputed > " +
                "Boxing > Setup Fight With Selected Aim Study Boxer. Without it there is no ragdoll KO.", this);
            enabled = false;
            return;
        }
        if (Puppet.targetRoot != transform)
        {
            Debug.LogError("AimStudyPhysics: the assigned PuppetMaster follows a different target " +
                "root — assign the puppet built for this boxer.", this);
            enabled = false;
            return;
        }
        if (animator != null && animator.updateMode != AnimatorUpdateMode.Normal)
        {
            Debug.LogError("AimStudyPhysics: the Animator must stay on Normal update mode — " +
                "PuppetMaster reads targets in LateUpdate.", this);
            enabled = false;
            return;
        }
        Puppet.OnRead += TuneMuscles;
        Puppet.mode = PuppetMaster.Mode.Active;
        Puppet.mappingWeight = 1f;
    }

    private void OnEnable()
    {
        if (Puppet != null && Puppet.initiated && !SafetyFallback)
        {
            Puppet.mode = PuppetMaster.Mode.Active;
            Puppet.mappingWeight = 1f;
        }
    }

    private void OnDisable()
    {
        if (Puppet != null)
        {
            Puppet.OnRead -= TuneMuscles;
            if (Puppet.initiated)
            {
                Puppet.mappingWeight = 0f;
                Puppet.mode = PuppetMaster.Mode.Kinematic;
            }
        }
        if (Legs != null && Legs.LegsInitialized) Legs.User_SetIsRagdolled(false);
    }

    /// <summary>
    /// Runs inside PuppetMaster's read — every frame in Normal update mode, before the muscles
    /// read their targets, so the weights here are what the solver actually uses this frame.
    /// </summary>
    private void TuneMuscles()
    {
        if (Puppet == null || !Puppet.initiated) return;

        if (!collisionsIgnored)
        {
            collisionsIgnored = true;
            IgnoreTargetCollisions();
        }

        down = health != null && health.IsDown;
        Puppet.mappingWeight = 1f;
        Puppet.pinWeight = 1f;
        Puppet.muscleWeight = 1f;
        foreach (Muscle m in Puppet.muscles)
        {
            if (m == null) continue;
            m.props.mappingWeight = 1f;
            bool groundedChain = m.props.group == Muscle.Group.Hips
                || m.props.group == Muscle.Group.Leg || m.props.group == Muscle.Group.Foot;
            if (down)
            {
                m.state.mappingWeightMlp = 1f;
                m.state.pinWeightMlp = 0f;
                m.state.muscleWeightMlp = 0.08f;
            }
            else
            {
                m.state.mappingWeightMlp = groundedChain ? 0f
                    : m.props.group == Muscle.Group.Head ? 0.55f : 0.8f;
                m.state.pinWeightMlp = groundedChain ? 1f : (boxer.IsDefending ? 1f : 0.8f);
                m.state.muscleWeightMlp = Mathf.Lerp(0.65f, 1f, boxer.Stamina01)
                    * (Time.time < weakenUntil ? 0.65f : 1f);
            }
        }

        if (!down && !SafetyFallback) Watchdog();
    }

    /// <summary>
    /// The ragdoll's colliders must never shove the animated target or the other fighter's
    /// controller capsule — physics pushes are the punch's job, not incidental contact's.
    /// </summary>
    private void IgnoreTargetCollisions()
    {
        if (Puppet == null) return;
        Collider[] physical = Puppet.GetComponentsInChildren<Collider>(true);
        AimStudyBoxer opponent = boxer != null ? boxer.Opponent : null;
        foreach (Collider muscleCollider in physical)
        {
            if (muscleCollider == null || muscleCollider.isTrigger) continue;
            foreach (Collider targetCollider in GetComponentsInChildren<Collider>(true))
                if (targetCollider != null && !targetCollider.isTrigger)
                    Physics.IgnoreCollision(muscleCollider, targetCollider, true);
            if (opponent == null) continue;
            foreach (Collider targetCollider in opponent.GetComponentsInChildren<Collider>(true))
                if (targetCollider != null && !targetCollider.isTrigger)
                    Physics.IgnoreCollision(muscleCollider, targetCollider, true);
        }
    }

    /// <summary>
    /// A puppet whose muscles sit far from their targets for a sustained stretch is broken rig
    /// tuning, not physics doing its job — park it kinematic rather than watch it explode.
    /// </summary>
    private void Watchdog()
    {
        float scale = Mathf.Max(0.1f, transform.lossyScale.y);
        float worst = 0f;
        foreach (Muscle m in Puppet.muscles)
        {
            if (m == null || m.rigidbody == null || m.target == null) continue;
            float distance = Vector3.Distance(m.rigidbody.position, m.target.position);
            if (distance > worst) worst = distance;
        }
        if (worst > 1.2f * scale)
        {
            if (worstSince < 0f) worstSince = Time.time;
            else if (Time.time - worstSince > 0.25f)
            {
                SafetyFallback = true;
                Puppet.mappingWeight = 0f;
                Puppet.mode = PuppetMaster.Mode.Kinematic;
                Debug.LogWarning("AimStudyPhysics: the puppet drifted too far from the animation " +
                    "and was parked kinematic — check muscle spring, pin range and joint limits.", this);
            }
        }
        else worstSince = -1f;
    }

    // After the boxer moves (-20), before Legs Animator reads its flags (-7).
    private void Update()
    {
        bool downed = health != null && health.IsDown;
        if (Legs == null || !Legs.LegsInitialized) return;
        Vector3 velocity = boxer != null ? boxer.MovementVelocity : Vector3.zero;
        Legs.User_SetIsMoving(!downed && velocity.sqrMagnitude > 0.01f);
        if (velocity.sqrMagnitude > 0.0001f) Legs.User_SetDesiredMovementDirection(velocity.normalized);
        Legs.User_SetIsGrounded(controller == null || controller.isGrounded);
        Legs.User_SetIsRagdolled(downed);
    }

    /// <summary>Down: release the pins and let the muscles fall. Up: hand control back.</summary>
    public void SetDown(bool isDown)
    {
        down = isDown;
        if (controller != null) controller.enabled = !isDown;
        if (Legs != null && Legs.LegsInitialized) Legs.User_SetIsRagdolled(isDown);
        if (Puppet != null && Puppet.initiated) TuneMuscles();
    }

    /// <summary>
    /// A restrained physical assist on the muscle nearest the contact — the glove sensor already
    /// did the damage; this just makes the ragdoll visibly feel it.
    /// </summary>
    public void Impact(Vector3 point, Vector3 direction, float power, bool headHit)
    {
        if (Puppet == null || !Puppet.initiated || SafetyFallback) return;
        Muscle nearest = null;
        float best = float.MaxValue;
        foreach (Muscle m in Puppet.muscles)
        {
            if (m == null || m.rigidbody == null || m.target == null) continue;
            float d = (m.target.position - point).sqrMagnitude;
            if (d < best) { best = d; nearest = m; }
        }
        if (nearest == null) return;
        nearest.rigidbody.AddForceAtPosition(
            direction.normalized * Mathf.Min(10f, power * 8f), point, ForceMode.Impulse);
        if (headHit) weakenUntil = Time.time + 0.6f * power;
    }

    /// <summary>Next bout: puppet back on its feet on the start mark, safety latch cleared.</summary>
    public void ResetPose(Vector3 position, Quaternion rotation)
    {
        SafetyFallback = false;
        weakenUntil = 0f;
        worstSince = -1f;
        down = false;
        if (Puppet == null) return;
        if (Puppet.targetRoot != transform)
        {
            Debug.LogError("AimStudyPhysics: puppet target root mismatch — refusing reset.", this);
            return;
        }
        if (Puppet.initiated)
        {
            Puppet.mode = PuppetMaster.Mode.Active;
            Puppet.mappingWeight = 1f;
            Puppet.pinWeight = 1f;
            Puppet.muscleWeight = 1f;
            Puppet.Teleport(position, rotation, true);
            foreach (Muscle m in Puppet.muscles)
            {
                if (m == null || m.rigidbody == null) continue;
                m.rigidbody.linearVelocity = Vector3.zero;
                m.rigidbody.angularVelocity = Vector3.zero;
            }
        }
        if (controller != null) controller.enabled = true;
        if (Legs != null && Legs.LegsInitialized) Legs.User_SetIsRagdolled(false);
    }
}
