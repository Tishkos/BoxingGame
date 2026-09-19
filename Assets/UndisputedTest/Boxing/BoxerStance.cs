using RootMotion.FinalIK;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Boxing
{
    /// <summary>
    /// Moves the boxer and keeps him squared up on his opponent.
    /// </summary>
    /// <remarks>
    /// Deliberately animation-free: this only translates and rotates the transform, leaving
    /// the legs animation to whatever else is driving it. That is also why root motion stays
    /// off on this rig - two things moving the same transform fight each other.
    ///
    /// Movement is in the boxer's own frame, and because he always faces the target, W steps
    /// in on the opponent, S backs out, and A/D circle around him. That is how boxing
    /// footwork reads, and it means the legs animation never has to match a world direction.
    /// </remarks>
    public class BoxerStance : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The opponent. The boxer squares up to this and punches aim at it.")]
        public Transform Target;

        [Header("Movement")]
        [Tooltip("Metres per second at full stick.")]
        public float MoveSpeed = 1.8f;

        [Tooltip("Higher reaches full speed sooner. Low values feel heavy.")]
        public float Acceleration = 10f;

        [Tooltip("Backing away is slower than stepping in, as it is in real footwork.")]
        [Range(0.3f, 1f)] public float BackwardScale = 0.75f;

        [Header("Facing")]
        public bool FaceTarget = true;

        [Tooltip("Degrees per second the body turns onto the target.")]
        public float TurnSpeed = 540f;

        [Tooltip("Keeps this distance from the target instead of walking through him.")]
        public float MinDistance = 0.75f;

        [Header("Look At")]
        public LookAtIK LookAt;

        [Range(0f, 1f)] public float LookWeight = 1f;
        [Range(0f, 1f)] public float LookBodyWeight = 0.35f;
        [Range(0f, 1f)] public float LookHeadWeight = 0.8f;

        [Tooltip("Aim this far above the target's pivot, i.e. at the head rather than the feet.")]
        public float LookHeightOffset = 1.45f;

        [Tooltip("When the target has a collider, aim this far down from its top instead of " +
                 "guessing a height. A hung bag's pivot sits at its mount, so an offset from " +
                 "the pivot would aim at the ceiling.")]
        [Range(0f, 1f)] public float AimDownFromTop = 0.3f;

        [Header("Debug")]
        public bool ShowOverlay = true;

        private Vector3 _Velocity;
        private Vector2 _Input;
        private Transform _LookPoint;
        private Collider _TargetCollider;

        /************************************************************************************/

        private void Awake()
        {
            if (LookAt == null)
                LookAt = GetComponentInChildren<LookAtIK>();

            // LookAtIK wants a Transform, and aiming at the opponent's pivot would stare at
            // his feet, so track a point held above him instead.
            var point = new GameObject("LookPoint");
            point.transform.SetParent(transform.parent, false);
            _LookPoint = point.transform;
        }

        private void OnDestroy()
        {
            if (_LookPoint != null)
                Destroy(_LookPoint.gameObject);
        }

        /************************************************************************************/

        private void Update()
        {
            _Input = ReadMove();

            Move();
            Turn();
            UpdateLookAt();
        }

        private void Move()
        {
            Vector2 input = _Input;

            // Retreating is slower than advancing.
            if (input.y < 0f)
                input.y *= BackwardScale;

            Vector3 wanted = (transform.right * input.x + transform.forward * input.y) * MoveSpeed;

            _Velocity = Vector3.MoveTowards(_Velocity, wanted, Acceleration * Time.deltaTime);

            Vector3 step = _Velocity * Time.deltaTime;

            // Do not walk through the opponent.
            if (Target != null && MinDistance > 0f)
            {
                Vector3 next = transform.position + step;
                Vector3 flat = next - Target.position;
                flat.y = 0f;

                if (flat.magnitude < MinDistance)
                {
                    Vector3 clamped = Target.position + flat.normalized * MinDistance;
                    clamped.y = transform.position.y;
                    step = clamped - transform.position;
                }
            }

            transform.position += step;
        }

        private void Turn()
        {
            if (!FaceTarget || Target == null)
                return;

            Vector3 to = Target.position - transform.position;
            to.y = 0f;

            if (to.sqrMagnitude < 0.0001f)
                return;

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                Quaternion.LookRotation(to),
                TurnSpeed * Time.deltaTime);
        }

        private void UpdateLookAt()
        {
            if (LookAt == null || LookAt.solver == null)
                return;

            if (Target == null)
            {
                LookAt.solver.SetLookAtWeight(0f);
                return;
            }

            _LookPoint.position = AimPoint();
            LookAt.solver.target = _LookPoint;

            LookAt.solver.SetLookAtWeight(
                LookWeight, LookBodyWeight, LookHeadWeight,
                eyesWeight: 0.5f, clampWeight: 0.5f);
        }

        /************************************************************************************/

        /// <summary>
        /// Where on the target to look and punch.
        /// </summary>
        /// <remarks>
        /// Measured from the target's collider when it has one, because a hung bag's pivot is
        /// at the mount near the ceiling - offsetting up from that would aim the boxer at the
        /// roof. Falls back to a fixed height above the pivot for a bare transform.
        /// </remarks>
        public Vector3 AimPoint()
        {
            if (Target == null)
                return transform.position + transform.forward;

            if (_TargetCollider == null)
                _TargetCollider = Target.GetComponentInChildren<Collider>();

            if (_TargetCollider != null)
            {
                Bounds b = _TargetCollider.bounds;
                return new Vector3(b.center.x,
                                   b.max.y - b.size.y * AimDownFromTop,
                                   b.center.z);
            }

            return Target.position + Vector3.up * LookHeightOffset;
        }

        private static Vector2 ReadMove()
        {
#if ENABLE_INPUT_SYSTEM
            Vector2 move = Vector2.zero;

            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                move = pad.leftStick.ReadValue();
                if (move.sqrMagnitude < 0.02f)
                    move = Vector2.zero;
            }

            if (move == Vector2.zero)
            {
                Keyboard kb = Keyboard.current;
                if (kb != null)
                {
                    if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1f;
                    if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1f;
                    if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= 1f;
                    if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += 1f;
                }
            }

            return Vector2.ClampMagnitude(move, 1f);
#else
            return Vector2.ClampMagnitude(
                new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f);
#endif
        }

        /************************************************************************************/

        private void OnGUI()
        {
            if (!ShowOverlay)
                return;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            GUILayout.BeginArea(new Rect(Screen.width - 330, 10, 320, 190), GUI.skin.box);

            GUILayout.Label("<b>Stance</b>   WASD / left stick to move", style);
            GUILayout.Label($"input   : ({_Input.x,5:0.00},{_Input.y,5:0.00})", style);
            GUILayout.Label($"speed   : {_Velocity.magnitude:0.00} m/s", style);
            GUILayout.Label($"target  : " + (Target != null
                ? $"<color=lime>{Target.name}</color>  {Vector3.Distance(transform.position, Target.position):0.00}m"
                : "<color=orange>none - not facing anything</color>"), style);
            GUILayout.Label($"lookAtIK: " + (LookAt != null
                ? "<color=lime>ok</color>" : "<color=orange>none</color>"), style);

            GUILayout.Space(4);
            GUILayout.Label($"move speed {MoveSpeed:0.00}", style);
            MoveSpeed = GUILayout.HorizontalSlider(MoveSpeed, 0.2f, 5f);
            FaceTarget = GUILayout.Toggle(FaceTarget, " face target");

            GUILayout.EndArea();
        }
    }
}
