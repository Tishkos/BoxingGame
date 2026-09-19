using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Puts a <see cref="PunchHitbox"/> on each hand of a humanoid boxer so its punches land on any
/// <see cref="PunchingBag"/> in the scene. The sensor sits at the glove's STRIKING FACE (wrist + Knuckle Offset),
/// not on the wrist bone, so the bag reacts on the frame the leather visibly touches it.
/// Attach next to the Animator on the character root.
///
/// Also exposes <see cref="PunchStart"/> / <see cref="PunchEnd"/> for Animation Events (for driving the strike
/// window from animation when no BoxerPunchController owns the hands), and a debug key that throws a synthetic
/// punch at the nearest bag so you can tune the bag on its own.
/// </summary>
[RequireComponent(typeof(Animator))]
public class BoxerHitboxes : MonoBehaviour
{
    [Header("Glove sensors")]
    [Tooltip("Radius of the glove's striking face (metres). Small and at the knuckles, so contact registers where the leather is.")]
    [Min(0.01f)]
    [SerializeField] private float gloveFaceRadius = 0.055f;

    [Tooltip("Distance from the wrist bone to the glove's striking face (metres). BoxerPunchController overrides this with its Fist Length.")]
    [Range(0f, 0.2f)]
    [SerializeField] private float knuckleOffset = 0.09f;

    [Tooltip("Mass behind each punch in kg: fist, glove, forearm and the shoulder driving them. 5-8 for a boxer; the controller multiplies it by technique.")]
    [Min(0.1f)]
    [SerializeField] private float punchMass = 6f;

    [Tooltip("Relative speed below this (m/s) is ignored so guard/idle motion does not nudge the bag.")]
    [Min(0f)]
    [SerializeField] private float minSpeed = 2.5f;

    [Tooltip("Scales every impulse. 1 = physically plausible.")]
    [Min(0f)]
    [SerializeField] private float power = 1f;

    [Min(1f)]
    [SerializeField] private float maxImpulse = 250f;

    [Min(0f)]
    [SerializeField] private float cooldown = 0.15f;

    [Header("Debug punch (no animation needed)")]
    [Tooltip("Press J to throw a test punch at the nearest bag in front of the boxer (no animation or IK needed).")]
    [SerializeField] private bool debugPunchKey = true;

    [Min(0f)]
    [SerializeField] private float debugImpulse = 80f;

    [Tooltip("How far in front of the boxer the debug punch can reach (metres).")]
    [Min(0.1f)]
    [SerializeField] private float debugReach = 1.4f;

    public PunchHitbox LeftHand { get; private set; }
    public PunchHitbox RightHand { get; private set; }

    /// <summary>Wrist → striking-face distance (metres). BoxerPunchController sets it from its Fist Length so the two agree.</summary>
    public float KnuckleOffset
    {
        get => knuckleOffset;
        set { knuckleOffset = Mathf.Max(0f, value); PlaceSensors(); }
    }

    private Animator animator;
    private bool debugUseLeft;
    private readonly Transform[] handBones = new Transform[2];
    private readonly Vector3[] knuckleDirLocal = new Vector3[2];   // finger direction in the hand bone's local space

    private void Start()
    {
        animator = GetComponent<Animator>();
        LeftHand = CreateHitbox(0, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftLowerArm, "Left Glove Hitbox");
        RightHand = CreateHitbox(1, HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLowerArm, "Right Glove Hitbox");
    }

    private void Update()
    {
        if (!debugPunchKey) return;

        BoxerInput.Poll();
        bool pressed = BoxerInput.DebugPunchDown;
        if (pressed) ThrowDebugPunch();
    }

    // ---------------------------------------------------------------- Animation Events / control

    /// <summary>Animation Event: the punch is extending, hits may land.</summary>
    public void PunchStart() => SetArmed(true);

    /// <summary>Animation Event: the punch is retracting, stop registering hits.</summary>
    public void PunchEnd() => SetArmed(false);

    public void SetArmed(bool armed)
    {
        if (LeftHand != null) LeftHand.Armed = armed;
        if (RightHand != null) RightHand.Armed = armed;
    }

    // ---------------------------------------------------------------- Helpers

    private PunchHitbox CreateHitbox(int index, HumanBodyBones bone, HumanBodyBones finger, HumanBodyBones lowerArm, string objectName)
    {
        Transform boneTransform = animator.isHuman ? animator.GetBoneTransform(bone) : null;
        if (boneTransform == null)
        {
            Debug.LogWarning($"BoxerHitboxes: no {bone} bone found; is the Animator's avatar Humanoid?", this);
            return null;
        }

        GameObject go = new GameObject(objectName) { layer = gameObject.layer };
        go.transform.SetParent(boneTransform, false);

        handBones[index] = boneTransform;
        knuckleDirLocal[index] = boneTransform.InverseTransformDirection(KnuckleDirection(boneTransform, finger, lowerArm));

        go.AddComponent<SphereCollider>();
        PunchHitbox hitbox = go.AddComponent<PunchHitbox>();
        hitbox.Hand = index;
        hitbox.Configure(gloveFaceRadius, punchMass, minSpeed, power, maxImpulse, cooldown);

        Place(index, hitbox);
        return hitbox;
    }

    /// <summary>The way the fingers point from the wrist: from the middle finger if the rig has one, else along the forearm.</summary>
    private Vector3 KnuckleDirection(Transform hand, HumanBodyBones finger, HumanBodyBones lowerArm)
    {
        Transform f = animator.GetBoneTransform(finger);
        Vector3 dir = f != null ? f.position - hand.position : Vector3.zero;
        if (dir.sqrMagnitude < 1e-6f)
        {
            Transform l = animator.GetBoneTransform(lowerArm);
            dir = l != null ? hand.position - l.position : hand.forward;
        }
        return dir.sqrMagnitude > 1e-6f ? dir.normalized : hand.forward;
    }

    private void PlaceSensors()
    {
        Place(0, LeftHand);
        Place(1, RightHand);
    }

    /// <summary>Put the sensor's centre one radius behind the striking face, so its front edge IS the glove's front.</summary>
    private void Place(int index, PunchHitbox hitbox)
    {
        if (hitbox == null || handBones[index] == null) return;
        Vector3 dir = handBones[index].TransformDirection(knuckleDirLocal[index]);
        hitbox.transform.position = handBones[index].position + dir * Mathf.Max(0f, knuckleOffset - gloveFaceRadius);
    }

    private void ThrowDebugPunch()
    {
        PunchingBag[] bags = FindObjectsByType<PunchingBag>();
        PunchingBag best = null;
        float bestDistance = float.MaxValue;

        Vector3 origin = transform.position + Vector3.up;
        foreach (PunchingBag bag in bags)
        {
            if (!bag.IsBuilt) continue;
            Vector3 point = bag.ClosestPoint(origin);
            Vector3 toBag = Vector3.ProjectOnPlane(point - origin, Vector3.up);
            float distance = toBag.magnitude;
            if (distance > debugReach) continue;
            if (Vector3.Dot(toBag.normalized, transform.forward) < 0.3f) continue; // roughly in front
            if (distance < bestDistance) { bestDistance = distance; best = bag; }
        }

        if (best == null) return;

        // Alternate hands; hit at glove height, slightly off-centre so the bag also twists a little like a real hook.
        debugUseLeft = !debugUseLeft;
        PunchHitbox hand = debugUseLeft ? LeftHand : RightHand;
        Vector3 from = hand != null ? hand.transform.position : origin + transform.right * (debugUseLeft ? -0.2f : 0.2f);
        Vector3 contact = best.ClosestPoint(from);
        Vector3 direction = Vector3.ProjectOnPlane(best.transform.position - origin, Vector3.up).normalized;

        best.Hit(contact, direction, debugImpulse, 0.9f, 6f);
    }
}
