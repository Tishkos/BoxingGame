using System.Collections.Generic;
using RootMotion.FinalIK;
using UnityEngine;

/// <summary>
/// A standalone rig for learning what <see cref="AimIK"/> actually does. No controller, no
/// PuppetMaster, no punches — one humanoid, one chain, one target you can drag around.
/// </summary>
/// <remarks>
/// AimIK does not place a hand anywhere. It ROTATES a chain of bones until one transform's
/// chosen LOCAL AXIS points at <c>IKPosition</c>. Everything that goes wrong with it comes from
/// one of three things, and this exposes all three live:
///
///   • <b>the axis</b> — which way the aiming transform considers "forward". Get this wrong and
///     the arm aims through the elbow. It is measured off the rig here, and drawn as a ray.
///   • <b>the chain</b> — which bones may turn, and how much each contributes. A long chain
///     turns the whole body; a short one only swivels the forearm.
///   • <b>the weight</b> — how much of the way to the target it goes.
///
/// Press Play, drag the target in the Scene view, and watch the ray. The sliders change the
/// chain live so the difference between "spine included" and "arm only" is visible immediately.
/// </remarks>
[RequireComponent(typeof(Animator))]
public class AimIKStudy : MonoBehaviour
{
    public enum Side { Left, Right }

    /// <summary>
    /// Which solver drives the arm. This is the whole lesson.
    /// </summary>
    public enum Mode
    {
        /// <summary>Rotates the chain so the fist POINTS at the target. Never reaches it.</summary>
        AimOnly,

        /// <summary>Trigonometric 3-bone reach: puts the hand ON the target, within arm length.</summary>
        ReachOnly,

        /// <summary>Reach places the hand, then the fist is rotated to point down the same line.</summary>
        ReachAndPoint,
    }

    [Header("What is aiming")]
    public Side Hand = Side.Right;

    [Tooltip("AimOnly = AimIK: the fist POINTS at the target but never travels to it, because " +
             "AimIK is a rotation solver and distance is not part of what it solves. " +
             "ReachOnly = LimbIK: the hand actually arrives at the position. " +
             "ReachAndPoint = both, which is what a punch wants.")]
    public Mode Solver = Mode.ReachAndPoint;

    [Tooltip("Drag this around in the Scene view while playing. One is created if left empty.")]
    public Transform Target;

    [Header("Chain — which bones may turn")]
    public bool IncludeSpine;
    public bool IncludeChest = true;
    public bool IncludeUpperArm = true;
    public bool IncludeForeArm = true;

    [Tooltip("Contribution of the spine/chest bones. The arm bones always contribute fully.")]
    [Range(0f, 1f)] public float TorsoWeight = 0.25f;

    [Header("Solver")]
    [Range(0f, 1f)] public float Weight = 1f;

    [Tooltip("How many passes the solver makes. More is more exact and more expensive.")]
    [Range(1, 12)] public int Iterations = 4;

    [Tooltip("Stop once the aim is this close (degrees). 0 = always use every iteration.")]
    [Range(0f, 10f)] public float Tolerance = 0f;

    [Tooltip("Limits how far from its animated direction the aim may pull. 0 = unrestricted, " +
             "1 = pinned to the animation.")]
    [Range(0f, 1f)] public float ClampWeight = 0.1f;

    [Header("Debug")]
    public bool DrawChain = true;
    public bool DrawAxis = true;
    public bool ShowPanel = true;

    private Animator _Animator;
    private AimIK _IK;
    private LimbIK _Limb;
    private float _ReachErrorMetres;
    private float _ArmLength;
    private float _TargetDistance;
    private Side _BuiltFor = (Side)(-1);
    private string _ChainDescription = "";
    private float _ErrorDegrees;

    /************************************************************************************/

    private void Awake()
    {
        _Animator = GetComponent<Animator>();

        if (Target == null)
            Target = CreateTarget();

        Build();
    }

    private Transform CreateTarget()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "AimIK Study Target";
        go.transform.localScale = Vector3.one * 0.15f;

        Collider col = go.GetComponent<Collider>();
        if (col != null)
            Destroy(col);

        Transform head = Bone(HumanBodyBones.Head);
        go.transform.position = head != null
            ? head.position + transform.forward * 1.2f
            : transform.position + transform.forward * 1.2f + Vector3.up * 1.4f;

        return go.transform;
    }

    private Transform Bone(HumanBodyBones bone)
        => _Animator != null ? _Animator.GetBoneTransform(bone) : null;

    /************************************************************************************/

    /// <summary>Rebuilds the solver chain from the current toggles.</summary>
    public void Build()
    {
        if (_Animator == null)
            _Animator = GetComponent<Animator>();

        if (_Animator == null || _Animator.avatar == null || !_Animator.avatar.isHuman)
        {
            Debug.LogError("AimIKStudy: needs a Humanoid Animator.", this);
            enabled = false;
            return;
        }

        bool left = Hand == Side.Left;

        Transform upper = Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        Transform fore = Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        Transform hand = Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);

        if (upper == null || fore == null || hand == null)
        {
            Debug.LogError("AimIKStudy: that arm is missing bones.", this);
            enabled = false;
            return;
        }

        var chain = new List<Transform>();
        var weights = new List<float>();

        if (IncludeSpine)
        {
            Transform spine = Bone(HumanBodyBones.Spine);
            if (spine != null) { chain.Add(spine); weights.Add(TorsoWeight); }
        }

        if (IncludeChest)
        {
            Transform chest = Bone(HumanBodyBones.UpperChest);
            if (chest == null)
                chest = Bone(HumanBodyBones.Chest);
            if (chest != null) { chain.Add(chest); weights.Add(TorsoWeight); }
        }

        if (IncludeUpperArm) { chain.Add(upper); weights.Add(1f); }
        if (IncludeForeArm) { chain.Add(fore); weights.Add(1f); }

        if (chain.Count == 0)
        {
            _ChainDescription = "<empty — nothing can turn>";
            if (_IK != null)
                _IK.solver.IKPositionWeight = 0f;
            return;
        }

        if (_IK == null)
            _IK = gameObject.AddComponent<AimIK>();

        _IK.solver.transform = hand;

        // MEASURED, not guessed: the way the fist actually points on this rig.
        _IK.solver.axis = hand.InverseTransformDirection(FistDirection(hand, fore, left)).normalized;

        _IK.solver.target = null;          // driven through IKPosition
        _IK.solver.poleWeight = 0f;

        _IK.solver.SetChain(chain.ToArray(), transform);
        for (int i = 0; i < _IK.solver.bones.Length && i < weights.Count; i++)
            _IK.solver.bones[i].weight = weights[i];

        // The REACH solver. Trigonometric over exactly three bones, so it can place the hand
        // on a point instead of only turning toward it — which is the thing AimIK cannot do.
        if (_Limb == null)
            _Limb = gameObject.AddComponent<LimbIK>();

        _Limb.solver.SetChain(upper, fore, hand, transform);
        _Limb.solver.target = null;

        _ArmLength = Vector3.Distance(upper.position, fore.position)
                   + Vector3.Distance(fore.position, hand.position);

        _BuiltFor = Hand;
        _ChainDescription = string.Join(" -> ", chain.ConvertAll(t => t.name));
    }

    private Vector3 FistDirection(Transform hand, Transform fore, bool left)
    {
        Transform knuckle = Bone(left ? HumanBodyBones.LeftMiddleProximal
                                      : HumanBodyBones.RightMiddleProximal);

        if (knuckle != null)
        {
            Vector3 d = knuckle.position - hand.position;
            if (d.sqrMagnitude > 1e-6f)
                return d.normalized;
        }

        Vector3 along = hand.position - fore.position;
        return along.sqrMagnitude > 1e-6f ? along.normalized : hand.forward;
    }

    /************************************************************************************/

    private void LateUpdate()
    {
        if (_IK == null || Target == null)
            return;

        if (_BuiltFor != Hand)
            Build();

        bool reach = Solver != Mode.AimOnly;
        bool point = Solver != Mode.ReachOnly;

        // AimIK: pure rotation. IKPosition is a DIRECTION reference for it, never a destination.
        _IK.solver.IKPosition = Target.position;
        _IK.solver.IKPositionWeight = point ? Weight : 0f;
        _IK.solver.maxIterations = Iterations;
        _IK.solver.tolerance = Tolerance;
        _IK.solver.clampWeight = ClampWeight;

        // LimbIK: the hand is actually moved to IKPosition, as far as the arm can stretch.
        if (_Limb != null)
        {
            _Limb.solver.IKPosition = Target.position;
            _Limb.solver.IKPositionWeight = reach ? Weight : 0f;
            _Limb.solver.IKRotationWeight = 0f;
        }

        // How far off the aim ended up — the number worth watching while you change the chain.
        Transform fist = _IK.solver.transform;
        if (fist != null)
        {
            Vector3 aimed = fist.TransformDirection(_IK.solver.axis);
            Vector3 wanted = Target.position - fist.position;
            if (wanted.sqrMagnitude > 1e-6f)
                _ErrorDegrees = Vector3.Angle(aimed, wanted);

            // How far the HAND still is from the target — the number that answers
            // "why does it not extend exactly to that position".
            _ReachErrorMetres = Vector3.Distance(fist.position, Target.position);

            Transform shoulder = Bone(Hand == Side.Left ? HumanBodyBones.LeftUpperArm
                                                        : HumanBodyBones.RightUpperArm);
            _TargetDistance = shoulder != null
                ? Vector3.Distance(shoulder.position, Target.position)
                : 0f;
        }
    }

    /************************************************************************************/

    private void OnDrawGizmos()
    {
        if (_IK == null || _IK.solver == null || !Application.isPlaying)
            return;

        Transform fist = _IK.solver.transform;
        if (fist == null)
            return;

        if (DrawChain && _IK.solver.bones != null)
        {
            Gizmos.color = Color.cyan;
            for (int i = 0; i < _IK.solver.bones.Length; i++)
            {
                Transform b = _IK.solver.bones[i].transform;
                if (b == null)
                    continue;

                Gizmos.DrawWireSphere(b.position, 0.03f);
                if (i + 1 < _IK.solver.bones.Length && _IK.solver.bones[i + 1].transform != null)
                    Gizmos.DrawLine(b.position, _IK.solver.bones[i + 1].transform.position);
            }
        }

        if (DrawAxis)
        {
            // Green = where the fist points. Yellow = where it should point. They converge as
            // the solver succeeds, and the angle between them IS the error readout.
            Gizmos.color = Color.green;
            Gizmos.DrawRay(fist.position, fist.TransformDirection(_IK.solver.axis) * 0.5f);

            if (Target != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(fist.position, Target.position);
                Gizmos.DrawWireSphere(Target.position, 0.07f);
            }
        }
    }

    private void OnGUI()
    {
        if (!ShowPanel)
            return;

        var style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
        GUILayout.BeginArea(new Rect(10, 10, 430, 430), GUI.skin.box);

        GUILayout.Label("<b>AimIK study</b>   drag the target in the Scene view", style);
        GUILayout.Label($"chain : {_ChainDescription}", style);
        GUILayout.Label($"axis  : {(_IK != null ? _IK.solver.axis.ToString("0.00") : "-")}   " +
                        "(green ray = where the fist points)", style);

        string colour = _ErrorDegrees < 2f ? "lime" : _ErrorDegrees < 15f ? "yellow" : "red";
        GUILayout.Label($"aim error   : <color={colour}>{_ErrorDegrees:0.0}°</color> off target", style);

        bool outOfRange = _TargetDistance > _ArmLength;
        string reachColour = _ReachErrorMetres < 0.03f ? "lime" : outOfRange ? "orange" : "red";
        GUILayout.Label($"reach error : <color={reachColour}>{_ReachErrorMetres:0.000} m</color> " +
                        $"between the hand and the target", style);
        GUILayout.Label($"arm {_ArmLength:0.00} m   target is {_TargetDistance:0.00} m from the shoulder" +
                        (outOfRange ? "   <color=orange>OUT OF RANGE</color>" : ""), style);

        GUILayout.Space(4);
        GUILayout.Label(Solver switch
        {
            Mode.AimOnly => "<color=yellow>AimIK only — it POINTS, it never travels. Reach error " +
                            "stays large by design.</color>",
            Mode.ReachOnly => "<color=lime>LimbIK — the hand is placed ON the target, up to arm length.</color>",
            _ => "<color=lime>LimbIK places the hand, AimIK points the fist down the same line.</color>",
        }, style);

        GUILayout.Space(4);
        if (GUILayout.Button($"solver: {Solver}  (click to change)"))
            Solver = (Mode)(((int)Solver + 1) % 3);

        GUILayout.Space(6);

        GUILayout.Label($"weight {Weight:0.00}", style);
        Weight = GUILayout.HorizontalSlider(Weight, 0f, 1f);

        GUILayout.Label($"clamp {ClampWeight:0.00}   (0 = free, 1 = pinned to the animation)", style);
        ClampWeight = GUILayout.HorizontalSlider(ClampWeight, 0f, 1f);

        GUILayout.Label($"iterations {Iterations}", style);
        Iterations = Mathf.RoundToInt(GUILayout.HorizontalSlider(Iterations, 1f, 12f));

        GUILayout.Label($"torso weight {TorsoWeight:0.00}", style);
        float torso = GUILayout.HorizontalSlider(TorsoWeight, 0f, 1f);

        GUILayout.Space(6);
        GUILayout.Label("<b>chain</b> — toggling rebuilds the solver", style);

        bool spine = GUILayout.Toggle(IncludeSpine, " spine");
        bool chest = GUILayout.Toggle(IncludeChest, " chest");
        bool upper = GUILayout.Toggle(IncludeUpperArm, " upper arm");
        bool fore = GUILayout.Toggle(IncludeForeArm, " forearm");

        GUILayout.Space(4);
        bool swap = GUILayout.Button(Hand == Side.Left ? "aiming with LEFT — switch to right"
                                                      : "aiming with RIGHT — switch to left");

        GUILayout.EndArea();

        if (swap)
            Hand = Hand == Side.Left ? Side.Right : Side.Left;

        if (spine != IncludeSpine || chest != IncludeChest ||
            upper != IncludeUpperArm || fore != IncludeForeArm ||
            !Mathf.Approximately(torso, TorsoWeight))
        {
            IncludeSpine = spine;
            IncludeChest = chest;
            IncludeUpperArm = upper;
            IncludeForeArm = fore;
            TorsoWeight = torso;
            Build();
        }
    }
}
