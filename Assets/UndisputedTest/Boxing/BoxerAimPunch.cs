using RootMotion.Dynamics;
using RootMotion.FinalIK;
using UnityEngine;

/// <summary>
/// Aims punches with FinalIK's <see cref="AimIK"/> instead of dragging the fist sideways.
/// </summary>
/// <remarks>
/// The controller's own aim correction is a POSITION offset: it takes the difference between
/// where the fist is and where the target is and slides the hand across. When that correction
/// is large, or the target is off to one side, the hand travels sideways — which is what a
/// punch never does.
///
/// AimIK is the right shape for this. It ROTATES a chain (chest → upper arm → forearm) until
/// the fist's own forward axis points at the target, so the punch stays one straight motion
/// down the arm and simply leaves on a better line. Nothing slides.
///
/// Ordering matters and is already handled: <see cref="BoxerPhysics.OrderSolvers"/> puts
/// LookAt first, FullBodyBipedIK second, and appends everything else after — so this gets the
/// last word on the punching arm. PuppetMaster collects it automatically because it is a
/// SolverManager under the target root; if it is added while the game is running it registers
/// itself below.
///
/// The aim axis is MEASURED off the rig (wrist → middle knuckle, or the forearm if the rig has
/// no fingers), never assumed — a guessed axis is what makes IK aiming point through the elbow.
/// </remarks>
[DefaultExecutionOrder(-9)]     // after BoxerPunchController (-10), before PuppetMaster
[RequireComponent(typeof(BoxerPunchController))]
public class BoxerAimPunch : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("What the punches aim at. Leave empty to use the controller's own AimPoint.")]
    public Transform Target;

    [Tooltip("Aim at the target's collider centre rather than its pivot. A hung bag's pivot is " +
             "at the ceiling mount, so aiming at the pivot punches upward.")]
    public bool UseColliderCentre = true;

    [Tooltip("How far down from the top of the collider to aim, as a fraction of its height.")]
    [Range(0f, 1f)] public float AimDownFromTop = 0.35f;

    [Header("Strength")]
    [Tooltip("How much of the punch line AimIK is allowed to own. 1 = the fist always points " +
             "dead at the target; lower leaves more of the authored technique showing.")]
    [Range(0f, 1f)] public float Weight = 0.75f;

    [Tooltip("Seconds to blend the aim in once the hand starts driving.")]
    [Min(0f)] public float FadeIn = 0.05f;

    [Tooltip("Seconds to release it once the punch is over. Slower than the fade in, so the " +
             "arm is never snapped off line on the way home.")]
    [Min(0f)] public float FadeOut = 0.3f;

    [Tooltip("Keep aiming this long after the punch leaves Drive/Impact, so the arm settles " +
             "back rather than dropping off line the frame the phase changes.")]
    [Min(0f)] public float HoldAfterImpact = 0.35f;

    [Tooltip("How much of the aim is already on while the punch is being LOADED (Control), as " +
             "a fraction of Weight. A boxer lines the shot up before he throws it, and coming " +
             "up here means the weight is already there when the punch fires instead of " +
             "chasing it through a flight that lasts a few frames. 0 = only aim once flying.")]
    [Range(0f, 1f)] public float AimWhileLoading = 0.6f;

    [Header("Chain")]
    [Tooltip("How much the chest is allowed to turn into the aim. Keep it low - the torso is " +
             "already turned by the pose chain, and doubling up over-rotates the whole body.")]
    [Range(0f, 1f)] public float ChestInvolvement = 0.25f;

    [Header("Solvers (filled by Setup Aim Punch IK)")]
    public AimIK LeftAim;
    public AimIK RightAim;

    [Header("Debug")]
    public bool ShowOverlay;
    public bool DrawGizmo = true;

    private BoxerPunchController _Controller;
    private Animator _Animator;
    private readonly float[] _Weight = new float[2];
    private readonly float[] _HoldLeft = new float[2];
    private Vector3 _AimWorld;
    private Collider _TargetCollider;

    /************************************************************************************/

    private void Awake()
    {
        _Controller = GetComponent<BoxerPunchController>();
        _Animator = GetComponent<Animator>();

        if (LeftAim == null || RightAim == null)
            FindSolvers();

        Disarm(LeftAim);
        Disarm(RightAim);

        RegisterWithPuppet();
    }

    private void FindSolvers()
    {
        foreach (AimIK ik in GetComponents<AimIK>())
        {
            if (ik.solver == null || ik.solver.transform == null)
                continue;

            bool left = ik.solver.transform == Bone(HumanBodyBones.LeftHand);
            if (left && LeftAim == null)
                LeftAim = ik;
            else if (!left && RightAim == null)
                RightAim = ik;
        }
    }

    /// <summary>
    /// PuppetMaster grabs every SolverManager under its target root when it initiates. A solver
    /// added after that has to announce itself or it silently never solves.
    /// </summary>
    private void RegisterWithPuppet()
    {
        PuppetMaster puppet = null;
        foreach (PuppetMaster pm in FindObjectsByType<PuppetMaster>(FindObjectsSortMode.None))
        {
            if (pm.targetRoot == transform)
            {
                puppet = pm;
                break;
            }
        }

        if (puppet == null || puppet.solvers == null)
            return;

        foreach (AimIK ik in new[] { LeftAim, RightAim })
        {
            if (ik != null && !puppet.solvers.Contains(ik))
                puppet.solvers.Add(ik);
        }
    }

    private static void Disarm(AimIK ik)
    {
        if (ik != null && ik.solver != null)
            ik.solver.IKPositionWeight = 0f;
    }

    private Transform Bone(HumanBodyBones bone)
        => _Animator != null ? _Animator.GetBoneTransform(bone) : null;

    /************************************************************************************/

    private void Update()
    {
        Advance(Time.deltaTime);
        Push();
    }

    /// <summary>
    /// PuppetMaster solves the IK inside FixedUpdate, so the weights have to be written there
    /// too — but never advanced there.
    /// </summary>
    /// <remarks>
    /// Update runs exactly once per rendered frame; FixedUpdate runs zero to several times.
    /// Integrating in both ran the blend at an unpredictable multiple of real time, which is
    /// why the fades never matched their inspector values. Advancing lives in Update alone and
    /// this just republishes the result.
    /// </remarks>
    private void FixedUpdate() => Push();

    /// <summary>
    /// Moves each hand's aim weight toward where the punch says it should be.
    /// </summary>
    /// <remarks>
    /// The envelope covers the WHOLE punch, not just the flight. A boxer lines a shot up while
    /// he loads it and stays on it through the contact; aiming only during Drive gave a window
    /// a few frames wide on a fast jab, so the weight was gone before it read as anything.
    ///
    ///     Control            aim comes up to AimWhileLoading of full — the shot is being lined up
    ///     Drive / Impact     full weight
    ///     Recover / hold     falls away over FadeOut
    /// </remarks>
    private void Advance(float dt)
    {
        if (_Controller == null)
            return;

        for (int hand = 0; hand < 2; hand++)
        {
            BoxerPunchController.Phase phase = _Controller.HandPhase(hand);

            float target;
            switch (phase)
            {
                case BoxerPunchController.Phase.Control:
                    target = Weight * AimWhileLoading;
                    break;

                case BoxerPunchController.Phase.Drive:
                case BoxerPunchController.Phase.Impact:
                    target = Weight;
                    _HoldLeft[hand] = HoldAfterImpact;
                    break;

                default:
                    if (_HoldLeft[hand] > 0f)
                    {
                        _HoldLeft[hand] -= dt;
                        target = Weight;
                    }
                    else
                    {
                        target = 0f;
                    }
                    break;
            }

            float fade = target > _Weight[hand] ? FadeIn : FadeOut;
            _Weight[hand] = fade <= 0f
                ? target
                : Mathf.MoveTowards(_Weight[hand], target, dt / fade);
        }
    }

    private void Push()
    {
        _AimWorld = AimPoint();

        Write(LeftAim, _Weight[0]);
        Write(RightAim, _Weight[1]);
    }

    private void Write(AimIK ik, float weight)
    {
        if (ik == null || ik.solver == null)
            return;

        ik.solver.IKPosition = _AimWorld;
        ik.solver.IKPositionWeight = weight;
    }

    /// <summary>Where on the target to point the fist.</summary>
    public Vector3 AimPoint()
    {
        if (Target == null)
            return _Controller != null ? _Controller.AimPoint : transform.position + transform.forward;

        if (!UseColliderCentre)
            return Target.position;

        if (_TargetCollider == null)
            _TargetCollider = Target.GetComponentInChildren<Collider>();

        if (_TargetCollider == null)
            return Target.position;

        Bounds b = _TargetCollider.bounds;
        return new Vector3(b.center.x, b.max.y - b.size.y * AimDownFromTop, b.center.z);
    }

    /************************************************************************************/

    /// <summary>
    /// Builds both chains from the humanoid rig. Safe to call again after changing the rig.
    /// </summary>
    public bool Configure()
    {
        _Animator = GetComponent<Animator>();
        if (_Animator == null || _Animator.avatar == null || !_Animator.avatar.isHuman)
        {
            Debug.LogError("BoxerAimPunch: needs a Humanoid Animator.", this);
            return false;
        }

        return Build(ref LeftAim, true) & Build(ref RightAim, false);
    }

    private bool Build(ref AimIK ik, bool left)
    {
        Transform chest = Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest);
        Transform upper = Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        Transform lower = Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        Transform hand = Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);

        if (upper == null || lower == null || hand == null)
        {
            Debug.LogError($"BoxerAimPunch: the {(left ? "left" : "right")} arm is missing bones.", this);
            return false;
        }

        if (ik == null)
            ik = gameObject.AddComponent<AimIK>();

        ik.solver.transform = hand;
        ik.solver.axis = hand.InverseTransformDirection(FistDirection(hand, lower, left)).normalized;
        ik.solver.target = null;            // driven through IKPosition instead
        ik.solver.poleWeight = 0f;
        ik.solver.tolerance = 0f;
        ik.solver.maxIterations = 4;

        var chain = chest != null
            ? new[] { chest, upper, lower }
            : new[] { upper, lower };

        ik.solver.SetChain(chain, transform);

        // The chest is already turned by the pose chain; letting the aim turn it again
        // over-rotates the whole body, so its bone gets a fraction of the authority.
        for (int i = 0; i < ik.solver.bones.Length; i++)
        {
            bool isChest = chest != null && ik.solver.bones[i].transform == chest;
            ik.solver.bones[i].weight = isChest ? ChestInvolvement : 1f;
        }

        ik.solver.IKPositionWeight = 0f;
        ik.enabled = true;
        return true;
    }

    /// <summary>
    /// The way the fist points: wrist to middle knuckle if the rig has fingers, else along the
    /// forearm. Guessing this axis is the classic reason IK aiming points through the elbow.
    /// </summary>
    private Vector3 FistDirection(Transform hand, Transform lowerArm, bool left)
    {
        Transform knuckle = Bone(left ? HumanBodyBones.LeftMiddleProximal
                                      : HumanBodyBones.RightMiddleProximal);

        if (knuckle != null)
        {
            Vector3 d = knuckle.position - hand.position;
            if (d.sqrMagnitude > 1e-6f)
                return d.normalized;
        }

        Vector3 along = hand.position - lowerArm.position;
        return along.sqrMagnitude > 1e-6f ? along.normalized : hand.forward;
    }

    /************************************************************************************/

    private void OnDrawGizmos()
    {
        if (!DrawGizmo || !Application.isPlaying)
            return;

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(_AimWorld, 0.08f);

        for (int h = 0; h < 2; h++)
        {
            AimIK ik = h == 0 ? LeftAim : RightAim;
            if (ik == null || ik.solver == null || ik.solver.transform == null || _Weight[h] <= 0.01f)
                continue;

            Transform fist = ik.solver.transform;
            Gizmos.color = Color.Lerp(Color.yellow, Color.red, _Weight[h]);
            Gizmos.DrawWireSphere(fist.position, 0.05f);
            Gizmos.DrawLine(fist.position, _AimWorld);
            Gizmos.DrawRay(fist.position, fist.TransformDirection(ik.solver.axis) * 0.35f);
        }
    }

    private void OnGUI()
    {
        if (!ShowOverlay)
            return;

        var style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
        GUILayout.BeginArea(new Rect(10, Screen.height - 130, 420, 120), GUI.skin.box);
        GUILayout.Label("<b>Aim IK</b>", style);
        GUILayout.Label($"target : {(Target != null ? Target.name : "<controller AimPoint>")}", style);
        GUILayout.Label($"aim    : {_AimWorld}", style);
        GUILayout.Label($"weight : L {_Weight[0]:0.00}   R {_Weight[1]:0.00}   (max {Weight:0.00})", style);
        if (_Controller != null)
        {
            GUILayout.Label($"phase  : L {_Controller.HandPhase(0)}  hold {_HoldLeft[0]:0.00}   " +
                            $"R {_Controller.HandPhase(1)}  hold {_HoldLeft[1]:0.00}", style);
        }
        GUILayout.Label($"solvers: L {(LeftAim != null ? "ok" : "<color=red>missing</color>")}   " +
                        $"R {(RightAim != null ? "ok" : "<color=red>missing</color>")}", style);
        GUILayout.EndArea();
    }
}
