using UnityEngine;

/// <summary>
/// THE CALCULATED BALANCE — one number, 0..1, computed from the body's real situation every frame:
///
///   • centre of mass vs the SUPPORT AREA between the feet (the core term),
///   • whether each foot is actually on the ground,
///   • how fast the body is moving,
///   • how far the torso is leaning,
///   • an accumulated DISTURBANCE from punches thrown, extreme angles and hits received (decays on its own —
///     and decays slower the worse the balance already is).
///
/// Consumers (BoxerPunchController): punch power and accuracy at the MOMENT of impact, recovery time, stagger
/// vulnerability, and a stumble chance on extreme throws. Nothing here forces a fall — every consequence
/// scales smoothly with how unbalanced the boxer actually is.
/// Auto-added next to <see cref="BoxerPunchController"/>.
/// </summary>
[DisallowMultipleComponent]
public class BoxerBalance : MonoBehaviour
{
    [Header("Support area")]
    [Tooltip("Radius around each planted foot that counts as support (metres at 1x scale).")]
    [Min(0.05f)] [SerializeField] private float footRadius = 0.16f;

    [Tooltip("How far above the sole the ground may be for the foot to count as PLANTED (metres at 1x).")]
    [Min(0.02f)] [SerializeField] private float plantHeight = 0.14f;

    [Header("Destabilisers")]
    [Tooltip("Speed (m/s at 1x) above which movement starts to cost stability.")]
    [Min(0.1f)] [SerializeField] private float steadySpeed = 1.6f;

    [Tooltip("How much full-tilt movement can cost (fraction of balance).")]
    [Range(0f, 0.6f)] [SerializeField] private float speedCost = 0.25f;

    [Tooltip("How much a full torso lean costs.")]
    [Range(0f, 0.6f)] [SerializeField] private float leanCost = 0.3f;

    [Header("Recovery")]
    [Tooltip("How fast accumulated disturbance drains per second when well balanced (slower when already shaky).")]
    [Min(0.05f)] [SerializeField] private float recoverPerSecond = 0.9f;

    [Tooltip("Smoothing on the published value (seconds) — consequences must never step.")]
    [Range(0.01f, 0.3f)] [SerializeField] private float smoothTime = 0.08f;

    [Header("Debug")]
    [Tooltip("Scene-view gizmos: centre of mass (red→green by balance), the support area, planted feet.")]
    [SerializeField] private bool debugDraw = true;
    [Tooltip("On-screen numbers: live balance, foot contact, and the last impact's calculated power.")]
    [SerializeField] private bool debugOverlay = true;

    /// <summary>The number everything reads: 1 = rooted, 0 = about to trip over himself.</summary>
    public float Balance01 { get; private set; } = 1f;

    /// <summary>How much of the current trouble came from accumulated disturbance (debugging/overlay).</summary>
    public float Disturbance { get; private set; }

    private Animator animator;
    private BoxerPunchController controller;
    private Controller locomotion;
    private Transform hips, chest, leftFoot, rightFoot;
    private readonly bool[] planted = { true, true };
    private readonly float[] plantedSmooth = { 1f, 1f };
    private float raw = 1f, smoothed = 1f, smoothVelocity;
    private float scale = 1f;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<BoxerPunchController>();
        locomotion = GetComponent<Controller>();
        if (animator == null || !animator.isHuman) { enabled = false; return; }
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        scale = Mathf.Max(0.01f, transform.lossyScale.y);
        if (hips == null || leftFoot == null || rightFoot == null) enabled = false;
    }

    /// <summary>Something rocked the body: a received hit, an extreme throw, a stumble. 0..1-ish per event.</summary>
    public void AddDisturbance(float amount)
    {
        Disturbance = Mathf.Clamp01(Disturbance + Mathf.Max(0f, amount));
    }

    /// <summary>A punch was thrown: it costs a little balance — spam drains the legs before any cooldown could.</summary>
    public void NotifyPunch(float cost) => AddDisturbance(cost);

    /// <summary>
    /// Direction-aware punch cost: a punch thrown ALONG the line of the feet is braced by the stance; one thrown
    /// square ACROSS it fights the base and costs nearly double — the brief's "punch direction" balance input.
    /// </summary>
    public void NotifyPunch(float cost, Vector3 worldDirection)
    {
        if (leftFoot != null && rightFoot != null && worldDirection.sqrMagnitude > 0.01f)
        {
            Vector3 axis = Vector3.ProjectOnPlane(rightFoot.position - leftFoot.position, Vector3.up);
            Vector3 dir = Vector3.ProjectOnPlane(worldDirection, Vector3.up);
            if (axis.sqrMagnitude > 0.001f && dir.sqrMagnitude > 0.001f)
            {
                float along = Mathf.Abs(Vector3.Dot(dir.normalized, axis.normalized));
                cost *= Mathf.Lerp(1.5f, 0.8f, along);
            }
        }
        AddDisturbance(cost);
    }

    /// <summary>How planted the foot on this side is (0 left, 1 right), smoothed — the drive leg's root.</summary>
    public float Plantedness(int side) => plantedSmooth[Mathf.Clamp(side, 0, 1)];

    /// <summary>Is this side's foot on the ground right now?</summary>
    public bool FootPlanted(int side) => planted[Mathf.Clamp(side, 0, 1)];

    private void Update()
    {
        if (hips == null) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // 1. Foot contact — a short ray under each sole. If BOTH rays miss but the ground is clearly under the
        // hips, the rays (not the boxer) are wrong — never punish balance for probe geometry.
        planted[0] = ProbeGround(leftFoot);
        planted[1] = ProbeGround(rightFoot);
        if (!planted[0] && !planted[1]
            && Physics.Raycast(hips.position, Vector3.down, 1.4f * scale, ~0, QueryTriggerInteraction.Ignore))
        {
            planted[0] = planted[1] = true;
        }
        plantedSmooth[0] = Mathf.MoveTowards(plantedSmooth[0], planted[0] ? 1f : 0f, 6f * dt);
        plantedSmooth[1] = Mathf.MoveTowards(plantedSmooth[1], planted[1] ? 1f : 0f, 6f * dt);

        // 2. Centre of mass vs the support area: the segment between the planted feet, padded by footRadius.
        Vector3 com = chest != null ? Vector3.Lerp(hips.position, chest.position, 0.35f) : hips.position;
        float centring;
        if (planted[0] && planted[1]) centring = Centring(com, leftFoot.position, rightFoot.position);
        else if (planted[0]) centring = Centring(com, leftFoot.position, leftFoot.position);
        else if (planted[1]) centring = Centring(com, rightFoot.position, rightFoot.position);
        else centring = 0.25f;   // airborne: never zero (a hop is not a knockdown), never good

        // 3. Movement.
        float speed = locomotion != null ? locomotion.CurrentSpeed : 0f;
        float moving = 1f - speedCost * Mathf.Clamp01((speed / scale - steadySpeed) / (steadySpeed * 1.5f));

        // 4. Torso lean (the controller's slip/stagger lean, normalised).
        float lean = controller != null ? Mathf.Clamp01(controller.LeanMagnitude01) : 0f;
        float leaning = 1f - leanCost * lean;

        raw = Mathf.Clamp01(centring * moving * leaning) - Disturbance;

        // Disturbance drains slower the worse things already are: a rocked boxer stays rocked for a beat.
        Disturbance = Mathf.MoveTowards(Disturbance, 0f, recoverPerSecond * Mathf.Lerp(0.45f, 1f, smoothed) * dt);

        smoothed = Mathf.SmoothDamp(smoothed, Mathf.Clamp01(raw), ref smoothVelocity, smoothTime);
        Balance01 = Mathf.Clamp01(smoothed);

        // Diagnosability: standing still with nothing wrong yet reading off-balance = a setup problem, and it
        // silently scatters every punch. Say so once instead of letting aim feel broken.
        if (Balance01 < 0.5f && speed < 0.3f && Disturbance < 0.1f)
        {
            lowSince += dt;
            if (lowSince > 3f && !warnedLow)
            {
                warnedLow = true;
                Debug.LogWarning($"BoxerBalance: balance reads {Balance01:0.00} while standing still (feet planted: " +
                                 $"L {planted[0]} R {planted[1]}, centring low?). Check the floor has a collider under the " +
                                 "feet — a failing ground probe must not scatter punches.", this);
            }
        }
        else lowSince = 0f;
    }

    private float lowSince;
    private bool warnedLow;

    /// <summary>1 when the COM sits over the padded segment between the feet, falling to 0 one footRadius outside it.</summary>
    private float Centring(Vector3 com, Vector3 a, Vector3 b)
    {
        Vector3 flatCom = new Vector3(com.x, 0f, com.z);
        Vector3 fa = new Vector3(a.x, 0f, a.z);
        Vector3 fb = new Vector3(b.x, 0f, b.z);
        Vector3 ab = fb - fa;
        float t = ab.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector3.Dot(flatCom - fa, ab) / ab.sqrMagnitude) : 0f;
        float distance = Vector3.Distance(flatCom, fa + ab * t);
        float pad = footRadius * scale;
        // Fully stable while inside the pad, gone one pad further out.
        return 1f - Mathf.Clamp01((distance - pad) / pad);
    }

    private bool ProbeGround(Transform foot)
    {
        if (foot == null) return true;
        // The foot BONE sits at the ankle, well above the sole — probe generously from above it.
        Vector3 origin = foot.position + Vector3.up * (0.12f * scale);
        return Physics.Raycast(origin, Vector3.down, (0.12f + plantHeight) * scale, ~0, QueryTriggerInteraction.Ignore)
            || Physics.SphereCast(origin, 0.05f * scale, Vector3.down, out _, (0.12f + plantHeight) * scale, ~0, QueryTriggerInteraction.Ignore);
    }

    // ---------------------------------------------------------------- Debug visualization

    private void OnDrawGizmos()
    {
        if (!debugDraw || !Application.isPlaying || hips == null) return;

        Vector3 com = chest != null ? Vector3.Lerp(hips.position, chest.position, 0.35f) : hips.position;
        Vector3 floor = leftFoot != null ? leftFoot.position : com - Vector3.up * scale;
        Vector3 flatCom = new Vector3(com.x, floor.y, com.z);

        // Feet: green ring = planted, red = airborne. The yellow line is the support segment.
        DrawFoot(leftFoot, planted[0]);
        DrawFoot(rightFoot, planted[1]);
        if (leftFoot != null && rightFoot != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(leftFoot.position, rightFoot.position);
        }

        // Centre of mass, coloured by the balance it produces, with its drop line onto the floor.
        Gizmos.color = Color.Lerp(Color.red, Color.green, Balance01);
        Gizmos.DrawSphere(com, 0.045f * scale);
        Gizmos.DrawLine(com, flatCom);
        Gizmos.DrawWireSphere(flatCom, 0.03f * scale);
    }

    private void DrawFoot(Transform foot, bool isPlanted)
    {
        if (foot == null) return;
        Gizmos.color = isPlanted ? new Color(0.2f, 1f, 0.3f, 0.9f) : new Color(1f, 0.25f, 0.2f, 0.9f);
        Gizmos.DrawWireSphere(foot.position, footRadius * scale);
    }

    private void OnGUI()
    {
        if (!debugOverlay || hips == null) return;
        string impact = controller != null ? controller.LastImpactReport : "";
        string text = $"BALANCE {Balance01:0.00}   feet L{(planted[0] ? "●" : "○")} R{(planted[1] ? "●" : "○")}   " +
                      $"disturb {Disturbance:0.00}\n{impact}";
        GUI.Label(new Rect(Screen.width - 360f, 8f, 352f, 48f), text);
    }
}
