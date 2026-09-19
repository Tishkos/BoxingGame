using UnityEngine;

/// <summary>
/// Smooth fight camera. Keeps the framing you set up in the scene (distance, height, shoulder offset, tilt),
/// but orients it along the "fight axis" (boxer -> opponent) so the opponent stays in the same place on screen.
///
/// The camera's yaw is driven straight from that axis rather than from the boxer's own rotation, which is
/// deliberately snappy for footwork and may also be moved by animation root motion. The yaw is then damped with
/// a time constant AND a hard degrees-per-second cap, so circling the opponent produces a slow cinematic orbit
/// and stepping past them never whips the view around.
///
/// With no opponent (no BoxingTarget in the scene), the boxer steers relative to the camera. In that mode the
/// camera holds its yaw and only follows position: mirroring the boxer's facing there would create a feedback
/// loop (turn -> camera swings behind -> "left" changes -> turn again) that spins the view endlessly.
///
/// <see cref="Shake"/> and <see cref="Kick"/> add impact feedback on top of the smoothed motion.
/// Attach to the Main Camera, position it how you like relative to the boxer, and assign the boxer as Target.
/// </summary>
public class CameraFollow : MonoBehaviour
{
    [Header("Targets")]
    [Tooltip("The boxer to follow. If empty, the first Controller in the scene is used.")]
    [SerializeField] private Transform target;

    [Tooltip("Who the boxer is fighting. If empty, it is read from the boxer's Controller each frame.")]
    [SerializeField] private Transform opponent;

    [Header("Orientation")]
    [Tooltip("Swing around behind the boxer as the fight axis (boxer -> opponent) turns. Untick for a fixed-angle follow.")]
    [SerializeField] private bool rotateWithTarget = true;

    [Tooltip("With no opponent the boxer steers relative to the camera, so following the boxer's own facing spins the " +
             "view in a feedback loop. Leave off to hold the camera yaw in that case; tick only for a fixed-facing character.")]
    [SerializeField] private bool followFacingWithoutOpponent = false;

    [Tooltip("Follow the boxer's height too. Untick to keep the camera at a fixed height (e.g. ignore jumps).")]
    [SerializeField] private bool followVertical = true;

    [Header("Smoothing")]
    [Tooltip("Seconds for the camera to settle after the fight axis turns. ~0.4-0.8 feels cinematic, 0.1 feels arcade.")]
    [Range(0f, 2f)]
    [SerializeField] private float rotationDamping = 0.5f;

    [Tooltip("Hard cap on how fast the camera may swing, in degrees per second. " +
             "Keeps a 180-degree flip (stepping past the opponent) from whipping the view around. 0 = no cap.")]
    [Min(0f)]
    [SerializeField] private float maxTurnSpeed = 100f;

    [Tooltip("Ignore fight-axis changes smaller than this many degrees. Removes micro-jitter while both boxers shuffle.")]
    [Range(0f, 10f)]
    [SerializeField] private float turnDeadZone = 1.5f;

    [Tooltip("Seconds for the camera position to catch up with the boxer.")]
    [Range(0f, 2f)]
    [SerializeField] private float positionDamping = 0.2f;

    [Header("Impact feedback")]
    [Tooltip("Largest positional shake at full strength (metres).")]
    [Min(0f)]
    [SerializeField] private float shakeAmplitude = 0.06f;

    [Tooltip("Largest roll at full strength (degrees).")]
    [Min(0f)]
    [SerializeField] private float shakeRoll = 1.2f;

    [Tooltip("How fast shakes die out (per second).")]
    [Min(0.1f)]
    [SerializeField] private float shakeDecay = 7f;

    [Tooltip("Seconds for a kick (camera pushed along the punch) to spring back.")]
    [Range(0.02f, 0.5f)]
    [SerializeField] private float kickReturnTime = 0.12f;

    [Tooltip("Seconds for an FOV punch-in to recover.")]
    [Range(0.05f, 0.5f)]
    [SerializeField] private float fovReturnTime = 0.18f;

    // The scene-view framing, stored in a yaw-local frame so it can be rebuilt as the yaw turns.
    private Vector3 localOffset;
    private Quaternion localRotation;

    private Controller targetController;

    private float currentYaw;      // yaw the camera is actually at
    private float desiredYaw;      // yaw the camera is heading towards (dead-zone filtered)
    private float yawVelocity;
    private Vector3 smoothedPosition;
    private Vector3 positionVelocity;

    private float shake;
    private float shakeSeed;
    private Vector3 kick;
    private Vector3 kickVelocity;
    private Camera cam;
    private float baseFov = -1f;
    private float fovKick;
    private float fovVelocity;

    /// <summary>The transform the camera is currently orienting towards (may be null).</summary>
    public Transform Opponent => ResolveOpponent();

    private void Start()
    {
        if (target == null)
        {
            Controller found = FindAnyObjectByType<Controller>();
            if (found != null) target = found.transform;
        }

        if (target == null)
        {
            Debug.LogWarning("CameraFollow: no target assigned and no Controller found in the scene.", this);
            enabled = false;
            return;
        }

        CacheController();
        cam = GetComponent<Camera>();
        shakeSeed = Random.value * 100f;
        smoothedPosition = transform.position;

        // Capture the current view relative to the fight axis if there is one, otherwise the boxer's facing
        // (yaw only, so tilt/roll of the boxer never leaks in).
        if (!TryResolveRawYaw(out currentYaw)) currentYaw = target.eulerAngles.y;
        desiredYaw = currentYaw;

        Quaternion inverseYaw = Quaternion.Inverse(Quaternion.Euler(0f, currentYaw, 0f));
        localOffset = inverseYaw * (transform.position - target.position);
        localRotation = inverseYaw * transform.rotation;
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // --- Yaw: dead zone, then damped with a speed cap ---
        if (rotateWithTarget && TryResolveRawYaw(out float rawYaw))
        {
            if (Mathf.Abs(Mathf.DeltaAngle(desiredYaw, rawYaw)) > turnDeadZone)
                desiredYaw = rawYaw;
        }

        float maxSpeed = maxTurnSpeed > 0f ? maxTurnSpeed : Mathf.Infinity;
        currentYaw = rotationDamping > 0f
            ? Mathf.SmoothDampAngle(currentYaw, desiredYaw, ref yawVelocity, rotationDamping, maxSpeed, dt)
            : Mathf.MoveTowardsAngle(currentYaw, desiredYaw, maxSpeed * dt);

        Quaternion yawRotation = Quaternion.Euler(0f, currentYaw, 0f);

        // --- Position: rebuilt from the damped yaw so the orbit stays consistent, then damped itself ---
        Vector3 desiredPosition = target.position + yawRotation * localOffset;
        if (!followVertical) desiredPosition.y = smoothedPosition.y;

        smoothedPosition = positionDamping > 0f
            ? Vector3.SmoothDamp(smoothedPosition, desiredPosition, ref positionVelocity, positionDamping, Mathf.Infinity, dt)
            : desiredPosition;

        Quaternion rotation = yawRotation * localRotation;

        // --- Impact feedback layered on top (never fed back into the smoothing) ---
        kick = Vector3.SmoothDamp(kick, Vector3.zero, ref kickVelocity, kickReturnTime, Mathf.Infinity, dt);
        shake *= Mathf.Exp(-shakeDecay * dt);
        if (shake < 0.005f) shake = 0f;

        Vector3 offset = kick;
        float roll = 0f;
        if (shake > 0f)
        {
            float t = Time.unscaledTime * 22f;
            float nx = (Mathf.PerlinNoise(shakeSeed, t) - 0.5f) * 2f;
            float ny = (Mathf.PerlinNoise(shakeSeed + 7f, t) - 0.5f) * 2f;
            float nr = (Mathf.PerlinNoise(shakeSeed + 13f, t) - 0.5f) * 2f;
            offset += rotation * new Vector3(nx, ny, 0f) * (shakeAmplitude * shake);
            roll = nr * shakeRoll * shake;
        }

        transform.position = smoothedPosition + offset;
        transform.rotation = rotation * Quaternion.Euler(0f, 0f, roll);

        // FOV punch-in: impacts push the lens in for a beat, then it springs back.
        if (cam != null)
        {
            if (baseFov < 0f) baseFov = cam.fieldOfView;
            fovKick = Mathf.SmoothDamp(fovKick, 0f, ref fovVelocity, fovReturnTime, Mathf.Infinity, dt);
            cam.fieldOfView = baseFov - fovKick;
        }
    }

    /// <summary>Punch the FOV in by this many degrees; it springs back on its own.</summary>
    public void FovKick(float degrees)
    {
        if (cam == null) cam = GetComponent<Camera>();
        if (cam == null) return;
        if (baseFov < 0f) baseFov = cam.fieldOfView;
        fovKick = Mathf.Min(10f, fovKick + Mathf.Max(0f, degrees));
    }

    // ---------------------------------------------------------------- Impact feedback

    /// <summary>Add a shake (0..1 strength; stacks up to 1 and decays on its own).</summary>
    public void Shake(float strength)
    {
        shake = Mathf.Clamp01(shake + Mathf.Max(0f, strength));
    }

    /// <summary>Push the camera by a world-space offset that springs back (e.g. along a punch).</summary>
    public void Kick(Vector3 worldOffset)
    {
        kick += worldOffset;
    }

    // ---------------------------------------------------------------- Targets

    /// <summary>Change the followed boxer at runtime, keeping the same relative view.</summary>
    public void SetTarget(Transform newTarget, bool snap = false)
    {
        target = newTarget;
        CacheController();
        if (snap) SnapToTarget();
    }

    /// <summary>Override the opponent (pass null to fall back to the boxer's Controller target).</summary>
    public void SetOpponent(Transform newOpponent)
    {
        opponent = newOpponent;
    }

    /// <summary>
    /// Jump straight to the settled position and orientation, discarding all smoothing.
    /// Call after teleporting the boxer or at the start of a round so the camera does not swing in.
    /// </summary>
    public void SnapToTarget()
    {
        if (target == null) return;

        if (rotateWithTarget && TryResolveRawYaw(out float rawYaw)) currentYaw = rawYaw;
        desiredYaw = currentYaw;
        yawVelocity = 0f;
        positionVelocity = Vector3.zero;
        kick = Vector3.zero;
        kickVelocity = Vector3.zero;
        shake = 0f;

        Quaternion yawRotation = Quaternion.Euler(0f, currentYaw, 0f);
        Vector3 position = target.position + yawRotation * localOffset;
        if (!followVertical) position.y = smoothedPosition.y;

        smoothedPosition = position;
        transform.position = position;
        transform.rotation = yawRotation * localRotation;
    }

    // ---------------------------------------------------------------- Helpers

    private void CacheController()
    {
        targetController = target != null ? target.GetComponent<Controller>() : null;
    }

    private Transform ResolveOpponent()
    {
        if (opponent != null) return opponent;
        return targetController != null ? targetController.Target : null;
    }

    /// <summary>
    /// Yaw the camera should orient to: the fight axis (boxer -> opponent) when there is an opponent, otherwise the
    /// boxer's own facing if <see cref="followFacingWithoutOpponent"/> is set. Returns false when the yaw should be held.
    /// </summary>
    private bool TryResolveRawYaw(out float yaw)
    {
        Transform opp = ResolveOpponent();
        if (opp != null)
        {
            Vector3 axis = Vector3.ProjectOnPlane(opp.position - target.position, Vector3.up);
            if (axis.sqrMagnitude > 0.0001f)
            {
                yaw = Mathf.Atan2(axis.x, axis.z) * Mathf.Rad2Deg;
                return true;
            }
        }

        if (followFacingWithoutOpponent)
        {
            yaw = target.eulerAngles.y;
            return true;
        }

        yaw = 0f;
        return false;
    }

    private void OnDrawGizmosSelected()
    {
        if (target == null) return;
        Transform opp = Application.isPlaying ? ResolveOpponent() : opponent;
        if (opp == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(target.position + Vector3.up, opp.position + Vector3.up);
    }
}
