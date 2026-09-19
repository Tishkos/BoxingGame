using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Boxing-style character controller. Input comes from BoxerInput (keyboard/mouse, gamepad, or Rewired).
/// The character always squares up to a target (opponent): W/S step in and out,
/// A/D circle around it, Left Shift to move faster, Space to jump (off by default).
/// If no target is set, movement falls back to camera-relative and the character turns to face its movement.
/// Requires a CharacterController component on the same GameObject.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class Controller : MonoBehaviour
{
    [Header("Target (opponent)")]
    [Tooltip("What the boxer faces and circles around. If empty, the first BoxingTarget in the scene is used.")]
    [SerializeField] private Transform target;
    [Tooltip("The boxer cannot step closer to the target's SURFACE than this (metres). ~0.4 lets hooks and uppercuts land; straights need ~0.55.")]
    [SerializeField] private float closestRange = 0.4f;
    [Tooltip("Optional: the boxer cannot back away further than this. 0 = no limit.")]
    [SerializeField] private float maxDistanceToTarget = 0f;

    [Header("References")]
    [Tooltip("Used for camera-relative movement when there is no target. Defaults to Camera.main.")]
    [SerializeField] private Transform cameraTransform;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 3f;
    [SerializeField] private float sprintSpeed = 5f;
    [Tooltip("How quickly the boxer reaches its target speed (higher = snappier footwork).")]
    [SerializeField] private float acceleration = 14f;
    [Tooltip("How quickly the boxer turns to face the target (higher = snappier).")]
    [SerializeField] private float turnSpeed = 14f;
    [Tooltip("Extra yaw (degrees) added to the facing so the animated guard stance squares up to the target. " +
             "Tune live in Play mode if the punches land slightly left or right of the bag.")]
    [Range(-90f, 90f)]
    [SerializeField] private float stanceYawOffset = 0f;

    [Header("Jump & Gravity")]
    [SerializeField] private bool allowJump = false;
    [SerializeField] private float jumpHeight = 0.8f;
    [SerializeField] private float gravity = -20f;
    [Tooltip("Small downward force applied while grounded so isGrounded stays stable on slopes.")]
    [SerializeField] private float groundedStickForce = -2f;

    [Header("Knockback")]
    [Tooltip("How quickly external pushes (e.g. the bag swinging into the boxer) fade, per second.")]
    [SerializeField] private float knockbackDamping = 6f;

    [Header("Dash step")]
    [Tooltip("Tapping sprint (L3 / Shift) fires an instant step impulse in the moved direction (m/s). Holding it still sprints.")]
    [SerializeField] private float dashImpulse = 1.6f;

    /// <summary>When the last dash-step fired — punches thrown just after it earn the step-in bonus.</summary>
    public float LastDashTime { get; private set; } = -10f;
    private bool sprintWasHeld;

    /// <summary>Current horizontal speed in units/second (useful for driving an Animator).</summary>
    public float CurrentSpeed { get; private set; }
    /// <summary>True while the CharacterController reports ground contact.</summary>
    public bool IsGrounded { get; private set; }
    /// <summary>Normalised world-space movement direction (zero when idle).</summary>
    public Vector3 MoveDirection { get; private set; }
    /// <summary>
    /// Movement relative to the boxer's facing: x = strafe (+right/-left), y = forward (+toward target/-away).
    /// Handy for a strafe blend tree in an Animator.
    /// </summary>
    public Vector2 LocalMove { get; private set; }
    /// <summary>The transform the boxer is currently facing (may be null).</summary>
    public Transform Target => target;

    /// <summary>Scales walk/sprint speed (e.g. 0.6 while blocking, 0.8 when gassed). Whoever sets it resets it.</summary>
    public float SpeedMultiplier { get; set; } = 1f;

    /// <summary>Push the boxer by a velocity change (m/s), e.g. when the bag swings into them. Fades out on its own.</summary>
    public void AddImpulse(Vector3 velocityChange)
    {
        externalVelocity += Vector3.ProjectOnPlane(velocityChange, Vector3.up);
    }

    /// <summary>This boxer's intent — the player's by default, an AI's when one takes over.</summary>
    private IBoxerInput input = PlayerBoxerInput.Instance;

    /// <summary>Hand the footwork to an AI (or back to the player).</summary>
    public void SetInput(IBoxerInput source) => input = source ?? PlayerBoxerInput.Instance;

    private CharacterController controller;
    private BoxingTarget targetInfo;
    private Vector3 horizontalVelocity;
    private Vector3 externalVelocity;
    private float verticalVelocity;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (target == null)
        {
            BoxingTarget found = FindAnyObjectByType<BoxingTarget>();
            if (found != null) target = found.transform;
        }
        targetInfo = target != null ? target.GetComponent<BoxingTarget>() : null;
    }

    private void Update()
    {
        input.Poll();
        Vector2 move = input.Move;
        bool sprint = input.Sprint;
        bool jumpPressed = input.JumpDown;

        IsGrounded = controller.isGrounded;

        // --- Reference frame: face the target if we have one, otherwise use the camera ---
        Vector3 forward, right;
        bool hasTarget = target != null;

        if (hasTarget)
        {
            forward = Vector3.ProjectOnPlane(target.position - transform.position, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f) forward = transform.forward; // standing on top of the target
            forward.Normalize();
            right = Vector3.Cross(Vector3.up, forward);
        }
        else if (cameraTransform != null)
        {
            forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
            right   = Vector3.ProjectOnPlane(cameraTransform.right,   Vector3.up).normalized;
        }
        else
        {
            forward = Vector3.forward;
            right   = Vector3.right;
        }

        // W/S = in/out, A/D = circle left/right around the target
        Vector3 desiredDirection = forward * move.y + right * move.x;
        if (desiredDirection.sqrMagnitude > 1f) desiredDirection.Normalize();

        // --- Keep boxing range: no stepping into the target, optionally no drifting too far away ---
        if (hasTarget)
        {
            float surface = targetInfo != null ? targetInfo.surfaceRadius : 0f;
            float dist = Vector3.ProjectOnPlane(target.position - transform.position, Vector3.up).magnitude - surface;
            float towardAmount = Vector3.Dot(desiredDirection, forward);

            if (towardAmount > 0f && dist <= closestRange)
                desiredDirection -= forward * towardAmount;            // cancel the inward component
            else if (maxDistanceToTarget > 0f && towardAmount < 0f && dist >= maxDistanceToTarget)
                desiredDirection -= forward * towardAmount;            // cancel the outward component
        }

        MoveDirection = desiredDirection;
        LocalMove = new Vector2(Vector3.Dot(desiredDirection, right), Vector3.Dot(desiredDirection, forward));

        // Dash-step: the tap edge of sprint is a footwork verb of its own.
        if (sprint && !sprintWasHeld && desiredDirection.sqrMagnitude > 0.04f)
        {
            AddImpulse(desiredDirection.normalized * dashImpulse);
            LastDashTime = Time.time;
        }
        sprintWasHeld = sprint;

        // --- Horizontal velocity with smooth acceleration ---
        float targetSpeed = (sprint ? sprintSpeed : walkSpeed) * Mathf.Max(0f, SpeedMultiplier);
        Vector3 targetVelocity = desiredDirection * targetSpeed;
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, targetVelocity, 1f - Mathf.Exp(-acceleration * Time.deltaTime));
        CurrentSpeed = horizontalVelocity.magnitude;

        // --- Vertical velocity: gravity + jump ---
        if (IsGrounded && verticalVelocity < 0f)
            verticalVelocity = groundedStickForce;

        if (allowJump && jumpPressed && IsGrounded)
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;

        // --- Move ---
        externalVelocity = Vector3.Lerp(externalVelocity, Vector3.zero, 1f - Mathf.Exp(-knockbackDamping * Time.deltaTime));
        Vector3 motion = horizontalVelocity + externalVelocity + Vector3.up * verticalVelocity;
        controller.Move(motion * Time.deltaTime);

        // --- Facing: always square up to the target; otherwise face the movement direction ---
        Vector3 faceDirection = hasTarget ? forward : desiredDirection;
        if (faceDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(faceDirection, Vector3.up) * Quaternion.Euler(0f, stanceYawOffset, 0f);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, 1f - Mathf.Exp(-turnSpeed * Time.deltaTime));
        }
    }

    /// <summary>Change the opponent at runtime (pass null to go back to free camera-relative movement).</summary>
    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
        targetInfo = target != null ? target.GetComponent<BoxingTarget>() : null;
    }

    // ---------------------------------------------------------------- Input

    private void OnDrawGizmosSelected()
    {
        if (target == null) return;
        Gizmos.color = Color.red;
        Gizmos.DrawLine(transform.position + Vector3.up, target.position + Vector3.up);
    }
}
