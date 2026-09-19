using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Contact fireworks, no art assets needed — every sprite, ring and material is generated in code.
///
/// A landed punch is FIVE layers, each doing one job, all scaled by the impulse the bag actually absorbed:
///
///   FLASH   — one bright additive kernel for a couple of frames: the eye reads it as force.
///   SPARKS  — stretched streaks exploding off the contact along the reflection of the punch: the snap.
///   DUST    — a soft warm plume that drifts up and hangs: the air the bag just moved.
///   FLECKS  — tiny dark leather bits with real gravity: the bag itself shedding under the blow.
///   RING    — an expanding shockwave quad ORIENTED TO THE PUNCH (not screen-facing), so the wave visibly
///             travels along the line of the shot from any camera angle.
///
/// On top of that: a chalk puff shaken off the TOP of the bag on heavy hits (the classic gym shot), speed-gated
/// glove trails that heat up as a held punch charges and flare white for the strike itself, faint whiff streaks
/// when a punch finds only air, and a double-gold ceremony on a perfect hit.
/// Add next to <see cref="BoxerPunchController"/> on the boxer; it finds the bags and events itself.
/// </summary>
[DisallowMultipleComponent]
public class BoxerVFX : MonoBehaviour
{
    [Header("Impact burst")]
    [Tooltip("Dust billboards on a full-strength hit (weak hits get about a third).")]
    [Range(0, 40)] [SerializeField] private int dustCount = 14;
    [SerializeField] private Color dustColor = new Color(0.9f, 0.86f, 0.8f, 0.5f);

    [Tooltip("Spark streaks on a full-strength hit.")]
    [Range(0, 30)] [SerializeField] private int sparkCount = 10;
    [SerializeField] private Color sparkColor = new Color(1f, 0.96f, 0.82f, 0.9f);

    [Tooltip("Leather flecks knocked off the bag on a full-strength hit.")]
    [Range(0, 20)] [SerializeField] private int fleckCount = 7;
    [SerializeField] private Color fleckColor = new Color(0.32f, 0.2f, 0.16f, 0.9f);

    [Tooltip("Expanding shockwave ring at the contact point, oriented along the punch. Perfect hits get a double ring.")]
    [SerializeField] private bool shockRing = true;
    [SerializeField] private Color ringColor = new Color(1f, 0.95f, 0.8f, 0.8f);

    [Tooltip("Strength above which the top of the bag sheds a chalk puff (the classic gym shot).")]
    [Range(0f, 1f)] [SerializeField] private float chalkThreshold = 0.55f;

    [Header("Glove trails")]
    [SerializeField] private bool gloveTrails = true;
    [Tooltip("Hand speed (m/s) above which the trail shows — only real punches leave streaks.")]
    [Range(1f, 10f)] [SerializeField] private float trailSpeed = 4f;
    [SerializeField] private Color trailColor = new Color(1f, 1f, 1f, 0.45f);
    [Tooltip("The trail flares toward this over the strike itself.")]
    [SerializeField] private Color strikeColor = new Color(1f, 1f, 1f, 0.85f);

    [Header("Charge telegraph")]
    [Tooltip("The trail heats toward this colour as a held punch charges, and the glove sheds tiny embers at high charge.")]
    [SerializeField] private Color chargedColor = new Color(1f, 0.6f, 0.15f, 0.7f);

    [Header("Whiffs")]
    [Tooltip("Faint air streaks when a punch misses — the swing reads even when nothing was there to hit.")]
    [SerializeField] private bool whiffStreaks = true;

    private BoxerPunchController controller;
    private ImpactFeedback feedback;
    private ParticleSystem dust;
    private ParticleSystem flash;
    private ParticleSystem sparks;
    private ParticleSystem flecks;
    private ParticleSystem rings;
    private Material spriteMaterial;
    private Material additiveMaterial;
    private Material ringMaterial;
    private Texture2D softTexture;
    private Texture2D ringTexture;
    private Mesh ringQuad;
    private readonly HashSet<PunchingBag> hookedBags = new HashSet<PunchingBag>();
    private float nextScan;

    private readonly Transform[] hands = new Transform[2];
    private readonly TrailRenderer[] trails = new TrailRenderer[2];
    private readonly Vector3[] lastHandPos = new Vector3[2];
    private readonly Vector3[] handVelocity = new Vector3[2];
    private readonly float[] chargeAccumulator = new float[2];
    private bool hasLastHandPos;

    private void Awake()
    {
        controller = GetComponent<BoxerPunchController>();
        feedback = GetComponent<ImpactFeedback>();

        dust = MakeSystem("Impact Dust", out ParticleSystemRenderer dustRenderer, 0.15f);
        dustRenderer.material = SpriteMaterial();

        flash = MakeSystem("Impact Flash", out ParticleSystemRenderer flashRenderer, 0f);
        flashRenderer.material = AdditiveMaterial();

        sparks = MakeSystem("Impact Sparks", out ParticleSystemRenderer sparkRenderer, 0.05f);
        sparkRenderer.material = AdditiveMaterial();
        sparkRenderer.renderMode = ParticleSystemRenderMode.Stretch;
        sparkRenderer.lengthScale = 0f;
        sparkRenderer.velocityScale = 0.055f;   // streaks stretch with their speed — fast sparks, long tails

        flecks = MakeSystem("Impact Flecks", out ParticleSystemRenderer fleckRenderer, 0.8f);
        fleckRenderer.material = SpriteMaterial();

        rings = MakeSystem("Impact Rings", out ParticleSystemRenderer ringRenderer, 0f);
        ringRenderer.material = RingMaterial();
        ringRenderer.renderMode = ParticleSystemRenderMode.Mesh;
        ringRenderer.mesh = RingQuad();
        ringRenderer.alignment = ParticleSystemRenderSpace.World;   // rotation3D orients each ring to its punch

        // Rings grow over their life and fade.
        ParticleSystem.SizeOverLifetimeModule size = rings.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.15f), new Keyframe(0.4f, 0.75f), new Keyframe(1f, 1f)));

        Animator animator = GetComponent<Animator>();
        if (gloveTrails && animator != null && animator.isHuman)
        {
            hands[0] = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            hands[1] = animator.GetBoneTransform(HumanBodyBones.RightHand);
            for (int i = 0; i < 2; i++) if (hands[i] != null) trails[i] = MakeTrail(hands[i]);
        }
    }

    private void OnEnable()
    {
        if (feedback != null) feedback.PerfectHit += OnPerfectHit;
        if (controller != null)
        {
            controller.PunchThrown += OnPunchThrown;
            controller.PunchMissed += OnPunchMissed;
        }
    }

    private void OnDisable()
    {
        if (feedback != null) feedback.PerfectHit -= OnPerfectHit;
        if (controller != null)
        {
            controller.PunchThrown -= OnPunchThrown;
            controller.PunchMissed -= OnPunchMissed;
        }
        foreach (PunchingBag bag in hookedBags) if (bag != null) bag.OnHit.RemoveListener(OnBagHit);
        hookedBags.Clear();
    }

    private void OnDestroy()
    {
        if (softTexture != null) Destroy(softTexture);
        if (ringTexture != null) Destroy(ringTexture);
        if (spriteMaterial != null) Destroy(spriteMaterial);
        if (additiveMaterial != null) Destroy(additiveMaterial);
        if (ringMaterial != null) Destroy(ringMaterial);
        if (ringQuad != null) Destroy(ringQuad);
    }

    private void Update()
    {
        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 1f;
            foreach (PunchingBag bag in FindObjectsByType<PunchingBag>())
                if (hookedBags.Add(bag)) bag.OnHit.AddListener(OnBagHit);
        }
    }

    private void LateUpdate()
    {
        if (!gloveTrails) return;
        float dt = Time.deltaTime;
        for (int i = 0; i < 2; i++)
        {
            if (trails[i] == null || hands[i] == null) continue;
            Vector3 position = hands[i].position;
            if (hasLastHandPos && dt > 0f)
                handVelocity[i] = Vector3.Lerp(handVelocity[i], (position - lastHandPos[i]) / dt, 0.5f);
            float speed = handVelocity[i].magnitude;
            trails[i].emitting = speed > trailSpeed;
            lastHandPos[i] = position;

            // The strike itself flares the trail white — one bright streak per punch, gone by the plant.
            float strike = controller != null ? Mathf.InverseLerp(0.35f, 0.9f, controller.TimelineX(i)) : 0f;
            float charge = controller != null ? controller.Charge((BoxerPunchController.Hand)i) : 0f;
            Color heat = Color.Lerp(Color.Lerp(trailColor, chargedColor, charge), strikeColor, strike);
            trails[i].startColor = heat;
            trails[i].endColor = new Color(heat.r, heat.g, heat.b, 0f);
            trails[i].widthMultiplier = 1f + 0.8f * strike;

            // Charge telegraph: a well-loaded glove sheds tiny embers while it waits.
            if (charge > 0.35f)
            {
                chargeAccumulator[i] += (3f + 9f * charge) * dt;
                while (chargeAccumulator[i] >= 1f)
                {
                    chargeAccumulator[i] -= 1f;
                    dust.Emit(new ParticleSystem.EmitParams
                    {
                        position = position + Random.insideUnitSphere * 0.05f,
                        velocity = Vector3.up * Random.Range(0.08f, 0.2f) + Random.insideUnitSphere * 0.06f,
                        startSize = Random.Range(0.01f, 0.022f),
                        startLifetime = Random.Range(0.25f, 0.45f),
                        startColor = chargedColor,
                    }, 1);
                }
            }
        }
        hasLastHandPos = true;
    }

    // ---------------------------------------------------------------- Reactions

    private void OnBagHit(PunchingBag bag, PunchingBag.HitInfo info)
    {
        Burst(info.point, info.direction, info.strength, false);

        // MET the swing: a punch into the bag's incoming pendulum rings COOL — the counter's colour.
        if (info.closing > 1.2f)
            EmitRing(info.point, info.direction, 0.5f + 0.4f * info.strength, 0.28f, new Color(0.7f, 0.85f, 1f, 0.8f));

        // An OVERHAND's ring comes down over the guard the way the punch did.
        if (controller != null && info.hand >= 0 && controller.LastFlavor((BoxerPunchController.Hand)info.hand) == "overhand")
        {
            Vector3 tiltAxis = Vector3.Cross(info.direction, Vector3.up);
            if (tiltAxis.sqrMagnitude > 0.001f)
                EmitRing(info.point, (Quaternion.AngleAxis(20f, tiltAxis.normalized) * info.direction).normalized,
                         0.45f, 0.26f, ringColor);
        }

        // Heavy hits shake chalk off the top of the bag — the whole rig felt that one.
        if (info.strength >= chalkThreshold && bag.BagCollider != null)
        {
            Bounds b = bag.BagCollider.bounds;
            Vector3 top = new Vector3(b.center.x, b.max.y - 0.03f, b.center.z);
            int puffs = Mathf.RoundToInt(Mathf.Lerp(3f, 8f, info.strength));
            for (int i = 0; i < puffs; i++)
            {
                dust.Emit(new ParticleSystem.EmitParams
                {
                    position = top + Random.insideUnitSphere * 0.08f,
                    velocity = Vector3.up * Random.Range(0.15f, 0.5f) + Random.insideUnitSphere * 0.2f,
                    startSize = Random.Range(0.04f, 0.09f),
                    startLifetime = Random.Range(0.5f, 0.9f),
                    startColor = new Color(dustColor.r, dustColor.g, dustColor.b, dustColor.a * 0.7f),
                }, 1);
            }
        }
    }

    private void OnPerfectHit(PunchingBag.HitInfo info)
    {
        Burst(info.point, info.direction, 1f, true);
    }

    private void OnPunchThrown(BoxerPunchController.Hand hand, BoxerPunchController.PunchType type, float overdrive)
    {
        // The release: one soft air-puff off the glove as it leaves — barely there, but the launch reads.
        int i = (int)hand;
        if (hands[i] == null) return;
        dust.Emit(new ParticleSystem.EmitParams
        {
            position = hands[i].position,
            velocity = Random.insideUnitSphere * 0.25f,
            startSize = Random.Range(0.05f, 0.09f) * (1f + 0.5f * overdrive),
            startLifetime = 0.3f,
            startColor = new Color(1f, 1f, 1f, 0.14f + 0.1f * overdrive),
        }, 1);
    }

    private void OnPunchMissed(BoxerPunchController.Hand hand)
    {
        if (!whiffStreaks) return;
        int i = (int)hand;
        if (hands[i] == null) return;
        Vector3 v = handVelocity[i];
        if (v.sqrMagnitude < 1f) v = transform.forward * 3f;
        for (int k = 0; k < 3; k++)
        {
            sparks.Emit(new ParticleSystem.EmitParams
            {
                position = hands[i].position + Random.insideUnitSphere * 0.05f,
                velocity = v * Random.Range(0.5f, 0.8f) + Random.insideUnitSphere * 0.3f,
                startSize = Random.Range(0.008f, 0.014f),
                startLifetime = Random.Range(0.1f, 0.18f),
                startColor = new Color(1f, 1f, 1f, 0.18f),
            }, 1);
        }
    }

    // ---------------------------------------------------------------- The burst

    private void Burst(Vector3 point, Vector3 direction, float strength, bool perfect)
    {
        // FLASH — one bright kernel for a couple of frames.
        flash.Emit(new ParticleSystem.EmitParams
        {
            position = point,
            velocity = Vector3.zero,
            startSize = 0.22f + 0.4f * strength,
            startLifetime = 0.07f,
            startColor = perfect ? new Color(1f, 0.9f, 0.55f, 1f) : new Color(1f, 0.97f, 0.85f, 0.95f),
        }, 1);

        // SPARKS — streaks bursting back off the surface, cheated outward around the punch line.
        Vector3 reflect = -direction;
        int sparkTotal = Mathf.RoundToInt(sparkCount * Mathf.Lerp(0.3f, 1f, strength)) + (perfect ? 6 : 0);
        for (int i = 0; i < sparkTotal; i++)
        {
            Vector3 spread = Vector3.Slerp(reflect, Random.onUnitSphere, 0.55f);
            if (Vector3.Dot(spread, direction) > 0.5f) spread = Vector3.Reflect(spread, direction); // never INTO the bag
            sparks.Emit(new ParticleSystem.EmitParams
            {
                position = point + Random.insideUnitSphere * 0.03f,
                velocity = spread * Random.Range(2.2f, 4.5f) * (0.5f + 0.7f * strength),
                startSize = Random.Range(0.01f, 0.02f),
                startLifetime = Random.Range(0.12f, 0.25f),
                startColor = perfect ? new Color(1f, 0.85f, 0.4f, 0.95f) : sparkColor,
            }, 1);
        }

        // DUST — the air the bag just moved, drifting up and hanging.
        int count = Mathf.RoundToInt(dustCount * Mathf.Lerp(0.35f, 1f, strength)) + (perfect ? 6 : 0);
        for (int i = 0; i < count; i++)
        {
            Vector3 velocity = -direction * Random.Range(0.3f, 1.2f) + Random.insideUnitSphere * (1.2f * strength + 0.4f);
            velocity.y = Mathf.Abs(velocity.y) * 0.7f + 0.15f;
            dust.Emit(new ParticleSystem.EmitParams
            {
                position = point + Random.insideUnitSphere * 0.05f,
                velocity = velocity,
                startSize = Random.Range(0.025f, 0.06f) * (1f + strength),
                startLifetime = Random.Range(0.3f, 0.65f),
                startColor = dustColor,
            }, 1);
        }

        // FLECKS — the bag shedding: small, dark, real gravity.
        int fleckTotal = Mathf.RoundToInt(fleckCount * strength);
        for (int i = 0; i < fleckTotal; i++)
        {
            Vector3 velocity = Vector3.Slerp(-direction, Random.onUnitSphere, 0.6f) * Random.Range(1f, 2.6f);
            flecks.Emit(new ParticleSystem.EmitParams
            {
                position = point + Random.insideUnitSphere * 0.04f,
                velocity = velocity,
                startSize = Random.Range(0.006f, 0.014f),
                startLifetime = Random.Range(0.4f, 0.8f),
                startColor = fleckColor,
            }, 1);
        }

        // RING — the shockwave, oriented along the punch so it travels with the shot from any angle.
        if (!shockRing) return;
        EmitRing(point, direction, 0.55f + 0.55f * strength, 0.3f, perfect ? new Color(1f, 0.85f, 0.45f, 0.85f) : ringColor);
        if (perfect) EmitRing(point, direction, 1.5f, 0.42f, new Color(1f, 0.8f, 0.35f, 0.7f));
    }

    private void EmitRing(Vector3 point, Vector3 direction, float size, float life, Color color)
    {
        Quaternion facing = Quaternion.LookRotation(direction.sqrMagnitude > 0.001f ? direction : transform.forward);
        rings.Emit(new ParticleSystem.EmitParams
        {
            position = point - direction * 0.02f,
            velocity = Vector3.zero,
            startSize = size,
            startLifetime = life,
            startColor = color,
            rotation3D = facing.eulerAngles,
        }, 1);
    }

    // ---------------------------------------------------------------- Builders

    private ParticleSystem MakeSystem(string name, out ParticleSystemRenderer psRenderer, float gravity)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed = 0f;
        main.gravityModifier = gravity;
        main.maxParticles = 400;
        main.playOnAwake = true;
        main.loop = true;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;

        ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
        col.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.55f, 0.4f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        ParticleSystem.LimitVelocityOverLifetimeModule limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.drag = 2.5f;

        psRenderer = go.GetComponent<ParticleSystemRenderer>();
        psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
        psRenderer.material = SpriteMaterial();
        psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return ps;
    }

    private TrailRenderer MakeTrail(Transform hand)
    {
        GameObject go = new GameObject("Glove Trail");
        go.transform.SetParent(hand, false);
        TrailRenderer trail = go.AddComponent<TrailRenderer>();
        trail.time = 0.18f;
        trail.minVertexDistance = 0.012f;
        trail.widthCurve = AnimationCurve.EaseInOut(0f, 0.055f, 1f, 0f);
        trail.material = SpriteMaterial();
        trail.startColor = trailColor;
        trail.endColor = new Color(trailColor.r, trailColor.g, trailColor.b, 0f);
        trail.emitting = false;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return trail;
    }

    private Material SpriteMaterial()
    {
        if (spriteMaterial != null) return spriteMaterial;
        if (softTexture == null) softTexture = MakeSoft(64);
        spriteMaterial = MakeUnlit(softTexture, false);
        return spriteMaterial;
    }

    private Material AdditiveMaterial()
    {
        if (additiveMaterial != null) return additiveMaterial;
        if (softTexture == null) softTexture = MakeSoft(64);
        additiveMaterial = MakeUnlit(softTexture, true);
        return additiveMaterial;
    }

    private Material RingMaterial()
    {
        if (ringMaterial != null) return ringMaterial;
        if (ringTexture == null) ringTexture = MakeRing(128);
        ringMaterial = MakeUnlit(ringTexture, true);
        return ringMaterial;
    }

    /// <summary>A unit quad in the XY plane facing +Z — the ring mesh, oriented per-particle via rotation3D.</summary>
    private Mesh RingQuad()
    {
        if (ringQuad != null) return ringQuad;
        ringQuad = new Mesh { name = "VFX Ring Quad (runtime)" };
        ringQuad.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
        };
        ringQuad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
        ringQuad.triangles = new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 };   // both windings: visible from either side
        ringQuad.RecalculateNormals();
        return ringQuad;
    }

    private static Material MakeUnlit(Texture2D texture, bool additive)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
        Material m = new Material(shader) { name = additive ? "Boxer VFX Additive (runtime)" : "Boxer VFX (runtime)" };
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
        else if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", texture);
        if (m.HasProperty("_Surface"))
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetInt("_SrcBlend", (int)(additive ? UnityEngine.Rendering.BlendMode.SrcAlpha : UnityEngine.Rendering.BlendMode.SrcAlpha));
            m.SetInt("_DstBlend", (int)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + (additive ? 1 : 0);
        }
        return m;
    }

    private static Texture2D MakeSoft(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "VFX Soft (runtime)" };
        var px = new Color32[size * size];
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                float a = Mathf.Clamp01(1f - d);
                a *= a;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        tex.SetPixels32(px); tex.Apply(true); tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    private static Texture2D MakeRing(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "VFX Ring (runtime)" };
        var px = new Color32[size * size];
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                float band = 1f - Mathf.Clamp01(Mathf.Abs(d - 0.8f) / 0.14f);   // thin annulus near the edge
                band *= band;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(band * 255f));
            }
        tex.SetPixels32(px); tex.Apply(true); tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }
}
