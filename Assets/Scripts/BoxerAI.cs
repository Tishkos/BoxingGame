using UnityEngine;

/// <summary>
/// An opponent who fights you. It drives a completely ordinary <see cref="BoxerPunchController"/> +
/// <see cref="Controller"/> through <see cref="AIBoxerInput"/> — it holds a trigger, pushes a stick and lets go,
/// exactly like a human on a pad.
///
/// That is the whole design, and it matters: the AI gets NO privileged path. Its punches wind up through the
/// same pose chain, cost the same stamina, carry the same effective mass, whiff the same way when it misjudges
/// range, and are read by the same glove sensors. Anything you improve about punching improves the opponent
/// too, automatically, and it can never do something you cannot.
///
/// The behaviour is a small range-and-rhythm machine rather than a decision tree:
///
///     TOO FAR   → close, feint, look for the step-in
///     IN RANGE  → work: pick a shape and a height, wind it up, let it go, reset
///     TOO CLOSE → step off the line, pivot out
///     THREATENED→ guard up, or slip off the line if it reads the punch early enough
///     HURT      → cover up and back away until the head clears
///
/// Its punch selection deliberately spreads across the whole authored pose library — shape on the stick's X,
/// height on its Y — so an opponent shows you jabs, crosses, hooks upstairs and down, and uppercuts inside,
/// instead of the same one over and over.
/// </summary>
[RequireComponent(typeof(BoxerPunchController))]
[RequireComponent(typeof(Controller))]
[DefaultExecutionOrder(-20)]   // decide before the controllers read the input this frame
public class BoxerAI : MonoBehaviour
{
    private enum Intent { Close, Work, BreakOff, Cover, Down }

    [Header("Who it is fighting")]
    [Tooltip("Left empty, it finds the player-controlled boxer in the scene.")]
    [SerializeField] private Transform opponent;

    [Header("Skill (0 = novice, 1 = sharp)")]
    [Tooltip("Raises punch rate and accuracy, shortens reactions, and makes it read your punches sooner.")]
    [Range(0f, 1f)] [SerializeField] private float skill = 0.5f;

    [Tooltip("How willing it is to be in there. High = walks you down and trades; low = pot-shots and resets.")]
    [Range(0f, 1f)] [SerializeField] private float aggression = 0.55f;

    [Header("Range (metres, measured flat)")]
    [Tooltip("Where it wants to stand — its punches land from about here.")]
    [Min(0.2f)] [SerializeField] private float preferredRange = 1.15f;
    [Tooltip("Closer than this and it steps off rather than staying jammed up.")]
    [Min(0.1f)] [SerializeField] private float tooClose = 0.75f;
    [Tooltip("Further than this and it stops throwing and closes instead.")]
    [Min(0.3f)] [SerializeField] private float reach = 1.45f;

    [Header("Rhythm")]
    [Tooltip("Seconds between punches at skill 0 … at skill 1.")]
    [SerializeField] private Vector2 punchInterval = new Vector2(1.5f, 0.5f);
    [Tooltip("How long a punch is held before release — this is the wind-up you can read and counter.")]
    [SerializeField] private Vector2 holdTime = new Vector2(0.32f, 0.14f);
    [Tooltip("Chance a punch is followed straight up by another (a combination).")]
    [Range(0f, 1f)] [SerializeField] private float comboChance = 0.45f;

    [Header("Defence")]
    [Tooltip("Chance it reacts at all when it sees a punch coming.")]
    [Range(0f, 1f)] [SerializeField] private float guardChance = 0.7f;
    [Tooltip("Of the reactions, how many are slips off the line rather than a block.")]
    [Range(0f, 1f)] [SerializeField] private float slipShare = 0.35f;
    [Tooltip("Seconds it keeps the guard up after the threat passes.")]
    [Min(0f)] [SerializeField] private float guardHold = 0.45f;
    [Tooltip("Circling: how much it moves laterally instead of straight in and out.")]
    [Range(0f, 1f)] [SerializeField] private float circling = 0.5f;

    [Header("Debug")]
    [SerializeField] private bool showState = false;

    // ------------------------------------------------------------------ State

    public string StateName { get; private set; } = "-";
    public float Range { get; private set; }

    private readonly AIBoxerInput input = new AIBoxerInput();
    private BoxerPunchController boxer;
    private Controller locomotion;
    private BoxerHealth health;
    private BoxerPunchController target;
    private BoxerHealth targetHealth;

    private Intent intent = Intent.Close;
    private float nextPunchAt;
    private float releaseAt = -1f;
    private int activeHand = -1;
    private Vector2 punchStick;
    private float guardUntil = -1f;
    private float slipUntil = -1f;
    private Vector2 slipDirection;
    private int circleDirection = 1;
    private float nextCircleFlip;
    private float lastSeenThreat = -10f;

    // ------------------------------------------------------------------ Setup

    /// <summary>Set up by <see cref="OpponentSpawner"/> before the fighter is switched on.</summary>
    public void Configure(Transform fightThis, float skillLevel, float aggressionLevel)
    {
        opponent = fightThis;
        skill = Mathf.Clamp01(skillLevel);
        aggression = Mathf.Clamp01(aggressionLevel);
    }

    private void Awake()
    {
        boxer = GetComponent<BoxerPunchController>();
        locomotion = GetComponent<Controller>();
        health = GetComponent<BoxerHealth>();

        // The ranges are authored at 1x and this component is only ever ADDED AT RUNTIME to a spawned clone,
        // so scaling here can never double-apply. On the 2x character, 1.15 m of "preferred range" would be
        // half a punch short — the AI would crowd you chest to chest.
        float scale = Mathf.Max(0.5f, transform.lossyScale.y);
        preferredRange *= scale;
        tooClose *= scale;
        reach *= scale;

        // Take the wheel. From here the controllers never see the player's pad.
        boxer.SetInput(input);
        locomotion.SetInput(input);
    }

    private void Start()
    {
        AcquireTarget();
        nextPunchAt = Time.time + Random.Range(0.4f, 1.2f);
        nextCircleFlip = Time.time + Random.Range(1.5f, 3.5f);
    }

    private void AcquireTarget()
    {
        if (opponent == null)
        {
            foreach (BoxerPunchController b in FindObjectsByType<BoxerPunchController>(FindObjectsSortMode.None))
                if (b != boxer && b.IsPlayerControlled) { opponent = b.transform; break; }
        }
        if (opponent == null) return;

        target = opponent.GetComponent<BoxerPunchController>();
        targetHealth = opponent.GetComponent<BoxerHealth>();
        locomotion.SetTarget(opponent);   // it squares up to the player, not to the bag
    }

    // ------------------------------------------------------------------ Think

    private void Update()
    {
        if (target == null) { AcquireTarget(); if (target == null) return; }

        float dt = Time.deltaTime;
        Vector3 flat = Vector3.ProjectOnPlane(opponent.position - transform.position, Vector3.up);
        Range = flat.magnitude;

        if (health != null && health.IsDown)
        {
            intent = Intent.Down;
            StateName = health.IsOut ? "OUT" : "down";
            input.Clear();
            input.Poll();
            return;
        }

        ChooseIntent();
        DriveFootwork(flat);
        DriveDefence();
        DrivePunching();
        input.Poll();
    }

    private void ChooseIntent()
    {
        // Hurt badly? Cover up and get out until the head clears — this is what makes finishing him feel earned.
        if (health != null && (health.Concussion01 > 0.65f || boxer.Stamina01 < 0.2f))
        {
            intent = Intent.Cover;
            StateName = "hurt";
            return;
        }

        if (Range > reach) { intent = Intent.Close; StateName = "closing"; }
        else if (Range < tooClose) { intent = Intent.BreakOff; StateName = "off the line"; }
        else { intent = Intent.Work; StateName = "working"; }
    }

    private void DriveFootwork(Vector3 flat)
    {
        if (Time.time >= nextCircleFlip)
        {
            circleDirection = -circleDirection;
            nextCircleFlip = Time.time + Random.Range(1.5f, 4f);
        }

        float towards;
        switch (intent)
        {
            case Intent.Close: towards = Mathf.Lerp(0.55f, 1f, aggression); break;
            case Intent.BreakOff: towards = -0.8f; break;
            case Intent.Cover: towards = -1f; break;
            default:
                // Hold the range with small corrections rather than marching in and out.
                towards = Mathf.Clamp((Range - preferredRange) * 1.8f, -0.6f, 0.6f);
                break;
        }

        float lateral = circleDirection * circling * (intent == Intent.Work ? 0.7f : 0.35f);
        // While it is winding a punch up it stops circling and plants — you cannot punch off a moving base.
        if (releaseAt > 0f) lateral *= 0.2f;

        input.Move = Vector2.ClampMagnitude(new Vector2(lateral, towards), 1f);
        input.Sprint = intent == Intent.Close && Range > reach * 1.6f;
    }

    /// <summary>
    /// Reading the punch. It watches for the player's wind-up (Charge / the hand being held) and reacts once,
    /// with a delay that shortens with skill — so a low-skill opponent guards late and eats it, and a sharp one
    /// beats you to it. It cannot see anything the player could not see.
    /// </summary>
    private void DriveDefence()
    {
        bool threat = target != null && !target.IsKnockedDown &&
                      (target.IsPunching || target.Charge(BoxerPunchController.Hand.Left) > 0.1f
                                         || target.Charge(BoxerPunchController.Hand.Right) > 0.1f);

        if (threat && Range < reach * 1.25f)
        {
            if (Time.time - lastSeenThreat > 0.6f)
            {
                lastSeenThreat = Time.time;
                if (Random.value < guardChance * Mathf.Lerp(0.55f, 1f, skill))
                {
                    float reaction = Mathf.Lerp(0.22f, 0.05f, skill);
                    if (Random.value < slipShare)
                    {
                        // Slip off the line: the punch controller turns lean into a real graze reduction.
                        slipUntil = Time.time + reaction + 0.35f;
                        slipDirection = new Vector2(Random.value < 0.5f ? -1f : 1f, Random.Range(-0.5f, 0.1f));
                    }
                    else
                    {
                        guardUntil = Time.time + reaction + guardHold;
                    }
                }
            }
        }

        bool covering = intent == Intent.Cover;
        input.Block = covering || Time.time < guardUntil;

        // Body guard against a low shot; high guard otherwise.
        input.BodyGuard = input.Block && target != null && target.AimPoint.y < transform.position.y + 1.1f;
        if (input.BodyGuard) input.Block = false;
    }

    private void DrivePunching()
    {
        // Mid-punch: hold the stick where the punch was chosen, then let go.
        if (releaseAt > 0f)
        {
            input.Body = punchStick;
            if (Time.time >= releaseAt)
            {
                input.SetPunch(activeHand, false);   // release = throw
                releaseAt = -1f;
                activeHand = -1;

                bool combo = Random.value < comboChance * Mathf.Lerp(0.5f, 1f, aggression);
                nextPunchAt = Time.time + (combo
                    ? Random.Range(0.12f, 0.3f)
                    : Mathf.Lerp(punchInterval.x, punchInterval.y, skill) * Random.Range(0.7f, 1.4f));
            }
            return;
        }

        // Not punching: the stick is free for slipping and leaning.
        input.Body = Time.time < slipUntil ? slipDirection : Vector2.Lerp(input.Body, Vector2.zero, 0.2f);

        if (intent != Intent.Work && intent != Intent.Close) return;
        if (Range > reach || Time.time < nextPunchAt) return;
        if (input.Block || input.BodyGuard) return;
        if (boxer.Stamina01 < 0.15f) return;
        if (targetHealth != null && targetHealth.IsDown) return;

        ThrowSomething();
    }

    /// <summary>
    /// Pick a punch and start winding it up. Shape comes from the stick's X and height from its Y — the same
    /// mapping the player has — so the opponent works through the whole authored library instead of drilling
    /// one pose: jabs and crosses straight, hooks out wide, uppercuts up the middle, at three heights each.
    /// </summary>
    private void ThrowSomething()
    {
        int hand = Random.value < 0.55f ? 0 : 1;          // slightly lead-hand biased, like a real fighter
        int side = hand == 1 ? 1 : -1;

        // Shape: mostly straights, hooks when it is close, uppercuts closer still.
        float roll = Random.value;
        float closeness = Mathf.InverseLerp(reach, tooClose, Range);   // 0 at the end of the reach, 1 jammed up
        float x;
        if (roll < 0.5f - 0.2f * closeness) x = Random.Range(-0.18f, 0.18f);                 // straight
        else if (roll < 0.8f) x = side * Random.Range(0.55f, 1f);                             // hook (outward)
        else x = -side * Random.Range(0.55f, 1f);                                             // uppercut (inward)

        // Height: head most of the time, body often enough to hurt, low occasionally.
        float h = Random.value;
        float y = h < 0.5f ? Random.Range(0.55f, 1f)          // head
                : h < 0.85f ? Random.Range(-0.2f, 0.2f)       // body
                : Random.Range(-1f, -0.5f);                   // low

        // A sharper opponent commits more cleanly; a novice's stick is sloppier, so its punches are too.
        float sloppiness = Mathf.Lerp(0.35f, 0.05f, skill);
        punchStick = new Vector2(Mathf.Clamp(x + Random.Range(-sloppiness, sloppiness), -1f, 1f),
                                 Mathf.Clamp(y + Random.Range(-sloppiness, sloppiness), -1f, 1f));

        activeHand = hand;
        input.Body = punchStick;
        input.SetPunch(hand, true);
        releaseAt = Time.time + Mathf.Lerp(holdTime.x, holdTime.y, skill) * Random.Range(0.8f, 1.3f);
    }

    private void OnGUI()
    {
        if (!showState || !Application.isPlaying) return;
        GUI.Label(new Rect(12f, Screen.height - 46f, 420f, 20f),
                  $"AI: {StateName}   range {Range:0.00}m" +
                  (health != null ? $"   hp {health.Health01:0.00}  chin {health.Concussion01:0.00}  down {health.Knockdowns}" : ""));
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.8f);
        DrawRing(preferredRange);
        Gizmos.color = new Color(1f, 0.2f, 0.1f, 0.5f);
        DrawRing(tooClose);
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.5f);
        DrawRing(reach);
    }

    private void DrawRing(float radius)
    {
        Vector3 prev = transform.position + new Vector3(radius, 0.05f, 0f);
        for (int i = 1; i <= 32; i++)
        {
            float a = i / 32f * Mathf.PI * 2f;
            Vector3 next = transform.position + new Vector3(Mathf.Cos(a) * radius, 0.05f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
