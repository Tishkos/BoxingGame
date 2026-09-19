using UnityEngine;

/// <summary>
/// The bag's voice — driven by the physics, never by input, and never the same twice.
///
/// Every landed punch picks its sound from the impulse the bag actually absorbed (<see cref="PunchingBag.OnHit"/>):
///
///   LIGHT (jab, glancing)  →  a SOFT take: the recording low-passed until only the contact is left, quiet,
///                             pitched a touch up.
///   CLEAN MID              →  a fresh take from the shuffle bag of originals + minted pitch variants.
///   HEAVY (cross, overdriven) →  the take PLUS a BODY layer underneath — the same recording pitched far down
///                             and filtered to a chest thump. That layer is what makes big sound BIG.
///   PERFECT (clean + hard) →  a faint high snap on top: the crack of a shot right through the middle.
///
/// Pitch always leans with weight (heavier = deeper), every play adds live jitter, and the shuffle bag deals
/// clips like cards so nothing repeats. Sounds play in 3D at the actual contact point through a voice pool, so
/// a double-hand flurry never cuts its own tails.
///
/// Extra: the chain CREAKS. When the bag is swinging hard, a heavily slowed, filtered squeak take plays up at
/// the mount every second or so — the rig complaining about the shot it just took.
/// Added and wired by Tools ▸ Boxer ▸ Rebuild Boxing System.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(PunchingBag))]
public class BagAudio : MonoBehaviour
{
    [Header("Source recordings (variants are minted from these at load)")]
    [Tooltip("Bag hit recordings — Assets/Sounds/Bag. The setup tool fills this.")]
    [SerializeField] private AudioClip[] hitClips;

    [Tooltip("Shoe squeak recordings — pitched far down they become the chain/strap creak. Optional.")]
    [SerializeField] private AudioClip[] squeakClips;

    [Header("Levels")]
    [Range(0f, 1f)] [SerializeField] private float masterVolume = 0.9f;
    [Tooltip("Volume of the softest hit that still makes a sound.")]
    [Range(0f, 1f)] [SerializeField] private float lightVolume = 0.32f;
    [Tooltip("How loud the body-thump layer gets under a full-strength hit.")]
    [Range(0f, 1f)] [SerializeField] private float bodyLayerVolume = 0.8f;

    [Header("What strength sounds like")]
    [Tooltip("Below this strength the SOFT bank plays (glancing contact, jabs at range).")]
    [Range(0f, 1f)] [SerializeField] private float softBelow = 0.35f;
    [Tooltip("Above this strength the body-thump layer fades in underneath.")]
    [Range(0f, 1f)] [SerializeField] private float bodyAbove = 0.5f;
    [Tooltip("Heavier punches play deeper: pitch at strength 1 (light hits sit slightly above 1).")]
    [Range(0.7f, 1f)] [SerializeField] private float heavyPitch = 0.93f;
    [Tooltip("Live pitch jitter on every play (±).")]
    [Range(0f, 0.12f)] [SerializeField] private float pitchJitter = 0.035f;

    [Header("Chain creak")]
    [Tooltip("The rig complains when the bag swings hard — a slowed, filtered squeak up at the mount.")]
    [SerializeField] private bool chainCreak = true;
    [Tooltip("Bag speed (m/s) above which the chain may creak.")]
    [Min(0f)] [SerializeField] private float creakSpeed = 1.2f;
    [Range(0f, 1f)] [SerializeField] private float creakVolume = 0.22f;

    private PunchingBag bag;
    private AudioVariants.Bank primary;
    private AudioVariants.Bank soft;
    private AudioVariants.Bank body;
    private AudioVariants.Bank creaks;
    private AudioVariants.VoicePool pool;
    private float nextHitSound;
    private float nextCreak;

    private void Awake()
    {
        bag = GetComponent<PunchingBag>();

        // The kitchen: originals + minted variants. Twelve recordings become a library.
        primary = AudioVariants.BuildPrimary(hitClips, 2.5f, -2.5f);
        soft = AudioVariants.BuildSoft(hitClips, 2100f);
        body = AudioVariants.BuildBody(hitClips, 0.62f, 750f, 1.1f);
        creaks = AudioVariants.BuildBody(squeakClips, 0.45f, 520f, 1.5f);

        float scale = Mathf.Max(0.5f, transform.lossyScale.y);
        pool = new AudioVariants.VoicePool(transform, 6, 1.4f * scale, 30f * scale);

        if (primary.IsEmpty)
            Debug.LogWarning("BagAudio: no hit clips assigned — run Tools ▸ Boxer ▸ Rebuild Boxing System to " +
                             "wire Assets/Sounds/Bag in.", this);
    }

    private void OnEnable()
    {
        if (bag != null) bag.OnHit.AddListener(OnHit);
    }

    private void OnDisable()
    {
        if (bag != null) bag.OnHit.RemoveListener(OnHit);
    }

    // ---------------------------------------------------------------- The hit

    private void OnHit(PunchingBag source, PunchingBag.HitInfo info)
    {
        if (primary.IsEmpty || Time.unscaledTime < nextHitSound) return;
        nextHitSound = Time.unscaledTime + 0.03f;   // two fists in one physics step = one sound, not a flam

        float strength = Mathf.Clamp01(info.strength);
        float clean = Mathf.Clamp01(info.cleanliness);

        // Weight → depth. A jab sits a touch bright; a cross drops the whole take down.
        float pitch = Mathf.Lerp(1.05f, heavyPitch, strength) * Jitter();
        float volume = Mathf.Lerp(lightVolume, 1f, Mathf.Pow(strength, 0.8f)) * Mathf.Lerp(0.8f, 1f, clean) * masterVolume;

        AudioClip take = strength < softBelow && !soft.IsEmpty ? soft.Next() : primary.Next();
        pool.Play(take, info.point, volume, pitch);

        // The chest layer: only real punches earn it, and it grows with what the bag actually absorbed.
        if (strength > bodyAbove && !body.IsEmpty)
        {
            float depth = Mathf.InverseLerp(bodyAbove, 1f, strength);
            pool.Play(body.Next(), info.point, Mathf.Lerp(0.25f, bodyLayerVolume, depth) * masterVolume, Jitter(0.05f));
        }

        // The crack of a perfect shot: a faint, fast, high snap over the top.
        if (clean > 0.85f && strength > 0.6f)
            pool.Play(primary.Next(), info.point, 0.22f * masterVolume, 1.3f * Jitter());

        // The COUNTER crack: meeting the bag's incoming swing rings sharper — timing is audible.
        if (info.closing > 1.2f && strength > 0.35f)
            pool.Play(primary.Next(), info.point, 0.2f * masterVolume, 1.35f * Jitter());
    }

    // ---------------------------------------------------------------- Technique voices

    /// <summary>A signature technique landed: its own voice on top of the normal hit (called by ImpactFeedback).</summary>
    public void Accent(string flavor, PunchingBag.HitInfo info)
    {
        if (string.IsNullOrEmpty(flavor) || primary.IsEmpty) return;
        switch (flavor)
        {
            case "overhand":       // the loop lands DEEP
                if (!body.IsEmpty) pool.Play(body.Next(), info.point, 0.5f * masterVolume, 0.55f * Jitter());
                break;
            case "shovel":         // dig-dig
                StartCoroutine(DigDig(info.point));
                break;
            case "stiff jab":      // the whip-crack
                pool.Play(primary.Next(), info.point, 0.2f * masterVolume, 1.4f * Jitter());
                break;
            case "counter":        // the riposte: bright crack with the body forced on underneath
                pool.Play(primary.Next(), info.point, 0.35f * masterVolume, 1.1f * Jitter());
                if (!body.IsEmpty) pool.Play(body.Next(), info.point, 0.4f * masterVolume, Jitter(0.05f));
                break;
            case "double":         // the second of the pair, clipped a touch brighter
                pool.Play(primary.Next(), info.point, 0.18f * masterVolume, 1.2f * Jitter());
                break;
        }
    }

    private System.Collections.IEnumerator DigDig(Vector3 point)
    {
        AudioVariants.Bank bank = soft.IsEmpty ? primary : soft;
        pool.Play(bank.Next(), point, 0.3f * masterVolume, 1.05f * Jitter());
        yield return new WaitForSeconds(0.06f);
        pool.Play(bank.Next(), point, 0.26f * masterVolume, 0.98f * Jitter());
    }

    /// <summary>The PURE hit: a sub-bass thump in near-silence, then a bright answer (called by ImpactFeedback).</summary>
    public void PureHit(PunchingBag.HitInfo info)
    {
        if (primary.IsEmpty) return;
        AudioVariants.Bank low = body.IsEmpty ? primary : body;
        pool.Play(low.Next(), info.point, 1f * masterVolume, 0.5f);
        StartCoroutine(PureTail(info.point));
    }

    private System.Collections.IEnumerator PureTail(Vector3 point)
    {
        yield return new WaitForSecondsRealtime(0.12f);
        if (!primary.IsEmpty) pool.Play(primary.Next(), point, 0.35f * masterVolume, 1.4f);
    }

    // ---------------------------------------------------------------- The creak

    private void Update()
    {
        if (!chainCreak || creaks.IsEmpty || bag == null || !bag.IsBuilt) return;
        if (Time.time < nextCreak) return;

        float speed = bag.Velocity.magnitude;
        if (speed < creakSpeed) return;

        Vector3 top = bag.BagCollider != null
            ? new Vector3(bag.BagCollider.bounds.center.x, bag.BagCollider.bounds.max.y + 0.15f, bag.BagCollider.bounds.center.z)
            : transform.position + Vector3.up;

        float loud = Mathf.InverseLerp(creakSpeed, creakSpeed * 3f, speed);
        pool.Play(creaks.Next(), top, Mathf.Lerp(0.4f, 1f, loud) * creakVolume * masterVolume, Jitter(0.1f));
        nextCreak = Time.time + Random.Range(0.7f, 1.7f);
    }

    private float Jitter(float amount = -1f)
    {
        float j = amount > 0f ? amount : pitchJitter;
        return 1f + Random.Range(-j, j);
    }
}
