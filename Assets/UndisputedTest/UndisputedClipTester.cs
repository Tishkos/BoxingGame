using System.Collections.Generic;
using Animancer;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

/// <summary>
/// Minimal harness to confirm the ripped Undisputed muscle clips actually retarget
/// onto Bennett through Animancer. Number keys play a clip outright; WASD holds a
/// movement clip and falls back to the idle on release so fading is visible too.
/// </summary>
[RequireComponent(typeof(AnimancerComponent))]
public class UndisputedClipTester : MonoBehaviour
{
    [Tooltip("Played by number keys 1-9, in order. Index 0 is also the idle fallback for WASD.")]
    public List<AnimationClip> Clips = new List<AnimationClip>();

    [Header("Hold-to-play (leave null to disable a direction)")]
    public AnimationClip Forward;
    public AnimationClip Back;
    public AnimationClip Left;
    public AnimationClip Right;

    [Header("Tuning")]
    public float FadeDuration = 0.25f;
    public float Speed = 1f;

    [Tooltip("Root motion moves Bennett through the scene. Off keeps him on the spot, which is easier to watch.")]
    public bool ApplyRootMotion;

    private AnimancerComponent _Animancer;
    private AnimancerState _State;
    private string _Source = "-";
    private AnimationClip _Held;

    private void Awake()
    {
        _Animancer = GetComponent<AnimancerComponent>();

        if (_Animancer.Animator != null)
        {
            _Animancer.Animator.applyRootMotion = ApplyRootMotion;
            _Animancer.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
    }

    private void Start()
    {
        if (Clips.Count > 0 && Clips[0] != null)
            Play(Clips[0], "start");
    }

    private void Update()
    {
        if (_Animancer.Animator != null)
            _Animancer.Animator.applyRootMotion = ApplyRootMotion;

        for (int i = 0; i < Clips.Count && i < 9; i++)
        {
            if (WasPressedDigit(i + 1) && Clips[i] != null)
            {
                _Held = null;
                Play(Clips[i], "key " + (i + 1));
            }
        }

        // Hold a direction to play its clip, release to fall back to the idle.
        AnimationClip wanted = HeldDirection();
        if (wanted != _Held)
        {
            _Held = wanted;

            AnimationClip clip = wanted;
            if (clip == null && Clips.Count > 0)
                clip = Clips[0];

            if (clip != null)
                Play(clip, wanted != null ? "hold" : "release");
        }

        if (WasPressed(KeyKind.Space) && _Animancer.IsGraphInitialized)
        {
            if (_Animancer.Graph.IsGraphPlaying)
                _Animancer.Graph.PauseGraph();
            else
                _Animancer.Graph.UnpauseGraph();
        }

        if (WasPressed(KeyKind.R) && _State != null)
            _State.Time = 0;

        if (WasPressed(KeyKind.T))
            ApplyRootMotion = !ApplyRootMotion;
    }

    private void Play(AnimationClip clip, string source)
    {
        _State = _Animancer.Play(clip, FadeDuration);
        _State.Speed = Speed;
        _Source = source;
    }

    private AnimationClip HeldDirection()
    {
        if (IsHeld(KeyKind.W))
            return Forward;
        if (IsHeld(KeyKind.S))
            return Back;
        if (IsHeld(KeyKind.A))
            return Left;
        if (IsHeld(KeyKind.D))
            return Right;
        return null;
    }

    private enum KeyKind { W, A, S, D, Space, R, T }

#if ENABLE_INPUT_SYSTEM
    private static ButtonControl Control(KeyKind kind)
    {
        Keyboard kb = Keyboard.current;
        if (kb == null)
            return null;

        switch (kind)
        {
            case KeyKind.W: return kb.wKey;
            case KeyKind.A: return kb.aKey;
            case KeyKind.S: return kb.sKey;
            case KeyKind.D: return kb.dKey;
            case KeyKind.Space: return kb.spaceKey;
            case KeyKind.R: return kb.rKey;
            case KeyKind.T: return kb.tKey;
            default: return null;
        }
    }

    private static bool IsHeld(KeyKind kind)
    {
        ButtonControl control = Control(kind);
        return control != null && control.isPressed;
    }

    private static bool WasPressed(KeyKind kind)
    {
        ButtonControl control = Control(kind);
        return control != null && control.wasPressedThisFrame;
    }

    private static bool WasPressedDigit(int digit)
    {
        Keyboard kb = Keyboard.current;
        if (kb == null)
            return false;

        ButtonControl control;
        switch (digit)
        {
            case 1: control = kb.digit1Key; break;
            case 2: control = kb.digit2Key; break;
            case 3: control = kb.digit3Key; break;
            case 4: control = kb.digit4Key; break;
            case 5: control = kb.digit5Key; break;
            case 6: control = kb.digit6Key; break;
            case 7: control = kb.digit7Key; break;
            case 8: control = kb.digit8Key; break;
            case 9: control = kb.digit9Key; break;
            default: return false;
        }

        return control != null && control.wasPressedThisFrame;
    }
#else
    private static KeyCode Code(KeyKind kind)
    {
        switch (kind)
        {
            case KeyKind.W: return KeyCode.W;
            case KeyKind.A: return KeyCode.A;
            case KeyKind.S: return KeyCode.S;
            case KeyKind.D: return KeyCode.D;
            case KeyKind.Space: return KeyCode.Space;
            case KeyKind.R: return KeyCode.R;
            default: return KeyCode.T;
        }
    }

    private static bool IsHeld(KeyKind kind) => Input.GetKey(Code(kind));

    private static bool WasPressed(KeyKind kind) => Input.GetKeyDown(Code(kind));

    private static bool WasPressedDigit(int digit) => Input.GetKeyDown(KeyCode.Alpha0 + digit);
#endif

    private void OnGUI()
    {
        GUIStyle style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };

        GUILayout.BeginArea(new Rect(10, 10, 470, 640), GUI.skin.box);

        GUILayout.Label("<b>Undisputed clip test</b>", style);

#if ENABLE_INPUT_SYSTEM
        GUILayout.Label("input: new Input System, keyboard " +
            (Keyboard.current != null ? "<color=lime>detected</color>" : "<color=red>NOT FOUND</color>"), style);
#else
        GUILayout.Label("input: legacy Input Manager", style);
#endif

        GUILayout.Space(6);
        GUILayout.Label("1-9 play | hold WASD | Space pause | R restart | T root motion", style);
        GUILayout.Space(6);

        for (int i = 0; i < Clips.Count && i < 9; i++)
        {
            AnimationClip clip = Clips[i];
            bool playing = _State != null && clip != null && clip == _State.Clip;

            string label = (playing ? "<color=lime>></color> " : "   ") + (i + 1) + "  ";
            label += clip != null
                ? clip.name + "  <i>" + clip.length.ToString("0.0") + "s</i>"
                : "<color=red>empty</color>";

            GUILayout.Label(label, style);
        }

        GUILayout.Space(8);

        if (_State != null)
        {
            AnimationClip clip = _State.Clip;

            GUILayout.Label("playing : <b>" + (clip != null ? clip.name : "?") + "</b>  (" + _Source + ")", style);
            GUILayout.Label("time    : " + _State.Time.ToString("0.00") + " / " + _State.Length.ToString("0.00") +
                "   norm " + _State.NormalizedTime.ToString("0.00"), style);
            GUILayout.Label("weight  : " + _State.Weight.ToString("0.00") +
                "   speed " + _State.Speed.ToString("0.00") +
                "   " + (_State.IsPlaying ? "<color=lime>running</color>" : "<color=orange>stopped</color>"), style);
            GUILayout.Label("humanoid: " + (clip != null && clip.isHumanMotion
                ? "<color=lime>yes - muscle clip, retargeting</color>"
                : "<color=red>NO - will not retarget</color>"), style);
        }
        else
        {
            GUILayout.Label("<color=orange>nothing playing - assign clips in the inspector</color>", style);
        }

        GUILayout.Space(8);

        Animator animator = _Animancer != null ? _Animancer.Animator : null;
        string avatarState = "<color=red>none</color>";
        if (animator != null && animator.avatar != null)
        {
            avatarState = animator.avatar.isHuman
                ? "<color=lime>humanoid</color>"
                : "<color=red>not humanoid</color>";
        }

        GUILayout.Label("animator: " + (animator != null ? animator.name : "<color=red>missing</color>") +
            "   avatar " + avatarState, style);
        GUILayout.Label("rootMotion: " + (ApplyRootMotion ? "<color=lime>on</color>" : "off") +
            "   worldPos " + transform.position.ToString("0.00"), style);
        GUILayout.Label("graph   : " + (_Animancer != null && _Animancer.IsGraphInitialized
            ? "<color=lime>initialised</color>"
            : "<color=red>not initialised</color>"), style);

        GUILayout.Space(8);

        GUILayout.Label("speed", style);
        Speed = GUILayout.HorizontalSlider(Speed, 0f, 2f);
        if (_State != null)
            _State.Speed = Speed;

        GUILayout.Label("fade  " + FadeDuration.ToString("0.00") + "s", style);
        FadeDuration = GUILayout.HorizontalSlider(FadeDuration, 0f, 1f);

        GUILayout.EndArea();
    }
}
