using Animancer;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
/// The fighting layer on top of the aim-study boxer: hit reactions, the stun that follows a clean
/// shot, the dazed sway when the concussion meter is high, and the bout reset.
///
/// Reactions live on three extra Animancer layers (7-9) masked to the torso-and-head region and
/// each arm — the same partition the punch system uses — so a landed shot reads on the upper body
/// without ever owning the legs or the root. Going down is the PuppetMaster ragdoll's job; this
/// component only tells <see cref="AimStudyPhysics"/> to let go.
/// </summary>
[RequireComponent(typeof(AimStudyBoxer))]
[RequireComponent(typeof(BoxerHealth))]
[DefaultExecutionOrder(-15)]
[DisallowMultipleComponent]
public class AimStudyCombat : MonoBehaviour
{
    [Header("Reactions — upper-body clips, head/body picked by where the shot landed")]
    public AnimationClip[] HeadReactions;
    public AnimationClip[] BodyReactions;
    public AnimationClip[] BlockReactions;
    [Tooltip("Looped sway while badly concussed and not punching. Torso/head only.")]
    public AnimationClip DazedIdle;

    public BoxerHealth Health { get; private set; }
    public bool IsStunned => Time.time < stunUntil;

    private AimStudyBoxer boxer;
    private AnimancerComponent animancer;
    private AimStudyPhysics physics;
    private LegsAnimator legs;
    private CharacterController mover;
    private readonly AnimancerLayer[] reactionLayers = new AnimancerLayer[3];
    private readonly AvatarMask[] runtimeMasks = new AvatarMask[3];
    private AnimancerLayer dazedLayer;
    private AnimancerState dazedState;
    private float stunUntil;
    private float reactionEnd;
    private readonly int[] rotation = new int[3];
    private Vector3 startPosition;
    private Quaternion startRotation;
    private bool hasStart;

    private void Awake()
    {
        boxer = GetComponent<AimStudyBoxer>();
        Health = GetComponent<BoxerHealth>();
        animancer = GetComponent<AnimancerComponent>();
        physics = GetComponent<AimStudyPhysics>();
        legs = GetComponentInChildren<LegsAnimator>(true);
        mover = GetComponent<CharacterController>();
    }

    private void Start()
    {
        startPosition = transform.position;
        startRotation = transform.rotation;
        hasStart = true;

        // Torso mask gains the head for reactions — a head shot that leaves the face stiff reads
        // as nothing happening. Root and legs stay off every mask: they belong to the stance.
        runtimeMasks[0] = AimStudyBoxer.CreateRegionMask(0);
        runtimeMasks[0].SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head, true);
        runtimeMasks[1] = AimStudyBoxer.CreateRegionMask(1);
        runtimeMasks[2] = AimStudyBoxer.CreateRegionMask(2);
        for (int i = 0; i < reactionLayers.Length; i++)
        {
            reactionLayers[i] = animancer.Layers[7 + i];
            reactionLayers[i].Mask = runtimeMasks[i];
            reactionLayers[i].SetLayerWeightOnPlay = false;
            reactionLayers[i].Weight = 0f;
        }
        dazedLayer = animancer.Layers[10];
        dazedLayer.Mask = runtimeMasks[0];
        dazedLayer.SetLayerWeightOnPlay = false;
        dazedLayer.Weight = 0f;
    }

    private void OnDestroy()
    {
        foreach (AvatarMask mask in runtimeMasks)
            if (mask != null) Destroy(mask);
    }

    /// <summary>
    /// A glove reached him — called by <see cref="BoxerHealth"/> after the zone damage is applied,
    /// so this only decides how the body SHOWS it.
    /// </summary>
    public void ReceiveImpact(Vector3 point, Vector3 direction, float power, bool blocked, bool headHit)
    {
        if (Health != null && Health.IsDown) return;
        if (physics == null) physics = GetComponent<AimStudyPhysics>();
        if (physics != null) physics.Impact(point, direction, power, headHit);

        float now = Time.time;
        if (!blocked && power > 0.15f)
        {
            boxer.CancelAttacks();
            stunUntil = Mathf.Max(stunUntil, now + Mathf.Lerp(0.12f, 0.38f, power));
            float strength = Mathf.Lerp(0.35f, 1f, power);
            PlayPool(headHit ? HeadReactions : BodyReactions, headHit ? 0 : 1,
                0.55f * strength, 0.75f * strength);
            if (boxer.IsPlayerControlled && boxer.FeedbackCamera != null)
                boxer.FeedbackCamera.Shake(boxer.ContactShake * Mathf.Clamp01(power) * 0.5f);
        }
        else
        {
            PlayPool(BlockReactions, 2, 0.2f, 0.3f);
        }
    }

    /// <summary>Cycles each pool so the same flinch never repeats back-to-back.</summary>
    private void PlayPool(AnimationClip[] pool, int slot, float torsoWeight, float armWeight)
    {
        AnimationClip clip = null;
        if (pool != null && pool.Length > 0)
        {
            int start = rotation[slot] == 0 ? Random.Range(0, pool.Length) : rotation[slot];
            for (int i = 0; i < pool.Length; i++)
            {
                AnimationClip candidate = pool[(start + i) % pool.Length];
                if (candidate != null) { clip = candidate; break; }
            }
            rotation[slot]++;
        }
        if (clip == null) return;

        float duration = Mathf.Clamp(clip.length, 0.3f, 0.85f);
        for (int i = 0; i < reactionLayers.Length; i++)
        {
            if (reactionLayers[i] == null) continue;
            AnimancerState state = reactionLayers[i].Play(clip, 0.08f);
            state.Speed = clip.length / duration;
            reactionLayers[i].StartFade(i == 0 ? torsoWeight : armWeight, 0.08f);
        }
        reactionEnd = Mathf.Max(reactionEnd, Time.time + duration);
    }

    private void Update()
    {
        float now = Time.time;
        bool down = Health != null && Health.IsDown;
        boxer.CombatLocked = down || now < stunUntil;
        boxer.MovementMultiplier = Health != null ? Mathf.Lerp(1f, 0.55f, Health.Concussion01) : 1f;

        if (now >= reactionEnd)
            foreach (AnimancerLayer layer in reactionLayers)
                if (layer != null && layer.TargetWeight > 0.001f) layer.StartFade(0f, 0.2f);

        float dazedWanted = DazedIdle != null && Health != null && Health.Concussion01 > 0.55f
            && !boxer.IsPunching && !boxer.IsPreparing && now >= reactionEnd && !down ? 0.3f : 0f;
        if (dazedLayer != null)
        {
            if (dazedWanted > 0f)
            {
                if (dazedState == null || dazedState.Clip != DazedIdle)
                {
                    dazedState = dazedLayer.Play(DazedIdle, 0.25f);
                    dazedState.Speed = 0.75f;
                }
                if (dazedState.NormalizedTime >= 1f) dazedState.NormalizedTime = 0f;
            }
            if (Mathf.Abs(dazedLayer.TargetWeight - dazedWanted) > 0.001f)
                dazedLayer.StartFade(dazedWanted, 0.25f);
        }
    }

    /// <summary>Down: hand the body to the ragdoll. Up: take it back. Never a scripted collapse.</summary>
    public void SetDown(bool down)
    {
        boxer.CancelAttacks();
        stunUntil = 0f;
        boxer.CombatLocked = down;
        foreach (AnimancerLayer layer in reactionLayers)
            if (layer != null) layer.StartFade(0f, 0.15f);
        if (dazedLayer != null) dazedLayer.StartFade(0f, 0.15f);
        if (physics == null) physics = GetComponent<AimStudyPhysics>();
        if (physics != null) physics.SetDown(down);
        else if (down)
            Debug.LogError("AimStudyCombat: knockdown needs the PuppetMaster bridge — run " +
                "Tools > Undisputed > Boxing > Setup Fight With Selected Aim Study Boxer.", this);
    }

    /// <summary>Next bout: fresh condition, fresh stamina, back on the start mark.</summary>
    public void ResetFighter()
    {
        bool moverWasEnabled = mover != null && mover.enabled;
        if (mover != null) mover.enabled = false;
        if (Health != null) Health.ResetFighter();
        boxer.ResetCombat();
        if (hasStart) transform.SetPositionAndRotation(startPosition, startRotation);
        if (physics == null) physics = GetComponent<AimStudyPhysics>();
        if (physics != null)
            physics.ResetPose(hasStart ? startPosition : transform.position,
                hasStart ? startRotation : transform.rotation);
        if (mover != null) mover.enabled = moverWasEnabled;
        if (legs != null && legs.LegsInitialized) legs.User_Teleport(transform.position);

        stunUntil = 0f;
        reactionEnd = 0f;
        boxer.CombatLocked = false;
        foreach (AnimancerLayer layer in reactionLayers)
        {
            if (layer == null) continue;
            foreach (AnimancerState state in layer) state.IsPlaying = false;
            layer.StartFade(0f, 0f);
            layer.Weight = 0f;
        }
        if (dazedLayer != null)
        {
            foreach (AnimancerState state in dazedLayer) state.IsPlaying = false;
            dazedLayer.StartFade(0f, 0f);
            dazedLayer.Weight = 0f;
            dazedState = null;
        }
    }

    private void OnGUI()
    {
        if (boxer == null || !boxer.IsPlayerControlled || Health == null) return;
        AimStudyBoxer opponent = boxer.Opponent;
        AimStudyCombat other = opponent != null ? opponent.GetComponent<AimStudyCombat>() : null;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };

        GUILayout.BeginArea(new Rect(10f, Screen.height - 86f, Screen.width - 20f, 76f), GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label($"YOU   health {Health.Health01 * 100f:0}%   stamina {boxer.Stamina01 * 100f:0}%",
            style, GUILayout.Width(Screen.width * 0.45f));
        if (other != null && other.Health != null)
        {
            AimStudyAI ai = opponent.GetComponent<AimStudyAI>();
            GUILayout.Label($"OPPONENT   health {other.Health.Health01 * 100f:0}%   " +
                $"stamina {opponent.Stamina01 * 100f:0}%" +
                (ai != null ? $"   [{ai.StateName}]" : ""), style);
        }
        GUILayout.EndHorizontal();

        string status = Health.IsDown ? (Health.IsOut ? "KNOCKOUT — you are counted out" : "knockdown — beating the count…")
            : other != null && other.Health != null && other.Health.IsDown
                ? (other.Health.IsOut ? "KNOCKOUT — opponent is out" : "opponent down — count running…")
                : null;
        GUILayout.BeginHorizontal();
        if (status != null) GUILayout.Label($"<b>{status}</b>", style);
        if (GUILayout.Button("Reset fight", GUILayout.Width(120f)))
        {
            ResetFighter();
            if (other != null) other.ResetFighter();
            AimStudyAI ai = opponent != null ? opponent.GetComponent<AimStudyAI>() : null;
            if (ai != null) ai.ResetDecision();
        }
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }
}
