using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Zero-click bring-up, so the game is never quietly missing half of itself:
///
///  • the Animator's punch layer is rebuilt whenever clips are assigned but the layer is not wired, and
///  • the FEEL STACK is restored whenever a boxer has no <see cref="ImpactFeedback"/> / <see cref="BoxerVFX"/> or a
///    bag has no <see cref="BagAudio"/> — impact bursts, shockwave rings, glove trails, camera kick, hit-stop and
///    the bag's voice all come from components that live in the scene, and a scene saved before they existed simply
///    does not have them. Rather than relying on someone remembering the menu, this runs the same setup itself.
///
/// Both run after script reloads and just before entering Play mode, only in edit mode, and only when something is
/// actually missing. The base animator layer (stance blends) is never touched.
/// </summary>
[InitializeOnLoad]
public static class BoxerAutoWire
{
    static BoxerAutoWire()
    {
        EditorApplication.delayCall += TryWire;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode) TryWire();
        };
    }

    private static void TryWire()
    {
        if (Application.isPlaying) return;
        RestoreFeelStack();
        WirePunchLayer();
    }

    /// <summary>Put the impact/VFX/audio components back on the scene when they are missing.</summary>
    private static void RestoreFeelStack()
    {
        bool missing = false;
        foreach (BoxerPunchController boxer in Object.FindObjectsByType<BoxerPunchController>())
        {
            GameObject go = boxer.gameObject;
            if (go.GetComponent<ImpactFeedback>() == null || go.GetComponent<BoxerVFX>() == null
                || go.GetComponent<PunchVariation>() == null || go.GetComponent<FootstepAudio>() == null
                || go.GetComponent<GloveSquash>() == null)
                missing = true;
        }
        foreach (PunchingBag bag in Object.FindObjectsByType<PunchingBag>())
            if (bag.GetComponent<BagAudio>() == null) missing = true;

        if (!missing) return;
        Debug.Log("BoxerAutoWire: the impact / VFX / audio stack is missing from the scene — restoring it now " +
                  "(same as Tools ▸ Boxer ▸ Rebuild Boxing System). Save the scene to keep it.");
        BoxerImpactSetup.Rebuild();
    }

    private static void WirePunchLayer()
    {
        BoxerPunchController punch = Object.FindAnyObjectByType<BoxerPunchController>();
        if (punch == null || !HasAnyClip(punch)) return;

        Animator animator = punch.GetComponent<Animator>();
        if (animator == null || !(animator.runtimeAnimatorController is AnimatorController controller)) return;
        if (IsWired(controller, punch.upperBodyLayerName)) return;

        Debug.Log("BoxerAutoWire: punch clips are assigned but the punch layer is not wired — wiring it now (no clicks needed).", punch);
        BoxerAnimatorSetup.WireAnimator(punch);
    }

    private static bool HasAnyClip(BoxerPunchController punch)
    {
        return !punch.leftClips.IsEmpty || !punch.rightClips.IsEmpty;
    }

    /// <summary>Wired = the punch layer exists and has at least one transition conditioned on PunchType.</summary>
    private static bool IsWired(AnimatorController controller, string layerName)
    {
        if (string.IsNullOrEmpty(layerName)) layerName = "Upper Body";
        foreach (AnimatorControllerLayer layer in controller.layers)
        {
            if (layer.name != layerName || layer.stateMachine == null) continue;
            foreach (AnimatorStateTransition transition in layer.stateMachine.anyStateTransitions)
                foreach (AnimatorCondition condition in transition.conditions)
                    if (condition.parameter == "PunchType") return true;
            return false;
        }
        return false;
    }
}
