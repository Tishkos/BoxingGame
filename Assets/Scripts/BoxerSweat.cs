using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Sweat for the boxer, no art assets needed. As the fight goes on the skin turns glossy (URP Lit smoothness),
/// darkens a touch, and grows tiny droplet bumps (a procedurally generated detail normal map). Stylized beads of
/// sweat appear ON the body — head, chest, spine, shoulders — glued to the skin, crawling slowly down and fading;
/// now and then a single drop falls from the chin. Nothing sprays or flies around, and the gloves stay dry.
///
/// Add next to the Animator on the boxer. It finds the renderers itself and skips gloves / shorts / hair by name;
/// sweat builds while you fight and dries off while you rest. Works with URP Lit materials — on stylized shaders
/// without _Smoothness only the darkening and beads apply.
/// </summary>
[DisallowMultipleComponent]
public class BoxerSweat : MonoBehaviour
{
    [Header("Body (assign the skin)")]
    [Tooltip("The body's skin mesh(es). When set, sweat wets ONLY these, and the beads are placed on the actual mesh " +
             "surface. Leave empty to auto-find renderers by name (Dry Name Parts) and place beads on rings around the bones.")]
    [SerializeField] private Renderer[] skinRenderers = new Renderer[0];

    [Header("Sweat level")]
    [Tooltip("Current sweat, 0 = dry, 1 = drenched. Runs itself in Play mode; drag it to preview.")]
    [Range(0f, 1f)] public float sweat;

    [Tooltip("Seconds of non-stop fighting to reach full sweat.")]
    [Min(5f)] [SerializeField] private float warmupSeconds = 75f;

    [Tooltip("Extra sweat per punch thrown (overdriven punches add more).")]
    [Range(0f, 0.05f)] [SerializeField] private float perPunch = 0.012f;

    [Tooltip("Sweat dried per second after ~6 s without activity.")]
    [Range(0f, 0.1f)] [SerializeField] private float drySpeed = 0.02f;

    [Header("Wet skin look (URP Lit)")]
    [Tooltip("Skin smoothness when drenched (glossy sheen). The dry value comes from the material.")]
    [Range(0.5f, 1f)] [SerializeField] private float wetSmoothness = 0.93f;

    [Tooltip("How much wet skin darkens (fraction of the base colour).")]
    [Range(0f, 0.4f)] [SerializeField] private float darken = 0.12f;

    [Tooltip("Strength of the droplet bumps at full sweat (detail normal scale).")]
    [Range(0f, 2f)] [SerializeField] private float dropletBumps = 0.9f;

    [Tooltip("Droplet tiling across the skin — higher = smaller beads.")]
    [Range(2f, 30f)] [SerializeField] private float dropletTiling = 10f;

    [Tooltip("Renderers or materials whose name contains any of these are left dry.")]
    [SerializeField] private string[] dryNameParts = { "glove", "short", "cloth", "hair", "eye", "teeth", "lash", "brow" };

    [Header("Beads on the body (stylized drops, glued to the skin)")]
    [SerializeField] private bool beads = true;

    [Tooltip("Beads appearing per second at full sweat, across the whole body.")]
    [Range(0f, 60f)] [SerializeField] private float beadRate = 22f;

    [Tooltip("How long a bead sits on the skin before fading (seconds, min-max).")]
    [SerializeField] private Vector2 beadLifetime = new Vector2(2.5f, 4f);

    [Tooltip("Bead size (metres, min-max).")]
    [SerializeField] private Vector2 beadScale = new Vector2(0.012f, 0.022f);

    [Tooltip("How fast a bead crawls down the skin (m/s).")]
    [Range(0f, 0.2f)] [SerializeField] private float beadCrawl = 0.03f;

    [Tooltip("Skin radius around the HEAD bone the beads sit on. Tune until they touch the skin, not float or sink.")]
    [Range(0.03f, 0.2f)] [SerializeField] private float headRadius = 0.09f;

    [Tooltip("Skin radius around the chest / spine.")]
    [Range(0.05f, 0.3f)] [SerializeField] private float torsoRadius = 0.13f;

    [Tooltip("Skin radius around the shoulders (deltoids).")]
    [Range(0.03f, 0.15f)] [SerializeField] private float shoulderRadius = 0.065f;

    [Tooltip("Single drops falling from the chin per second at full sweat. 0 = none.")]
    [Range(0f, 5f)] [SerializeField] private float chinDropRate = 1.5f;

    [SerializeField] private Color sweatDropColor = new Color(0.82f, 0.9f, 1f, 0.85f);

    // ---------------------------------------------------------------- Internals

    private class Target
    {
        public Material material;
        public float baseSmoothness = -1f;   // -1 = shader has no smoothness
        public Color baseColor;
        public string colorProperty;
        public bool hasDetail;
        public bool sweatShader;             // FlatKit/Stylized Surface Sweat: the shader does the whole wet look
    }

    private class BeadAnchor
    {
        public Transform bone;
        public ParticleSystem system;
        public float radius;
        public float verticalSpread;
    }

    private readonly List<Target> targets = new List<Target>();
    private readonly List<BeadAnchor> anchors = new List<BeadAnchor>();

    private BoxerPunchController controller;
    private Animator animator;
    private ParticleSystem chinDrops;
    private Transform headBone;
    private Material dropMaterial;
    private Texture2D dropletNormals;
    private Texture2D dropSprite;
    private Texture2D dropletMask;
    private Shader sweatShaderVariant;
    private bool sweatShaderSearched;
    private float lastActivity = -100f;
    private float beadAccumulator;
    private float chinAccumulator;
    private float applied = -1f;

    // Surface beads (used when a skinned body mesh is assigned): a periodic baked snapshot of the posed skin.
    private SkinnedMeshRenderer skinSurface;
    private Transform hipsBone;
    private Mesh bakedMesh;
    private readonly List<Vector3> bakedVertices = new List<Vector3>();
    private readonly List<Vector3> bakedNormals = new List<Vector3>();
    private readonly List<int> beadVertices = new List<int>();
    private float nextBakeTime;
    private bool bakeFailed;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<BoxerPunchController>();
        CollectMaterials();
        if (beads) BuildBeadSystems();
    }

    private void OnEnable()
    {
        if (controller != null) controller.PunchThrown += OnPunchThrown;
    }

    private void OnDisable()
    {
        if (controller != null) controller.PunchThrown -= OnPunchThrown;
    }

    private void OnDestroy()
    {
        if (dropletNormals != null) Destroy(dropletNormals);
        if (dropSprite != null) Destroy(dropSprite);
        if (dropMaterial != null) Destroy(dropMaterial);
    }

    // ---------------------------------------------------------------- Build-up

    private void OnPunchThrown(BoxerPunchController.Hand hand, BoxerPunchController.PunchType type, float overdrive)
    {
        sweat = Mathf.Clamp01(sweat + perPunch * (1f + overdrive));
        lastActivity = Time.time;
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        bool active = controller != null && (controller.IsPunching || controller.IsAiming || controller.IsBlocking);
        if (active) lastActivity = Time.time;

        if (Time.time - lastActivity < 6f) sweat = Mathf.Clamp01(sweat + dt / warmupSeconds);
        else sweat = Mathf.Clamp01(sweat - drySpeed * dt);

        Apply();
        EmitBeads(dt);
    }

    // ---------------------------------------------------------------- Wet look

    private void CollectMaterials()
    {
        bool assigned = HasAssignedSkin();
        IEnumerable<Renderer> renderers = assigned ? (IEnumerable<Renderer>)skinRenderers : GetComponentsInChildren<Renderer>();
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || renderer.GetComponent<ParticleSystem>() != null) continue;
            if (!assigned && IsDry(renderer.name)) continue;   // an assigned skin is wetted whatever its name

            foreach (Material material in renderer.materials)   // instances: sweat never edits the shared assets
            {
                if (material == null || IsDry(material.name)) continue;

                Target t = new Target { material = material };

                // FlatKit skin: switch this runtime instance to the duplicated sweat shader (the asset on disk is
                // untouched — set the material's shader to FlatKit/Stylized Surface Sweat yourself to keep it).
                if (!sweatShaderSearched) { sweatShaderVariant = Shader.Find("FlatKit/Stylized Surface Sweat"); sweatShaderSearched = true; }
                if (sweatShaderVariant != null && material.shader != null &&
                    material.shader.name.StartsWith("FlatKit/Stylized Surface") && !material.shader.name.Contains("Sweat"))
                {
                    material.shader = sweatShaderVariant;
                    Debug.Log($"BoxerSweat: '{material.name}' uses FlatKit — switched to the Stylized Surface Sweat shader for this session.", this);
                }

                if (material.HasProperty("_SweatAmount"))
                {
                    t.sweatShader = true;
                    if (dropletMask == null) dropletMask = MakeDropletMask(256, 110, 4242);
                    material.SetTexture("_SweatDropletMap", dropletMask);
                    material.SetTextureScale("_SweatDropletMap", Vector2.one * dropletTiling);
                    material.SetFloat("_SweatDarken", darken);
                    material.SetFloat("_SweatAmount", 0f);
                    targets.Add(t);
                    continue;
                }

                if (material.HasProperty("_Smoothness")) { t.baseSmoothness = material.GetFloat("_Smoothness"); }
                else if (material.HasProperty("_Glossiness")) { t.baseSmoothness = material.GetFloat("_Glossiness"); }

                if (material.HasProperty("_BaseColor")) t.colorProperty = "_BaseColor";
                else if (material.HasProperty("_Color")) t.colorProperty = "_Color";
                if (t.colorProperty != null) t.baseColor = material.GetColor(t.colorProperty);

                if (material.HasProperty("_DetailNormalMap"))
                {
                    if (dropletNormals == null) dropletNormals = MakeDropletNormals(256, 90, 12345);
                    material.SetTexture("_DetailNormalMap", dropletNormals);
                    if (material.HasProperty("_DetailAlbedoMap"))
                        material.SetTextureScale("_DetailAlbedoMap", Vector2.one * dropletTiling);   // URP detail maps share this ST
                    material.SetFloat("_DetailNormalMapScale", 0f);
                    material.EnableKeyword("_DETAIL_MULX2");
                    t.hasDetail = true;
                }

                if (t.baseSmoothness < 0f && t.colorProperty == null)
                    Debug.Log($"BoxerSweat: material '{material.name}' ({material.shader.name}) has no smoothness or colour property — it will only bead, not shine.", this);

                targets.Add(t);
            }
        }
        if (targets.Count == 0)
            Debug.LogWarning("BoxerSweat: no materials found to wet — assign the body's mesh to Skin Renderers, or check the Dry Name Parts filter.", this);
    }

    private bool HasAssignedSkin()
    {
        if (skinRenderers == null) return false;
        foreach (Renderer r in skinRenderers) if (r != null) return true;
        return false;
    }

    private bool IsDry(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string lower = name.ToLowerInvariant();
        foreach (string part in dryNameParts)
            if (!string.IsNullOrEmpty(part) && lower.Contains(part.ToLowerInvariant())) return true;
        return false;
    }

    private void Apply()
    {
        if (Mathf.Abs(sweat - applied) < 0.002f) return;
        applied = sweat;

        float sheen = Mathf.Sqrt(sweat);          // the shine shows early, the beads come later
        foreach (Target t in targets)
        {
            Material m = t.material;
            if (m == null) continue;
            if (t.sweatShader) { m.SetFloat("_SweatAmount", sweat); continue; }   // the shader does the rest
            if (t.baseSmoothness >= 0f)
            {
                float value = Mathf.Lerp(t.baseSmoothness, wetSmoothness, sheen);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", value);
                else m.SetFloat("_Glossiness", value);
            }
            if (t.colorProperty != null)
            {
                Color c = t.baseColor * Mathf.Lerp(1f, 1f - darken, sweat);
                c.a = t.baseColor.a;
                m.SetColor(t.colorProperty, c);
            }
            if (t.hasDetail) m.SetFloat("_DetailNormalMapScale", dropletBumps * sweat);
        }
    }

    // ---------------------------------------------------------------- Beads on the body

    private void BuildBeadSystems()
    {
        if (animator == null || !animator.isHuman) return;

        AddAnchor(HumanBodyBones.Head, headRadius, 0.07f);
        AddAnchor(HumanBodyBones.Chest, torsoRadius, 0.12f);
        AddAnchor(HumanBodyBones.Spine, torsoRadius, 0.10f);
        AddAnchor(HumanBodyBones.LeftUpperArm, shoulderRadius, 0.05f);
        AddAnchor(HumanBodyBones.RightUpperArm, shoulderRadius, 0.05f);

        headBone = animator.GetBoneTransform(HumanBodyBones.Head);
        hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (HasAssignedSkin())
            foreach (Renderer r in skinRenderers)
                if (r is SkinnedMeshRenderer smr) { skinSurface = smr; break; }

        if (chinDropRate > 0f && headBone != null)
        {
            chinDrops = MakeSystem(headBone, ParticleSystemSimulationSpace.World, 60);
            var main = chinDrops.main;
            main.gravityModifier = 1f;
        }
    }

    private void AddAnchor(HumanBodyBones bone, float radius, float verticalSpread)
    {
        Transform t = animator.GetBoneTransform(bone);
        if (t == null) return;
        anchors.Add(new BeadAnchor
        {
            bone = t,
            radius = radius,
            verticalSpread = verticalSpread,
            // Local simulation space + parented to the bone = beads are GLUED to that body part.
            system = MakeSystem(t, ParticleSystemSimulationSpace.Local, 150),
        });
    }

    private ParticleSystem MakeSystem(Transform parent, ParticleSystemSimulationSpace space, int maxParticles)
    {
        GameObject go = new GameObject("Sweat Beads");
        go.transform.SetParent(parent, false);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.simulationSpace = space;
        main.startSpeed = 0f;
        main.startSize = 0.01f;
        main.startLifetime = 3f;
        main.gravityModifier = 0f;            // beads crawl at their emit velocity; chin drops override this
        main.maxParticles = maxParticles;
        main.playOnAwake = true;
        main.loop = true;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;           // emitted by hand, on the skin

        // Fade in, sit, fade out — beads never pop in or out.
        ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
        col.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0.85f, 0.7f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        ParticleSystemRenderer psRenderer = go.GetComponent<ParticleSystemRenderer>();
        psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
        if (dropMaterial == null) dropMaterial = MakeDropMaterial();
        psRenderer.material = dropMaterial;
        psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return ps;
    }

    private void EmitBeads(float dt)
    {
        if (anchors.Count == 0 || sweat <= 0.05f) return;

        // Beads sitting on the skin.
        beadAccumulator += beadRate * Mathf.Pow(sweat, 1.5f) * dt;
        while (beadAccumulator >= 1f)
        {
            beadAccumulator -= 1f;
            if (!TryEmitSurfaceBead()) EmitAnchorBead();
        }

        // A single drop from the chin, once in a while.
        if (chinDrops == null || headBone == null) return;
        chinAccumulator += chinDropRate * Mathf.Pow(sweat, 1.5f) * dt;
        while (chinAccumulator >= 1f)
        {
            chinAccumulator -= 1f;
            Vector3 chin = headBone.position + Vector3.down * 0.08f + transform.forward * 0.05f;
            var p = new ParticleSystem.EmitParams
            {
                position = chin,
                velocity = Vector3.down * 0.15f,
                startSize = Random.Range(0.01f, 0.016f),
                startLifetime = 0.7f,
            };
            chinDrops.Emit(p, 1);
        }
    }

    /// <summary>Bead on the ACTUAL skin (a baked snapshot of the assigned body mesh), glued to the nearest bone.</summary>
    private bool TryEmitSurfaceBead()
    {
        if (skinSurface == null || bakeFailed) return false;
        if (Time.time >= nextBakeTime && !BakeSkin()) return false;
        if (beadVertices.Count == 0) return false;

        int index = beadVertices[Random.Range(0, beadVertices.Count)];
        Quaternion rotation = skinSurface.transform.rotation;
        Vector3 world = skinSurface.transform.position + rotation * bakedVertices[index];
        Vector3 normal = (rotation * bakedNormals[index]).normalized;
        float size = Random.Range(beadScale.x, beadScale.y);
        world += normal * (0.003f + size * 0.4f);   // clear of the skin — a billboard centred ON the surface is half-buried

        BeadAnchor glue = NearestAnchor(world);
        if (glue == null || glue.system == null) return false;
        var p = new ParticleSystem.EmitParams
        {
            position = glue.system.transform.InverseTransformPoint(world),
            velocity = glue.system.transform.InverseTransformVector(Vector3.down * beadCrawl),
            startSize = size,
            startLifetime = Random.Range(beadLifetime.x, beadLifetime.y),
        };
        glue.system.Emit(p, 1);
        return true;
    }

    /// <summary>Fallback when no skin mesh is assigned (or it cannot be baked): a ring around a bone.</summary>
    private void EmitAnchorBead()
    {
        BeadAnchor a = anchors[Random.Range(0, anchors.Count)];
        if (a.system == null) return;

        Vector3 radial = Vector3.ProjectOnPlane(Random.onUnitSphere, Vector3.up);
        if (radial.sqrMagnitude < 0.01f) radial = transform.forward;
        radial.Normalize();
        float size = Random.Range(beadScale.x, beadScale.y);
        Vector3 world = a.bone.position + radial * (a.radius + size * 0.4f) + Vector3.up * Random.Range(-a.verticalSpread, a.verticalSpread);

        var p = new ParticleSystem.EmitParams
        {
            position = a.system.transform.InverseTransformPoint(world),
            velocity = a.system.transform.InverseTransformVector(Vector3.down * beadCrawl),
            startSize = size,
            startLifetime = Random.Range(beadLifetime.x, beadLifetime.y),
        };
        a.system.Emit(p, 1);
    }

    /// <summary>Snapshot the posed skin ~5×/s; on the first bake, choose the vertices beads may sit on (above the hips).</summary>
    private bool BakeSkin()
    {
        nextBakeTime = Time.time + 0.2f;
        try
        {
            if (bakedMesh == null) bakedMesh = new Mesh { name = "Sweat Surface Bake" };
            skinSurface.BakeMesh(bakedMesh);
            bakedMesh.GetVertices(bakedVertices);
            bakedMesh.GetNormals(bakedNormals);
        }
        catch (System.Exception e)
        {
            return BakeFallback("bake failed: " + e.Message);
        }
        if (bakedVertices.Count == 0 || bakedNormals.Count != bakedVertices.Count)
            return BakeFallback("mesh has no readable surface");

        if (beadVertices.Count == 0)
        {
            Quaternion rotation = skinSurface.transform.rotation;
            Vector3 position = skinSurface.transform.position;

            // Sanity check the bake→world mapping against the renderer's real bounds before trusting it.
            Bounds check = new Bounds(position + rotation * bakedVertices[0], Vector3.zero);
            for (int i = 1; i < bakedVertices.Count; i += 89) check.Encapsulate(position + rotation * bakedVertices[i]);
            if ((check.center - skinSurface.bounds.center).magnitude > 1f)
                return BakeFallback("unexpected mesh scale");

            float minY = hipsBone != null ? hipsBone.position.y - 0.02f : transform.position.y + 0.8f;
            for (int i = 0; i < bakedVertices.Count; i++)
                if ((position + rotation * bakedVertices[i]).y >= minY) beadVertices.Add(i);
            if (beadVertices.Count == 0)
                return BakeFallback("no vertices above the hips");
        }
        return true;
    }

    private bool BakeFallback(string reason)
    {
        bakeFailed = true;
        Debug.Log($"BoxerSweat: cannot place beads on the skin surface ({reason}) — using bone rings instead.", this);
        return false;
    }

    private BeadAnchor NearestAnchor(Vector3 world)
    {
        BeadAnchor best = null;
        float bestSqr = float.MaxValue;
        foreach (BeadAnchor a in anchors)
        {
            float sqr = (a.bone.position - world).sqrMagnitude;
            if (sqr < bestSqr) { bestSqr = sqr; best = a; }
        }
        return best;
    }

    private Material MakeDropMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
        Material m = new Material(shader) { name = "Sweat Drop (runtime)" };
        if (dropSprite == null) dropSprite = MakeDropSprite(64);
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", dropSprite);
        else if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", dropSprite);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", sweatDropColor);
        else if (m.HasProperty("_Color")) m.SetColor("_Color", sweatDropColor);

        // Transparent alpha blend (URP particle shaders are opaque by default).
        if (m.HasProperty("_Surface"))
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        return m;
    }

    // ---------------------------------------------------------------- Generated textures

    /// <summary>Tiling tangent-space normal map of random droplet bumps (some slightly run down).</summary>
    private static Texture2D MakeDropletNormals(int size, int count, int seed)
    {
        float[,] height = new float[size, size];
        System.Random rng = new System.Random(seed);

        for (int d = 0; d < count; d++)
        {
            float cx = (float)rng.NextDouble() * size;
            float cy = (float)rng.NextDouble() * size;
            float rx = Mathf.Lerp(2.5f, 9f, (float)rng.NextDouble());
            float ry = rx * Mathf.Lerp(1f, 2.4f, (float)(rng.NextDouble() * rng.NextDouble()));   // a few streaked drops

            int x0 = Mathf.FloorToInt(cx - rx - 1f), x1 = Mathf.CeilToInt(cx + rx + 1f);
            int y0 = Mathf.FloorToInt(cy - ry - 1f), y1 = Mathf.CeilToInt(cy + ry + 1f);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    float q = 1f - dx * dx - dy * dy;
                    if (q <= 0f) continue;
                    float h = Mathf.Sqrt(q) * rx;                       // spherical cap
                    int xi = (x % size + size) % size, yi = (y % size + size) % size;
                    if (h > height[xi, yi]) height[xi, yi] = h;
                }
        }

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "Sweat Droplet Normals (runtime)" };
        var pixels = new Color32[size * size];
        const float strength = 1.1f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float hx = height[(x + 1) % size, y] - height[(x - 1 + size) % size, y];
                float hy = height[x, (y + 1) % size] - height[x, (y - 1 + size) % size];
                Vector3 n = new Vector3(-hx * strength, -hy * strength, 2f).normalized;
                pixels[y * size + x] = new Color32(
                    (byte)((n.x * 0.5f + 0.5f) * 255f),
                    (byte)((n.y * 0.5f + 0.5f) * 255f),
                    (byte)((n.z * 0.5f + 0.5f) * 255f), 255);
            }
        tex.SetPixels32(pixels);
        tex.Apply(true);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        return tex;
    }

    /// <summary>Droplet mask: each drop stores a random brightness, so drops appear one by one as sweat rises.</summary>
    private static Texture2D MakeDropletMask(int size, int count, int seed)
    {
        float[,] value = new float[size, size];
        System.Random rng = new System.Random(seed);
        for (int d = 0; d < count; d++)
        {
            float cx = (float)rng.NextDouble() * size;
            float cy = (float)rng.NextDouble() * size;
            float rx = Mathf.Lerp(2f, 7f, (float)rng.NextDouble());
            float ry = rx * Mathf.Lerp(1f, 2.2f, (float)(rng.NextDouble() * rng.NextDouble()));
            float brightness = Mathf.Lerp(0.15f, 1f, (float)rng.NextDouble());
            int x0 = Mathf.FloorToInt(cx - rx - 1f), x1 = Mathf.CeilToInt(cx + rx + 1f);
            int y0 = Mathf.FloorToInt(cy - ry - 1f), y1 = Mathf.CeilToInt(cy + ry + 1f);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    float q = 1f - dx * dx - dy * dy;
                    if (q <= 0f) continue;
                    int xi = (x % size + size) % size, yi = (y % size + size) % size;
                    float v = brightness * Mathf.Sqrt(q);
                    if (v > value[xi, yi]) value[xi, yi] = v;
                }
        }
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "Sweat Droplet Mask (runtime)" };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte b = (byte)(Mathf.Clamp01(value[x, y]) * 255f);
                px[y * size + x] = new Color32(b, b, b, 255);
            }
        tex.SetPixels32(px);
        tex.Apply(true);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        return tex;
    }

    /// <summary>A readable droplet: solid soft-edged disc with a bright specular glint off-centre — contrast, not haze.</summary>
    private static Texture2D MakeDropSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "Sweat Drop Sprite (runtime)" };
        var pixels = new Color32[size * size];
        float c = (size - 1) * 0.5f;
        float glintX = c * 0.68f;            // the highlight sits upper-left, like light on a real drop
        float glintY = c * 1.32f;
        float glintRadius = c * 0.34f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                float t = Mathf.Clamp01((d - 0.68f) / 0.32f);
                float body = 1f - t * t * (3f - 2f * t);                 // solid centre, soft rim
                float dg = Mathf.Sqrt((x - glintX) * (x - glintX) + (y - glintY) * (y - glintY)) / glintRadius;
                float glint = Mathf.Clamp01(1f - dg);
                glint *= glint;
                float alpha = body * (0.82f + 0.18f * glint);
                float v = Mathf.Lerp(0.8f, 1f, glint);
                pixels[y * size + x] = new Color32(
                    (byte)(v * 0.93f * 255f), (byte)(v * 0.97f * 255f), 255, (byte)(alpha * 255f));
            }
        tex.SetPixels32(pixels);
        tex.Apply(true);
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }
}
