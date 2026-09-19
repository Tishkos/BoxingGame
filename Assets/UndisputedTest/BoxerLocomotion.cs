using Animancer;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Strafe-style boxing locomotion on a 2D Animancer blend space.
/// </summary>
/// <remarks>
/// The blend thresholds are not authored by hand - each is read from
/// <see cref="AnimationClip.averageSpeed"/>, which is Unity's own measurement of where the
/// clip's root actually travels. A clip placed in the wrong slot would therefore sit at the
/// wrong point in the blend space and be visibly wrong, rather than quietly wrong.
///
/// Root motion supplies translation only. The sliced strafes drift about 13 degrees of yaw
/// per loop, so letting the animation rotate the character would spiral it away over a few
/// seconds. Facing belongs to the controller here, which is also what a boxing game wants -
/// the guard stays on the opponent while the feet circle.
/// </remarks>
[RequireComponent(typeof(AnimancerComponent))]
public class BoxerLocomotion : MonoBehaviour
{
    [Header("Blend space clips")]
    public AnimationClip Idle;
    public AnimationClip Forward;
    public AnimationClip Back;
    public AnimationClip Left;
    public AnimationClip Right;

    [Header("Blend thresholds (body-relative m/s)")]
    [Tooltip("Set by Tools > Undisputed > Build Locomotion Rig from thresholds.json. " +
             "Leave at zero to fall back to AnimationClip.averageSpeed.")]
    public Vector2 ForwardVelocity;
    public Vector2 BackVelocity;
    public Vector2 LeftVelocity;
    public Vector2 RightVelocity;

    [Header("Feel")]
    [Tooltip("How fast the blend parameter chases the stick, in units of clip speed per second.")]
    public float Acceleration = 6f;

    [Tooltip("Degrees per second the body turns, when turning is enabled at all.")]
    public float TurnSpeed = 360f;

    [Tooltip("Scales every clip's contribution. 1 = exactly as the mocap moved.")]
    [Range(0.1f, 2f)] public float SpeedScale = 1f;

    [Header("Facing")]
    [Tooltip("If set, the boxer always faces this. Leave empty to keep the starting facing.")]
    public Transform FaceTarget;

    [Tooltip("Right stick / Q+E turns the body.")]
    public bool AllowManualTurn = true;

    [Header("Root motion")]
    [Tooltip("Off pins the boxer in place, which makes it easier to check the footwork.")]
    public bool ApplyRootMotion = true;

    public bool ShowOverlay = true;

    private AnimancerComponent _Animancer;
    private Animator _Animator;
    private CartesianMixerState _Mixer;
    private AnimancerState[] _States;

    private Vector2 _Input;
    private Vector2 _Parameter;
    private Vector2 _MaxForward, _MaxBack, _MaxLeft, _MaxRight;
    private Vector3 _LastDelta;
    private float _WorldSpeed;
    private string _Error;

    /// <summary>
    /// Velocity the clip's root actually has, in the character's own frame.
    /// </summary>
    /// <remarks>
    /// Prefers the baked value. Each source take was captured facing an arbitrary direction
    /// (start yaws of -171, +118, -142 degrees here), so clip-space root displacement says
    /// nothing about which way the boxer walks - only the per-frame delta rotated by the
    /// inverse of RootQ does, and that is what the baked numbers hold.
    /// <see cref="AnimationClip.averageSpeed"/> is the fallback, not the source of truth.
    /// </remarks>
    private static Vector2 MeasuredVelocity(AnimationClip clip, Vector2 baked, string slot)
    {
        if (baked.sqrMagnitude > 0.0001f)
            return baked;

        if (clip == null)
            return Vector2.zero;

        Vector3 v = clip.averageSpeed;
        Debug.LogWarning($"BoxerLocomotion: no baked threshold for {slot}, falling back to " +
            $"averageSpeed ({v.x:0.00}, {v.z:0.00}). Re-run Build Locomotion Rig to bake it.");
        return new Vector2(v.x, v.z);
    }

    private void Awake()
    {
        _Animancer = GetComponent<AnimancerComponent>();
        _Animator = _Animancer.Animator;

        if (_Animator != null)
        {
            _Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // Unity only computes Animator.deltaPosition when root motion is enabled, and
            // OnAnimatorMove below reads exactly that value. Leaving this false makes the
            // boxer animate on the spot with no explanation, so force it on here and let
            // the ApplyRootMotion field decide whether the delta is actually used.
            _Animator.applyRootMotion = true;
        }
    }

    private void Start()
    {
        if (Idle == null || Forward == null || Back == null || Left == null || Right == null)
        {
            _Error = "One or more blend space clips are unassigned.";
            Debug.LogError("BoxerLocomotion: " + _Error, this);
            return;
        }

        _MaxForward = MeasuredVelocity(Forward, ForwardVelocity, "Forward");
        _MaxBack = MeasuredVelocity(Back, BackVelocity, "Back");
        _MaxLeft = MeasuredVelocity(Left, LeftVelocity, "Left");
        _MaxRight = MeasuredVelocity(Right, RightVelocity, "Right");

        _Mixer = new CartesianMixerState();
        _Mixer.ChildCapacity = 5;

        _States = new AnimancerState[5];
        _States[0] = _Mixer.Add(Idle);
        _States[1] = _Mixer.Add(Forward);
        _States[2] = _Mixer.Add(Back);
        _States[3] = _Mixer.Add(Left);
        _States[4] = _Mixer.Add(Right);

        // Assigned as one array so the thresholds can never desync from the child order.
        _Mixer.SetThresholds(
            Vector2.zero,
            _MaxForward,
            _MaxBack,
            _MaxLeft,
            _MaxRight);

        // The idle is a different length and should not be time-warped to match a strafe.
        _Mixer.DontSynchronize(_States[0]);

        _Animancer.Play(_Mixer);
        WarnIfMisplaced();
    }

    /// <summary>Shout if a clip does not travel the way its slot claims.</summary>
    private void WarnIfMisplaced()
    {
        Check(Forward, _MaxForward, new Vector2(0, 1), "Forward");
        Check(Back, _MaxBack, new Vector2(0, -1), "Back");
        Check(Left, _MaxLeft, new Vector2(-1, 0), "Left");
        Check(Right, _MaxRight, new Vector2(1, 0), "Right");

        void Check(AnimationClip clip, Vector2 measured, Vector2 wanted, string slot)
        {
            if (measured.sqrMagnitude < 0.01f)
            {
                Debug.LogWarning($"BoxerLocomotion: '{clip.name}' in the {slot} slot barely " +
                    $"moves ({measured.magnitude:0.00} m/s). The blend space will have a hole there.", this);
                return;
            }

            float angle = Vector2.Angle(measured.normalized, wanted);
            if (angle > 45f)
            {
                Debug.LogError($"BoxerLocomotion: '{clip.name}' is in the {slot} slot but travels " +
                    $"{angle:0}d away from {slot} (measured {measured.x:0.00}, {measured.y:0.00} m/s). " +
                    $"Movement will go the wrong way.", this);
            }
        }
    }

    private void Update()
    {
        if (_Mixer == null)
            return;

        _Input = ReadMove();

        // Map the stick onto the velocity the clips can actually produce, per axis, so a
        // full push forward asks for exactly the forward clip's speed and no more.
        Vector2 desired;
        desired.x = _Input.x >= 0
            ? _Input.x * Mathf.Abs(_MaxRight.x)
            : _Input.x * Mathf.Abs(_MaxLeft.x);
        desired.y = _Input.y >= 0
            ? _Input.y * Mathf.Abs(_MaxForward.y)
            : _Input.y * Mathf.Abs(_MaxBack.y);
        desired *= SpeedScale;

        _Parameter = Vector2.MoveTowards(_Parameter, desired, Acceleration * Time.deltaTime);
        _Mixer.Parameter = _Parameter;

        UpdateFacing();
    }

    private void UpdateFacing()
    {
        if (FaceTarget != null)
        {
            Vector3 to = FaceTarget.position - transform.position;
            to.y = 0;
            if (to.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, Quaternion.LookRotation(to), TurnSpeed * Time.deltaTime);
            }
            return;
        }

        if (AllowManualTurn)
        {
            float turn = ReadTurn();
            if (turn != 0)
                transform.Rotate(0, turn * TurnSpeed * Time.deltaTime, 0);
        }
    }

    /// <summary>Translation from the animation, rotation from the controller.</summary>
    private void OnAnimatorMove()
    {
        if (_Animator == null)
            return;

        _LastDelta = _Animator.deltaPosition;

        if (ApplyRootMotion)
            transform.position += _LastDelta;

        _WorldSpeed = Time.deltaTime > 0 ? _LastDelta.magnitude / Time.deltaTime : 0f;
    }

    /************************************************************************************/
    // Input: gamepad first, then keyboard. Both go through the same vector.
    /************************************************************************************/

    private Vector2 ReadMove()
    {
#if ENABLE_INPUT_SYSTEM
        Vector2 move = Vector2.zero;

        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            move = pad.leftStick.ReadValue();
            if (move.sqrMagnitude < 0.02f)      // deadzone
                move = Vector2.zero;
        }

        if (move == Vector2.zero)
        {
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= 1;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += 1;
            }
        }

        return Vector2.ClampMagnitude(move, 1f);
#else
        Vector2 move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        return Vector2.ClampMagnitude(move, 1f);
#endif
    }

    private float ReadTurn()
    {
#if ENABLE_INPUT_SYSTEM
        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            float stick = pad.rightStick.ReadValue().x;
            if (Mathf.Abs(stick) > 0.15f)
                return stick;
        }

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.qKey.isPressed) return -1f;
            if (kb.eKey.isPressed) return 1f;
        }
        return 0f;
#else
        if (Input.GetKey(KeyCode.Q)) return -1f;
        if (Input.GetKey(KeyCode.E)) return 1f;
        return 0f;
#endif
    }

    /************************************************************************************/

    private void OnGUI()
    {
        if (!ShowOverlay)
            return;

        GUIStyle style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
        GUILayout.BeginArea(new Rect(10, 10, 480, 470), GUI.skin.box);

        GUILayout.Label("<b>Boxer locomotion</b>  -  Cartesian blend space", style);

        if (_Error != null)
        {
            GUILayout.Label("<color=red>" + _Error + "</color>", style);
            GUILayout.EndArea();
            return;
        }

#if ENABLE_INPUT_SYSTEM
        string pad = Gamepad.current != null
            ? "<color=lime>" + Gamepad.current.displayName + "</color>"
            : "none";
        GUILayout.Label("gamepad : " + pad + "   keyboard : " +
            (Keyboard.current != null ? "<color=lime>ok</color>" : "<color=red>none</color>"), style);
#else
        GUILayout.Label("input: legacy Input Manager", style);
#endif
        GUILayout.Label("left stick / WASD to move" + (AllowManualTurn ? ", right stick / Q,E to turn" : ""), style);
        GUILayout.Space(6);

        GUILayout.Label($"input     : ({_Input.x,6:0.00}, {_Input.y,6:0.00})", style);
        GUILayout.Label($"parameter : ({_Parameter.x,6:0.00}, {_Parameter.y,6:0.00})  " +
            $"|v| {_Parameter.magnitude:0.00}", style);
        GUILayout.Label($"world     : {_WorldSpeed,5:0.00} m/s   rootMotion " +
            (ApplyRootMotion ? "<color=lime>on</color>" : "off"), style);
        GUILayout.Space(8);

        GUILayout.Label("<b>blend weights</b>  (threshold = clip's measured velocity)", style);

        if (_Mixer != null && _States != null)
        {
            string[] names = { "Idle", "Forward", "Back", "Left", "Right" };
            for (int i = 0; i < _States.Length; i++)
            {
                AnimancerState s = _States[i];
                if (s == null)
                    continue;

                Vector2 th = _Mixer.GetThreshold(i);
                int bars = Mathf.RoundToInt(s.Weight * 20f);
                string bar = new string('#', bars).PadRight(20, '.');
                GUILayout.Label($"{names[i],-8} <color=#7fd>{bar}</color> {s.Weight:0.00}   " +
                    $"thr ({th.x,5:0.00},{th.y,5:0.00})", style);
            }
        }

        GUILayout.Space(8);
        GUILayout.Label($"speed scale  {SpeedScale:0.00}", style);
        SpeedScale = GUILayout.HorizontalSlider(SpeedScale, 0.1f, 2f);
        if (GUILayout.Button(ApplyRootMotion ? "root motion: ON (click to pin in place)"
                                             : "root motion: OFF (click to move)"))
            ApplyRootMotion = !ApplyRootMotion;

        GUILayout.EndArea();
    }
}
