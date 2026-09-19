using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns physics into FEEL — the invisible 15% that makes a landed punch read as weight (req.md §46-§49).
///
/// The discipline is req.md §48's: "camera response should be extremely controlled". A jab barely registers; a
/// clean cross gets the full ceremony — the camera kicked ALONG the punch, a controlled shake, a lens punch-in,
/// a beat of hit-stop, a thump in the pad. Every response is scaled by the impulse the bag actually absorbed
/// (never by input), so the feedback is the physics, amplified.
///
///   landed punch   →  directional kick + shake + FOV + hit-stop + rumble, all by strength
///   PERFECT hit    →  (clean AND hard) gold flash on the bag, slow-motion follow-through, double-pulse rumble
///   bag hits YOU   →  heavy shake, shove-direction kick, dull rumble
///   whiff          →  a thin air-tick in the pad — a spacing mistake, not lag
///
/// Global time-stop only fires for genuinely heavy hits; light hits rely on the controller's per-hand freeze so
/// a flurry never stutters. Lives next to <see cref="BoxerPunchController"/>; finds the <see cref="CameraFollow"/> itself.
/// </summary>
[DisallowMultipleComponent]
public class ImpactFeedback : MonoBehaviour
{
    [Tooltip("Camera to shake. Defaults to the CameraFollow on Camera.main.")]
    [SerializeField] private CameraFollow cameraFollow;

    [Header("Camera (req.md §48: controlled, or it destroys the physical feel)")]
    [Tooltip("Shake at full strength. Weak hits are curved down hard — a jab is a tap, not an earthquake.")]
    [Range(0f, 1f)] [SerializeField] private float hitShake = 0.35f;
    [Range(0f, 1f)] [SerializeField] private float perfectShake = 0.6f;
    [Range(0f, 1f)] [SerializeField] private float blockShake = 0.3f;
    [Range(0f, 1f)] [SerializeField] private float bagHitsYouShake = 0.85f;

    [Tooltip("Camera pushed ALONG the punch on impact (metres at full strength) — the view leans into the hit.")]
    [Min(0f)]
    [SerializeField] private float kickMetres = 0.055f;

    [Tooltip("How hard weak hits are curved down: 1 = linear (everything shakes), 2 = squared (only heavy hits " +
             "register). 1.6 keeps taps quiet while an ordinary punch still moves the camera.")]
    [Range(1f, 3f)]
    [SerializeField] private float weakHitFalloff = 1.6f;

    [Tooltip("FOV punch-in at full strength (degrees).")]
    [Range(0f, 6f)]
    [SerializeField] private float fovPunch = 2.8f;

    [Header("Hit-stop")]
    [Tooltip("Freeze time for an instant when a HEAVY punch lands. Light hits use the controller's per-hand " +
             "freeze only, so flurries never stutter.")]
    [SerializeField] private bool hitFreeze = true;

    [Tooltip("Strength (0-1) a hit needs before the global freeze fires.")]
    [Range(0f, 1f)]
    [SerializeField] private float freezeThreshold = 0.45f;

    [Tooltip("Real seconds the freeze lasts on a full-strength hit.")]
    [Range(0f, 0.15f)]
    [SerializeField] private float freezeSeconds = 0.05f;

    [Tooltip("Time scale during the freeze. Not quite zero: a trace of motion reads better than a dead stop.")]
    [Range(0.01f, 0.5f)]
    [SerializeField] private float freezeScale = 0.08f;

    [Header("Perfect hits")]
    [Tooltip("Cleanliness (straight through the centre) needed for a perfect hit.")]
    [Range(0f, 1f)]
    [SerializeField] private float perfectCleanliness = 0.85f;

    [Tooltip("Strength (impulse vs the bag's full-strength impulse) needed for a perfect hit.")]
    [Range(0f, 1f)]
    [SerializeField] private float perfectStrength = 0.6f;

    [SerializeField] private bool slowMotion = true;

    [Range(0.05f, 1f)]
    [SerializeField] private float slowMoScale = 0.35f;

    [Tooltip("Real seconds the slow motion lasts.")]
    [Min(0f)]
    [SerializeField] private float slowMoSeconds = 0.09f;

    [Tooltip("Flash colour on the bag for a perfect hit.")]
    [SerializeField] private Color perfectFlash = new Color(1f, 0.85f, 0.45f);

    [Header("Ceremony governor (scarcity is what keeps the jackpots feeling like jackpots)")]
    [Tooltip("Minimum seconds between BIG ceremony channels (slow-mo, bag flash). Base per-hit shake/rumble/audio is never gated.")]
    [Min(0f)] [SerializeField] private float ceremonySpacing = 6f;

    [Header("MET — meeting the bag's swing")]
    [Tooltip("Bag closing speed (m/s) that counts as fully meeting the swing.")]
    [Min(0.1f)] [SerializeField] private float meetSpeed = 1.5f;
    [Range(0f, 1f)] [SerializeField] private float metCleanliness = 0.6f;
    [Tooltip("Cool counter flash on the bag when you meet its swing.")]
    [SerializeField] private Color metFlash = new Color(0.7f, 0.85f, 1f);

    [Header("PURE hit — the jackpot above perfect")]
    [Range(0.8f, 1f)] [SerializeField] private float pureCleanliness = 0.97f;
    [Range(0.5f, 1f)] [SerializeField] private float pureStrength = 0.9f;
    [Range(0f, 1f)] [SerializeField] private float pureOverdrive = 0.8f;
    [Tooltip("The bag must be nearly DEAD STILL (surface m/s) — you set it up, then you take it.")]
    [Min(0f)] [SerializeField] private float pureBagSpeed = 0.4f;
    [Tooltip("Minimum seconds between pure hits — once or twice a session, so it stays a story.")]
    [Min(0f)] [SerializeField] private float pureSpacing = 45f;

    /// <summary>Raised on a perfect hit (clean and hard). Hook up UI, VFX, score.</summary>
    public event Action<PunchingBag.HitInfo> PerfectHit;

    private BoxerPunchController boxer;
    private readonly HashSet<PunchingBag> hookedBags = new HashSet<PunchingBag>();
    private float nextScan;
    private float baseFixedDelta;
    private Coroutine warp;
    private float lastCeremonyTime = -100f;
    private float lastPureTime = -100f;

    private void Awake()
    {
        boxer = GetComponent<BoxerPunchController>();
        if (cameraFollow == null && Camera.main != null) cameraFollow = Camera.main.GetComponent<CameraFollow>();
    }

    private void OnEnable()
    {
        if (boxer != null)
        {
            boxer.PunchLanded += OnPunchLanded;
            boxer.BagContact += OnBagContact;
            boxer.PunchThrown += OnPunchThrown;
            boxer.PunchMissed += OnPunchMissed;
        }
    }

    private void OnDisable()
    {
        if (boxer != null)
        {
            boxer.PunchLanded -= OnPunchLanded;
            boxer.BagContact -= OnBagContact;
            boxer.PunchThrown -= OnPunchThrown;
            boxer.PunchMissed -= OnPunchMissed;
        }
        foreach (PunchingBag bag in hookedBags) if (bag != null) bag.OnHit.RemoveListener(OnBagHit);
        hookedBags.Clear();
        RestoreTime();
    }

    private void Update()
    {
        if (Time.time < nextScan) return;
        nextScan = Time.time + 1f;

        foreach (PunchingBag bag in FindObjectsByType<PunchingBag>())
        {
            if (hookedBags.Add(bag)) bag.OnHit.AddListener(OnBagHit);
        }
    }

    // ---------------------------------------------------------------- Reactions

    /// <summary>Weak hits are curved DOWN (response ∝ strength^falloff) so taps stay quiet — but not silent.</summary>
    private float Curve(float strength) => Mathf.Pow(Mathf.Clamp01(strength), weakHitFalloff);

    private void OnBagHit(PunchingBag bag, PunchingBag.HitInfo info)
    {
        float feel = Curve(info.strength);
        if (cameraFollow != null)
        {
            cameraFollow.Shake(hitShake * feel);
            cameraFollow.Kick(info.direction * (kickMetres * feel));
            cameraFollow.FovKick(fovPunch * feel);
        }
        // The impact frame: time stops for an instant with the glove planted in the bag — HEAVY hits only, and
        // never while the OTHER punch is still flying (a freeze then reads as the new punch stuttering out).
        // Gating on IsDriving was dead code: OnHit fires synchronously inside the landing test, while the
        // LANDING hand itself is still in Drive — so the impact frame never played at all.
        bool otherHandFlying = boxer != null && info.hand >= 0 && boxer.IsHandDriving(1 - info.hand);
        if (hitFreeze && freezeSeconds > 0f && info.strength >= freezeThreshold && !otherHandFlying)
            Warp(freezeScale, freezeSeconds * Mathf.Lerp(0.4f, 1f, info.strength), 1f, 0f);
    }

    private void OnPunchLanded(BoxerPunchController.Hand hand, PunchingBag bag, PunchingBag.HitInfo info)
    {
        // (The controller already thumps the pad for every landed punch — this layer adds texture + ceremony.)

        // SIGNATURE ACCENT: the technique that landed gets its own voice — texture, never governed.
        if (info.strength >= 0.3f && boxer != null) AccentFor(boxer.LastFlavor(hand), bag, info);

        // THE PURE HIT: dead-still bag, dead-centre, fully charged, on target — the jackpot above perfect.
        bool pureShape = info.cleanliness >= pureCleanliness && info.strength >= pureStrength
                         && info.overdrive >= pureOverdrive && info.surfaceSpeed < pureBagSpeed;
        if (pureShape && Time.unscaledTime - lastPureTime > pureSpacing && ClaimCeremony())
        {
            lastPureTime = Time.unscaledTime;
            PureCeremony(bag, info);
            PerfectHit?.Invoke(info);
            return;
        }

        bool perfect = info.cleanliness >= perfectCleanliness && info.strength >= perfectStrength;

        // MET: the punch met the bag swinging IN — the counter tier. Feedback only; the physics already paid it.
        float met01 = Mathf.Clamp01(info.closing / meetSpeed);
        if (!perfect && met01 > 0.5f && info.cleanliness >= metCleanliness)
        {
            if (cameraFollow != null) cameraFollow.Kick(info.direction * (kickMetres * 1.4f * met01));
            StartCoroutine(DoubleRumble(0.2f, 0.9f, 0.06f));
            if (bag != null && ClaimCeremony()) bag.Flash(metFlash * 1.5f, 0.1f);
            return;
        }
        if (!perfect) return;

        // PERFECT: flash, punchy lens, double-pulse rumble, light-bar strobe, slow-motion follow-through.
        // The big channels (flash, slow-mo) go through the governor so back-to-back perfects stay events.
        bool ceremony = ClaimCeremony();
        if (cameraFollow != null) { cameraFollow.Shake(perfectShake); cameraFollow.FovKick(4f); }
        if (bag != null && ceremony) bag.Flash(perfectFlash * 2.2f, 0.16f);
        if (boxer == null || boxer.IsPlayerControlled)
        {
            BoxerInput.Rumble(1f, 1f, 0.12f);
            BoxerInput.SetLightBar(Color.white);
        }
        if (ceremony && slowMotion && slowMoSeconds > 0f)
            Warp(freezeScale, hitFreeze ? freezeSeconds : 0f, slowMoScale, Mathf.Lerp(slowMoSeconds, slowMoSeconds * 2.5f, info.strength));
        PerfectHit?.Invoke(info);
    }

    /// <summary>Big ceremony channels share one clock: whoever claims it first wins, the rest stay modest.</summary>
    private bool ClaimCeremony()
    {
        if (Time.unscaledTime - lastCeremonyTime < ceremonySpacing) return false;
        lastCeremonyTime = Time.unscaledTime;
        return true;
    }

    /// <summary>The gym goes white-quiet for a heartbeat: flash, chain shudder, sub-bass answer, long slow-mo tail.</summary>
    private void PureCeremony(PunchingBag bag, PunchingBag.HitInfo info)
    {
        if (cameraFollow != null) { cameraFollow.Shake(perfectShake); cameraFollow.FovKick(5f); }
        if (bag != null)
        {
            bag.Flash(Color.white * 3f, 0.25f);
            bag.RattleChain(info.direction, 1f);
            BagAudio voice = bag.GetComponent<BagAudio>();
            if (voice != null) voice.PureHit(info);
        }
        if (boxer == null || boxer.IsPlayerControlled)
        {
            BoxerInput.SetLightBar(Color.white);
            StartCoroutine(DoubleRumble(1f, 1f, 0.12f));
        }
        Warp(freezeScale, hitFreeze ? freezeSeconds : 0f, 0.25f, 0.35f);
    }

    /// <summary>Per-technique haptics + audio accents. Small by design — texture, not ceremony.</summary>
    private void AccentFor(string flavor, PunchingBag bag, PunchingBag.HitInfo info)
    {
        if (string.IsNullOrEmpty(flavor)) return;
        bool player = boxer == null || boxer.IsPlayerControlled;
        switch (flavor)
        {
            case "overhand":
                if (player) BoxerInput.Rumble(0.8f, 0.2f, 0.18f);            // long and LOW — the loop landing
                break;
            case "stiff jab":
                if (player) BoxerInput.Rumble(0.1f, 0.9f, 0.04f);            // a whip-crack tick
                break;
            case "shovel":
                StartCoroutine(DoubleRumble(0.5f, 0.3f, 0.07f));             // dig-dig
                break;
            case "counter":
                if (cameraFollow != null) cameraFollow.Kick(info.direction * (kickMetres * 1.5f));
                StartCoroutine(DoubleRumble(0.4f, 0.9f, 0.06f));             // evade-tick… CRACK
                break;
        }
        if (bag != null)
        {
            BagAudio voice = bag.GetComponent<BagAudio>();
            if (voice != null) voice.Accent(flavor, info);
        }
    }

    private IEnumerator DoubleRumble(float low, float high, float gap)
    {
        if (boxer != null && !boxer.IsPlayerControlled) yield break;
        BoxerInput.Rumble(low, high, 0.05f);
        yield return new WaitForSecondsRealtime(gap);
        BoxerInput.Rumble(low, high, 0.05f);
    }

    private void OnBagContact(float strength, bool blocked)
    {
        if (cameraFollow != null) cameraFollow.Shake((blocked ? blockShake : bagHitsYouShake) * strength);
    }

    private void OnPunchThrown(BoxerPunchController.Hand hand, BoxerPunchController.PunchType type, float overdrive)
    {
        // A new punch always interrupts any freeze / slow-motion — nothing may stall a punch leaving the gate.
        if (warp != null) RestoreTime();
    }

    private void OnPunchMissed(BoxerPunchController.Hand hand)
    {
        // Whiff haptics live in the controller; a hook for whiff camera work goes here if it ever earns one.
    }

    // ---------------------------------------------------------------- Time warp

    /// <summary>Time warp in two steps: scale A for A seconds (real time), then scale B for B seconds. A new warp replaces a running one.</summary>
    private void Warp(float scaleA, float secondsA, float scaleB, float secondsB)
    {
        if (secondsA <= 0f && secondsB <= 0f) return;
        if (warp != null) StopCoroutine(warp);
        warp = StartCoroutine(WarpRoutine(scaleA, secondsA, scaleB, secondsB));
    }

    private IEnumerator WarpRoutine(float scaleA, float secondsA, float scaleB, float secondsB)
    {
        if (Time.timeScale >= 1f) baseFixedDelta = Time.fixedDeltaTime;   // the physics body may have changed it after Awake
        if (secondsA > 0f) { SetTimeScale(scaleA); yield return new WaitForSecondsRealtime(secondsA); }
        if (secondsB > 0f) { SetTimeScale(scaleB); yield return new WaitForSecondsRealtime(secondsB); }
        warp = null;
        RestoreTime();
    }

    private void SetTimeScale(float scale)
    {
        scale = Mathf.Clamp(scale, 0.01f, 1f);   // never a true zero: coroutines and physics must keep stepping
        Time.timeScale = scale;
        Time.fixedDeltaTime = baseFixedDelta * scale;
    }

    private void RestoreTime()
    {
        if (warp != null) { StopCoroutine(warp); warp = null; }
        Time.timeScale = 1f;
        if (baseFixedDelta > 0f) Time.fixedDeltaTime = baseFixedDelta;
    }
}
