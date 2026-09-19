using RootMotion.FinalIK;
using UnityEngine;

/// <summary>
/// The glove GIVES on impact: a short squash pulse on the hand bone — compressed along the punch line, bulging
/// slightly across it — so the leather visibly takes the hit instead of staying rigid. Bone SCALE is the one
/// channel neither the Animator (no scale curves in these clips) nor Final IK (positions/rotations only) ever
/// writes, so the pulse survives the whole animation/IK pipeline; it is applied in FBBIK's OnPostUpdate — right
/// after the solve, order-proof — and the exact base scale is restored, so nothing drifts on the 2x rig.
/// Works on the skinned gloves today and on separate attached glove meshes tomorrow (they ride the same bones).
/// Auto-added by the boxing system setup; lives next to <see cref="BoxerPunchController"/>.
/// </summary>
[DisallowMultipleComponent]
public class GloveSquash : MonoBehaviour
{
    [Tooltip("Squash at full strength: 0.14 = the glove compresses 14% along the punch line on a clean hit.")]
    [Range(0f, 0.35f)]
    [SerializeField] private float squashAmount = 0.14f;

    [Tooltip("How long one squash pulse lasts (seconds).")]
    [Range(0.04f, 0.3f)]
    [SerializeField] private float pulseSeconds = 0.11f;

    private BoxerPunchController controller;
    private FullBodyBipedIK ik;
    private readonly Transform[] hands = new Transform[2];
    private readonly Vector3[] baseScale = new Vector3[2];
    private readonly Vector3[] axisLocal = new Vector3[2];
    private readonly float[] pulse = new float[2];
    private readonly float[] amount = new float[2];

    private void Awake()
    {
        controller = GetComponent<BoxerPunchController>();
        ik = GetComponent<FullBodyBipedIK>();
        Animator animator = GetComponent<Animator>();
        if (controller == null || ik == null || animator == null || !animator.isHuman)
        {
            enabled = false;
            return;
        }
        hands[0] = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        hands[1] = animator.GetBoneTransform(HumanBodyBones.RightHand);
        for (int i = 0; i < 2; i++) if (hands[i] != null) baseScale[i] = hands[i].localScale;
    }

    private void OnEnable()
    {
        if (controller != null) controller.PunchLanded += OnPunchLanded;
        if (ik != null) ik.solver.OnPostUpdate += ApplySquash;
    }

    private void OnDisable()
    {
        if (controller != null) controller.PunchLanded -= OnPunchLanded;
        if (ik != null) ik.solver.OnPostUpdate -= ApplySquash;
        for (int i = 0; i < 2; i++) if (hands[i] != null) hands[i].localScale = baseScale[i];
    }

    private void OnPunchLanded(BoxerPunchController.Hand hand, PunchingBag bag, PunchingBag.HitInfo info)
    {
        int i = (int)hand;
        if (hands[i] == null) return;
        pulse[i] = 1f;
        amount[i] = squashAmount * Mathf.Lerp(0.55f, 1f, info.strength);
        axisLocal[i] = (Quaternion.Inverse(hands[i].rotation) * info.direction).normalized;
    }

    /// <summary>Runs right after the FBBIK solve every frame — the last writer before rendering.</summary>
    private void ApplySquash()
    {
        for (int i = 0; i < 2; i++)
        {
            if (hands[i] == null) continue;
            if (pulse[i] <= 0f) { hands[i].localScale = baseScale[i]; continue; }

            pulse[i] -= Time.deltaTime / Mathf.Max(0.02f, pulseSeconds);
            float s = Mathf.Sin(Mathf.Clamp01(pulse[i]) * Mathf.PI) * amount[i];   // eased in and out
            Vector3 a = new Vector3(Mathf.Abs(axisLocal[i].x), Mathf.Abs(axisLocal[i].y), Mathf.Abs(axisLocal[i].z));
            // Compress along the punch line, bulge half as much across it — volume-ish preserving.
            Vector3 mul = Vector3.one - a * s + (Vector3.one - a) * (s * 0.5f);
            hands[i].localScale = Vector3.Scale(baseScale[i], mul);
        }
    }
}
