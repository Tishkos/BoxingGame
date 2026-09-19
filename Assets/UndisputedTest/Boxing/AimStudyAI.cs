using UnityEngine;

/// <summary>
/// An opponent for the aim-study boxer. Writes into an <see cref="AIBoxerInput"/> the same way a
/// human's pad writes into <see cref="BoxerInput"/> — it holds a trigger, pushes the body stick,
/// guards and releases, so the shared boxer applies the same preparation, cost, fatigue, buffer
/// and throw rules to it for free.
///
/// Timing comes from the skill slider rather than reflexes: a threat is only answered inside a
/// scheduled <see cref="FightReactionWindow"/>, so a fast AI still reads the punch a beat after
/// it starts instead of covering before the trigger is even held.
/// </summary>
[RequireComponent(typeof(AimStudyBoxer))]
[RequireComponent(typeof(AimStudyCombat))]
[DefaultExecutionOrder(-40)]
[DisallowMultipleComponent]
public class AimStudyAI : MonoBehaviour
{
    [Tooltip("How reliably and how quickly he reads a threat — covers and slips arrive sooner and more often.")]
    [Range(0f, 1f)] public float Skill = 0.5f;
    [Tooltip("How soon he works again after a shot. Low waits, high keeps punching.")]
    [Range(0f, 1f)] public float Aggression = 0.55f;

    public string StateName { get; private set; } = "idle";

    private readonly AIBoxerInput input = new AIBoxerInput();
    private AimStudyBoxer boxer;
    private AimStudyCombat combat;
    private float nextAction;
    private float releaseAt = -1f;
    private int hand = -1;
    private int lastHand = -1;
    private float nextCircleFlip;
    private int circle = 1;
    private int lastThreat = -1;
    private readonly FightReactionWindow reaction = new FightReactionWindow();
    private bool reactingWithSlip;
    private AimStudyBoxer.Defense reactiveGuard;
    private Vector2 slip;
    private int comboRemaining;
    private Boxing.PunchType chosenTechnique;
    private bool chosenBody;

    private void Awake()
    {
        boxer = GetComponent<AimStudyBoxer>();
        combat = GetComponent<AimStudyCombat>();
        boxer.SetInput(input);
    }

    private void OnEnable()
    {
        if (boxer != null) boxer.SetInput(input);
    }

    private void OnDisable()
    {
        input.Clear();
        if (boxer != null) boxer.CancelAttacks();
    }

    /// <summary>Between rounds: forget the read, the held hand and any pending exchange.</summary>
    public void ResetDecision()
    {
        input.Clear();
        hand = -1;
        releaseAt = -1f;
        lastThreat = -1;
        comboRemaining = 0;
        reaction.Clear();
        nextAction = Time.time + 0.8f;
        StateName = "reading";
    }

    private void Update()
    {
        float now = Time.time;
        if (Time.deltaTime <= 0f || !Application.isFocused)
        {
            input.Clear();
            boxer.CancelAttacks();
            reaction.Clear();
            return;
        }

        AimStudyBoxer target = boxer.Opponent;
        AimStudyCombat targetCombat = target != null ? target.GetComponent<AimStudyCombat>() : null;
        if (target == null || combat.Health == null || combat.Health.IsDown
            || targetCombat == null || targetCombat.Health == null || targetCombat.Health.IsDown)
        {
            input.Clear();
            boxer.CancelAttacks();
            hand = -1;
            releaseAt = -1f;
            StateName = "idle";
            return;
        }

        float scale = Mathf.Max(0.1f, transform.lossyScale.y);
        float range = Vector3.ProjectOnPlane(target.transform.position - transform.position, Vector3.up).magnitude;
        float reach = 1.25f * scale, preferred = 0.95f * scale, close = 0.65f * scale;
        bool hurt = boxer.Stamina01 < 0.2f || combat.Health.Concussion01 > 0.65f;

        // Timers survive the per-frame input wipe — only the held buttons and sticks are rewritten.
        input.Clear();
        if (now >= nextCircleFlip)
        {
            nextCircleFlip = now + Random.Range(2f, 4f);
            circle = -circle;
        }
        Vector2 move = new Vector2(circle * 0.25f,
            hurt ? -0.65f : Mathf.Clamp((range - preferred) * 1.8f, -0.65f, 0.8f));
        if (range > reach * 1.5f) move.x = 0f;
        if (hand >= 0) move.x *= 0.2f;
        input.Move = Vector2.ClampMagnitude(move, 1f);

        // A NEW committed attack inside reach is the threat — re-reading the same shot must not
        // roll the dice twice.
        if ((target.IsPreparing || target.IsPunching) && target.AttackSequence != lastThreat
            && range <= reach * 1.2f)
        {
            lastThreat = target.AttackSequence;
            if (Random.value < Mathf.Lerp(0.4f, 0.8f, Skill))
            {
                reaction.Schedule(now, Mathf.Lerp(0.32f, 0.18f, Skill), 0.45f);
                reactingWithSlip = Random.value < 0.3f;
                if (reactingWithSlip)
                    slip = new Vector2(Random.value < 0.5f ? -0.8f : 0.8f, -0.2f);
                else
                    reactiveGuard = target.AttackTarget == Boxing.PunchTarget.Body
                        ? AimStudyBoxer.Defense.Body : AimStudyBoxer.Defense.Head;
            }
        }

        bool defending = reaction.Active(now);
        if (defending)
        {
            if (reactingWithSlip) input.Body = slip;
            else if (reactiveGuard == AimStudyBoxer.Defense.Head) input.Block = true;
            else input.BodyGuard = true;
        }
        else if (hurt)
        {
            // Shelling up covers the head; without a live read he cannot know where it lands.
            defending = true;
            input.Block = true;
        }

        if (defending)
        {
            if (hand >= 0)
            {
                boxer.CancelAttacks();
                hand = -1;
                releaseAt = -1f;
            }
            nextAction = Mathf.Max(nextAction, now + 0.35f);
            StateName = "covering";
            return;
        }

        if (hand >= 0)
        {
            input.SetPunch(hand, true);
            input.Body = new Vector2(0f, chosenTechnique == Boxing.PunchType.Hook ? 1f
                : chosenTechnique == Boxing.PunchType.Uppercut ? -1f : 0f);
            boxer.InputBodyShot = chosenBody;
            StateName = "preparing";
            if (now >= releaseAt)
            {
                input.SetPunch(hand, false);
                hand = -1;
                releaseAt = -1f;
                nextAction = now + (comboRemaining > 0 ? Random.Range(0.16f, 0.28f)
                    : Random.Range(0.7f, 1.2f) * Mathf.Lerp(1.25f, 0.75f, Aggression));
                if (comboRemaining > 0) comboRemaining--;
            }
            return;
        }

        if (range > reach)
        {
            StateName = "closing";
            return;
        }
        if (range < close || now < nextAction || boxer.Stamina01 <= 0.22f)
        {
            StateName = "reading";
            return;
        }

        hand = lastHand >= 0 && Random.value < 0.75f ? 1 - lastHand : Random.Range(0, 2);
        float roll = Random.value;
        Boxing.PunchType technique = range > 0.95f * scale
            ? (roll < 0.55f ? Boxing.PunchType.Jab : roll < 0.85f ? Boxing.PunchType.Hook : Boxing.PunchType.Uppercut)
            : (roll < 0.35f ? Boxing.PunchType.Jab : roll < 0.8f ? Boxing.PunchType.Hook : Boxing.PunchType.Uppercut);
        if (!boxer.HasMotion(hand, technique, Boxing.PunchTarget.Head)
            && !boxer.HasMotion(hand, technique, Boxing.PunchTarget.Body))
            technique = Boxing.PunchType.Jab;
        chosenTechnique = technique;
        chosenBody = Random.value < 0.35f && boxer.HasMotion(hand, technique, Boxing.PunchTarget.Body);
        if (comboRemaining <= 0 && Random.value < 0.4f) comboRemaining = Random.Range(1, 3);
        releaseAt = now + (Random.value < 0.2f && boxer.Stamina01 > 0.5f
            ? Random.Range(0.35f, 0.55f) : Random.Range(0.1f, 0.22f));
        lastHand = hand;

        // Held now so the boxer's Update sees the press edge with the stick already aimed.
        input.SetPunch(hand, true);
        input.Body = new Vector2(0f, technique == Boxing.PunchType.Hook ? 1f
            : technique == Boxing.PunchType.Uppercut ? -1f : 0f);
        boxer.InputBodyShot = chosenBody;
        StateName = "working";
    }
}
