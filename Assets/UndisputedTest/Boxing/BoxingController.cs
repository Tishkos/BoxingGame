using System.Collections.Generic;
using Animancer;
using RootMotion.FinalIK;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Boxing
{
    /// <summary>
    /// Mouse-gesture boxing. Left button throws with the left hand, right button with the
    /// right; a flick throws a jab, a drag picks the shot and aims it where you dragged.
    /// </summary>
    /// <remarks>
    /// The variety comes from stacking four independent sources rather than from having a
    /// lot of clips: which clip is chosen (52, minus a no-repeat history), the playback
    /// speed jitter, an additive upper-body layer re-rolled every punch, and a FinalIK hand
    /// offset that aims the glove at the actual drag point. The same jab therefore never
    /// plays back identically twice.
    ///
    /// Punches live on a masked layer so the lower body is left alone - whatever drives the
    /// legs keeps driving them while the upper body throws.
    /// </remarks>
    [RequireComponent(typeof(AnimancerComponent))]
    public class BoxingController : MonoBehaviour
    {
        [Header("Content")]
        public PunchLibrary Library;

        [Tooltip("Masks the punch layer to the upper body so the legs keep their own animation.")]
        public AvatarMask UpperBodyMask;

        [Header("Gesture")]
        [Tooltip("Drag distance in pixels that counts as a full-power commitment.")]
        public float FullDragPixels = 170f;

        [Tooltip("Below this many pixels the gesture is a flick, i.e. a fast jab.")]
        public float FlickPixels = 22f;

        [Header("Feel")]
        public float PunchFade = 0.12f;
        public float ReturnFade = 0.22f;

        [Tooltip("Random speed spread per punch. 0.1 means +/-10%.")]
        [Range(0f, 0.3f)] public float SpeedJitter = 0.1f;

        [Tooltip("Faster tiers play quicker. Tier 0 gets this multiplier, tier 3 gets 1.")]
        [Range(1f, 1.6f)] public float FastTierSpeed = 1.22f;

        [Tooltip("How many recent clips to avoid repeating.")]
        [Range(0, 12)] public int NoRepeatHistory = 6;

        [Header("Additive variation")]
        [Range(0f, 1f)] public float AdditiveMin = 0.12f;
        [Range(0f, 1f)] public float AdditiveMax = 0.4f;

        [Header("IK")]
        public FullBodyBipedIK IK;

        [Tooltip("How far the glove may be pulled toward the aim point, in metres.")]
        [Range(0f, 0.6f)] public float MaxReach = 0.22f;

        [Range(0f, 1f)] public float IKWeight = 0.8f;

        [Tooltip("Optional opponent. Punches aim here, offset by the drag.")]
        public Transform Target;

        [Tooltip("Supplies the aim point on the bag. A hung bag's pivot is at its mount, so " +
                 "aiming at Target.position alone would punch the ceiling.")]
        public BoxerStance Stance;

        [Header("Contact")]
        [Tooltip("The existing glove sensors. Armed only across the strike window, so guard " +
                 "and recovery motion never nudges the bag.")]
        public BoxerHitboxes Hitboxes;

        [Header("Impact")]
        [Tooltip("Fires once per punch at the strike frame. Arg0 = glove world position, " +
                 "Arg1 = power 0-1. Hook your VFX, audio and hit detection here.")]
        public UnityEngine.Events.UnityEvent<Vector3, float> OnImpact;

        [Header("Debug")]
        public bool ShowOverlay = true;

        private AnimancerComponent _Animancer;
        private AnimancerLayer _Base, _Punch, _Additive;

        private readonly List<PunchLibrary.Entry> _Recent = new List<PunchLibrary.Entry>();
        private PunchLibrary.Entry _Current;
        private AnimancerState _PunchState;
        private Hand _CurrentHand;

        private Vector2 _DragStart, _Drag;
        private Hand _DragHand;
        private bool _Dragging;

        private Vector3 _AimOffset;
        private float _IKRamp;
        private bool _ImpactFired;
        private bool _Armed;
        private string _LastGesture = "-";
        private int _Thrown;

        /************************************************************************************/

        private void Awake()
        {
            _Animancer = GetComponent<AnimancerComponent>();

            if (IK == null)
                IK = GetComponentInChildren<FullBodyBipedIK>();
        }

        private void Start()
        {
            if (Library == null)
            {
                Debug.LogError("BoxingController: no PunchLibrary assigned.", this);
                enabled = false;
                return;
            }

            Library.ClearCache();

            _Base = _Animancer.Layers[0];
            _Punch = _Animancer.Layers[1];
            _Additive = _Animancer.Layers[2];

            if (UpperBodyMask != null)
            {
                _Punch.Mask = UpperBodyMask;
                _Additive.Mask = UpperBodyMask;
            }

            // Additive means the layer contributes pose deltas relative to its own first
            // frame, so a slip clip adds lean on top of a punch instead of replacing it.
            _Additive.IsAdditive = true;
            _Additive.Weight = 0f;

            if (Library.Idle != null)
                _Base.Play(Library.Idle);

            _Punch.Weight = 0f;
        }

        /************************************************************************************/

        private void Update()
        {
            ReadGesture();

            // Let the punch layer fall away as the clip finishes so the idle returns.
            if (_PunchState != null && _PunchState.IsPlaying)
            {
                float t = _PunchState.NormalizedTime;
                if (t >= 1f)
                    EndPunch();
            }

            UpdateIKRamp();
        }

        private void ReadGesture()
        {
            bool leftDown = ButtonDown(0), rightDown = ButtonDown(1);
            bool leftUp = ButtonUp(0), rightUp = ButtonUp(1);

            if (!_Dragging && (leftDown || rightDown))
            {
                _Dragging = true;
                _DragHand = leftDown ? Hand.Left : Hand.Right;
                _DragStart = PointerPosition();
                _Drag = Vector2.zero;
                return;
            }

            if (!_Dragging)
                return;

            _Drag = PointerPosition() - _DragStart;

            bool release = _DragHand == Hand.Left ? leftUp : rightUp;
            if (release)
            {
                _Dragging = false;
                Throw(_DragHand, _Drag);
            }
        }

        /************************************************************************************/

        /// <summary>Turns a drag vector into a specific shot.</summary>
        /// <remarks>
        /// Horizontal travel means the punch comes round the side, so it is a hook. Upward
        /// travel means it comes from underneath, so it is an uppercut. A flick with no real
        /// travel is a jab. Dragging down aims at the body. Distance sets the power tier and,
        /// past the full-drag radius, commits to a lunge.
        /// </remarks>
        private void Throw(Hand hand, Vector2 drag)
        {
            float distance = drag.magnitude;
            float normalised = Mathf.Clamp01(distance / FullDragPixels);

            PunchType type;
            PunchTarget target;
            LungeType lunge = LungeType.None;

            if (distance < FlickPixels)
            {
                type = PunchType.Jab;
                target = PunchTarget.Head;
            }
            else
            {
                float ax = Mathf.Abs(drag.x), ay = Mathf.Abs(drag.y);

                if (ax > ay * 1.15f)
                    type = PunchType.Hook;
                else if (drag.y > 0f)
                    type = PunchType.Uppercut;
                else
                    type = PunchType.Jab;

                target = drag.y < -FlickPixels ? PunchTarget.Body : PunchTarget.Head;

                if (normalised > 0.8f)
                {
                    if (ax > ay)
                        lunge = drag.x > 0f ? LungeType.Right : LungeType.Left;
                    else
                        lunge = drag.y > 0f ? LungeType.In : LungeType.Out;
                }
            }

            int tier = distance < FlickPixels ? 0
                     : normalised < 0.45f ? 1
                     : normalised < 0.8f ? 2
                     : 3;

            PunchLibrary.Entry entry = Library.Best(hand, type, target, tier, lunge, _Recent);
            if (entry == null)
            {
                _LastGesture = $"{hand} {type} {target} t{tier} - no clip";
                return;
            }

            Remember(entry);
            PlayPunch(entry, drag, normalised);

            _LastGesture = $"{hand} {type} {target} t{tier}" +
                (lunge != LungeType.None ? " " + lunge : "") + $"  drag {distance:0}px";
            _Thrown++;
        }

        private void Remember(PunchLibrary.Entry entry)
        {
            _Recent.Add(entry);
            while (_Recent.Count > NoRepeatHistory)
                _Recent.RemoveAt(0);
        }

        private void PlayPunch(PunchLibrary.Entry entry, Vector2 drag, float normalised)
        {
            _Current = entry;
            _CurrentHand = entry.Hand;
            _ImpactFired = false;

            _Punch.Weight = 1f;
            _PunchState = _Punch.Play(entry.Clip, PunchFade, FadeMode.FromStart);

            // Faster on the light shots, plus a little spread so repeats do not line up.
            float tierSpeed = Mathf.Lerp(FastTierSpeed, 1f, entry.Tier / 3f);
            _PunchState.Speed = tierSpeed * (1f + Random.Range(-SpeedJitter, SpeedJitter));

            RollAdditive();

            // Aim: the drag direction becomes a world-space offset in the boxer's own frame,
            // so dragging right sends the glove right rather than right on screen.
            Vector2 aim = Vector2.ClampMagnitude(drag / FullDragPixels, 1f);
            _AimOffset = transform.right * (aim.x * MaxReach)
                       + transform.up * (aim.y * MaxReach)
                       + transform.forward * (normalised * MaxReach * 0.5f);
        }

        /// <summary>
        /// Picks a fresh additive clip and weight for every punch. This is the cheapest
        /// source of variety available: N variations times a continuous weight means the
        /// same base clip never reads the same way twice.
        /// </summary>
        private void RollAdditive()
        {
            if (Library.AdditiveVariations == null || Library.AdditiveVariations.Count == 0)
                return;

            AnimationClip clip = Library.AdditiveVariations[
                Random.Range(0, Library.AdditiveVariations.Count)];
            if (clip == null)
                return;

            AnimancerState state = _Additive.Play(clip, 0.15f, FadeMode.FromStart);
            state.Speed = Random.Range(0.75f, 1.35f);
            state.NormalizedTime = Random.value * 0.3f;

            _Additive.Weight = Random.Range(AdditiveMin, AdditiveMax);
        }

        private void EndPunch()
        {
            _PunchState = null;
            _Current = null;
            _Punch.StartFade(0f, ReturnFade);
            _Additive.StartFade(0f, ReturnFade);
        }

        /************************************************************************************/

        /// <summary>
        /// Ramps the hand effector in around the moment of impact and back out again, so the
        /// IK never fights the wind-up or the recovery - only the extension gets redirected.
        /// </summary>
        private void UpdateIKRamp()
        {
            float wanted = 0f;

            if (_PunchState != null && _Current != null && _PunchState.IsPlaying)
            {
                float t = Mathf.Clamp01(_PunchState.NormalizedTime);
                float strike = _Current.StrikeTime;

                // Triangular window centred on the strike.
                float width = 0.32f;
                float d = Mathf.Abs(t - strike);
                wanted = Mathf.Clamp01(1f - d / width);
                wanted *= wanted;

                if (!_ImpactFired && t >= strike)
                {
                    _ImpactFired = true;
                    Vector3 glove = GloveOf(_CurrentHand);
                    OnImpact?.Invoke(glove, Mathf.Clamp01((_Current.Tier + 1) / 4f));
                }

                // The sensors measure real glove speed, so they only need to be live while the
                // punch is actually extending. Armed all the time, guard motion would nudge
                // the bag; armed never, nothing lands at all.
                SetArmed(wanted > 0.05f);
            }

            else
            {
                SetArmed(false);
            }

            _IKRamp = Mathf.MoveTowards(_IKRamp, wanted, Time.deltaTime * 6f);
        }

        private void SetArmed(bool armed)
        {
            if (Hitboxes == null || armed == _Armed)
                return;

            _Armed = armed;
            if (armed)
                Hitboxes.PunchStart();
            else
                Hitboxes.PunchEnd();
        }

        /// <summary>World position of a glove, taken from the IK effector's bone.</summary>
        private Vector3 GloveOf(Hand hand)
        {
            if (IK != null && IK.solver != null)
            {
                IKEffector e = hand == Hand.Left
                    ? IK.solver.leftHandEffector
                    : IK.solver.rightHandEffector;
                if (e != null && e.bone != null)
                    return e.bone.position;
            }

            return transform.position + transform.forward * 0.4f + Vector3.up * 1.2f;
        }

        private void LateUpdate()
        {
            if (IK == null || IK.solver == null)
                return;

            IKEffector effector = _CurrentHand == Hand.Left
                ? IK.solver.leftHandEffector
                : IK.solver.rightHandEffector;

            if (_IKRamp <= 0.001f)
            {
                IK.solver.leftHandEffector.positionWeight = 0f;
                IK.solver.rightHandEffector.positionWeight = 0f;
                return;
            }

            Vector3 offset = _AimOffset;

            if (Target != null)
            {
                // Bias the glove toward where the boxer is actually looking. Stance measures
                // that from the bag's collider; Target.position on a hung bag is its mount.
                Vector3 aim = Stance != null ? Stance.AimPoint() : Target.position;
                Vector3 toTarget = aim - effector.bone.position;
                offset += Vector3.ClampMagnitude(toTarget, MaxReach) * 0.5f;
            }

            // positionOffset is additive on top of the animation and resets every frame,
            // which is what keeps the original punch arc intact.
            effector.positionOffset += offset * (_IKRamp * IKWeight);
        }

        /************************************************************************************/
        // Input
        /************************************************************************************/

        private static Vector2 PointerPosition()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }

        private static bool ButtonDown(int button)
        {
#if ENABLE_INPUT_SYSTEM
            Mouse m = Mouse.current;
            if (m == null)
                return false;
            return button == 0 ? m.leftButton.wasPressedThisFrame : m.rightButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(button);
#endif
        }

        private static bool ButtonUp(int button)
        {
#if ENABLE_INPUT_SYSTEM
            Mouse m = Mouse.current;
            if (m == null)
                return false;
            return button == 0 ? m.leftButton.wasReleasedThisFrame : m.rightButton.wasReleasedThisFrame;
#else
            return Input.GetMouseButtonUp(button);
#endif
        }

        /************************************************************************************/

        private void OnGUI()
        {
            if (!ShowOverlay)
                return;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            GUILayout.BeginArea(new Rect(10, 10, 430, 300), GUI.skin.box);

            GUILayout.Label("<b>Boxing</b>   LMB = left hand,  RMB = right hand", style);
            GUILayout.Label("flick = jab | drag sideways = hook | drag up = uppercut", style);
            GUILayout.Label("drag down = body | drag far = power + lunge", style);
            GUILayout.Space(6);

            GUILayout.Label($"last   : {_LastGesture}", style);
            GUILayout.Label($"clip   : {(_Current != null ? _Current.Clip.name : "-")}", style);
            GUILayout.Label($"library: {(Library != null ? Library.Entries.Count : 0)} punches, " +
                $"{(Library != null ? Library.AdditiveVariations.Count : 0)} additive", style);
            GUILayout.Label($"thrown : {_Thrown}   no-repeat window {_Recent.Count}", style);
            GUILayout.Space(6);

            GUILayout.Label($"punch layer  {(_Punch != null ? _Punch.Weight : 0):0.00}   " +
                $"additive {(_Additive != null ? _Additive.Weight : 0):0.00}   " +
                $"IK {_IKRamp:0.00}", style);
            GUILayout.Label($"IK component : " +
                (IK != null ? "<color=lime>ok</color>" : "<color=orange>none - no aiming</color>"), style);

            if (_Dragging)
                GUILayout.Label($"<color=yellow>drag {_Drag.magnitude:0}px  " +
                    $"({_Drag.x:0},{_Drag.y:0})</color>", style);

            GUILayout.EndArea();

            if (_Dragging)
            {
                // Simple aim readout at the pointer.
                Vector2 p = PointerPosition();
                var r = new Rect(p.x - 30, Screen.height - p.y - 12, 120, 22);
                GUI.Label(r, $"<color=yellow>{_Drag.magnitude:0}px</color>", style);
            }
        }
    }
}
