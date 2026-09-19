using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Mesh-level life for the heavy bag — what makes leather read as leather instead of a painted capsule.
///
/// Three layers, all vertex offsets on a copy of the bag mesh:
///   • IMPACT DENTS — a hit punches a bell-shaped dent in, and a damped spring rings it back out with a wobble.
///   • CONTACT DENTS — a glove pressed into the bag keeps a live dent under it that follows the fist and
///     relaxes when it leaves (fed by <see cref="PunchingBag.Lean"/> / <see cref="PunchingBag.SetContact"/>).
///   • RIPPLES — a hard hit sends a circular shockwave across the surface away from the knuckles, decaying as
///     it travels: the shudder that makes a heavy hit look HEAVY from any camera angle.
///
/// Visual only — the capsule collider still does the physics. Added automatically by <see cref="PunchingBag"/>.
/// </summary>
[DisallowMultipleComponent]
public class BagDeformer : MonoBehaviour
{
    private class DentState
    {
        public int id = -1;            // ≥ 0: a live contact dent owned by a glove; -1: an impact dent
        public Vector3 localPoint;     // MESH-LOCAL: the dent rides the leather as the bag swings away
        public Vector3 localDir;       // mesh-local, into the bag
        public float radius;           // world metres
        public float depth;            // current, world metres
        public float target;           // where the spring wants to be
        public float velocity;
        public float lastTouch;
        public float creaseRate;       // how fast the residual crease fades (impact dents only)
    }

    private class RippleState
    {
        public Vector3 origin;         // world at spawn
        public Vector3 direction;      // inward at origin
        public float age;
        public float amplitude;        // world metres at t=0
    }

    [Header("Dents")]
    [Tooltip("How fast a dent springs back (per second²).")]
    [Min(1f)] [SerializeField] private float spring = 180f;
    [Tooltip("Damping of the spring — lower wobbles more.")]
    [Min(0f)] [SerializeField] private float damping = 13f;

    [Tooltip("Spring of an IMPACT dent ringing back OUT. (The stiff spring above keeps contact dents tracking " +
             "the glove.) Softer = the crater stays visible after the fist leaves — before this, every dent " +
             "lived and died entirely UNDER the planted glove and was never once seen.")]
    [Min(1f)] [SerializeField] private float reboundSpring = 40f;
    [Min(0f)] [SerializeField] private float reboundDamping = 9f;

    [Tooltip("Fraction of an impact dent that stays behind as a soft CREASE in the leather, fading out over " +
             "Crease Seconds — a beaten bag looks beaten for a moment.")]
    [Range(0f, 0.6f)] [SerializeField] private float creaseFraction = 0.25f;
    [Min(0.1f)] [SerializeField] private float creaseSeconds = 3.5f;
    [Tooltip("Dents kept at once (oldest impact dents are recycled).")]
    [Range(1, 12)] [SerializeField] private int maxDents = 6;

    [Header("Ripples")]
    [Tooltip("Surface shockwave amplitude for a full-strength hit (metres). Small — it is a shudder, not jelly.")]
    [Range(0f, 0.06f)] [SerializeField] private float rippleAmplitude = 0.022f;
    [Tooltip("How fast the ring travels across the leather (m/s).")]
    [Min(0.1f)] [SerializeField] private float rippleSpeed = 2.6f;
    [Tooltip("Ring wavelength (metres).")]
    [Min(0.05f)] [SerializeField] private float rippleLength = 0.35f;
    [Tooltip("Seconds a ripple lives.")]
    [Min(0.05f)] [SerializeField] private float rippleLife = 0.5f;

    [Header("Cost")]
    [Tooltip("Recalculate lighting normals while deformed (costs a little on big meshes).")]
    [SerializeField] private bool recalculateNormals = true;

    public bool IsReady => mesh != null;

    private MeshFilter filter;
    private Mesh mesh;
    private Vector3[] baseVertices;
    private Vector3[] vertices;
    private float toLocal = 1f;
    private readonly List<DentState> dents = new List<DentState>();
    private readonly List<RippleState> ripples = new List<RippleState>(4);
    private bool wasDeformed;

    private void Awake()
    {
        filter = LargestMeshFilter();   // the bag body — bag models also carry chain-link meshes, which come first in the hierarchy
        if (filter == null || filter.sharedMesh == null)
        {
            Debug.LogWarning("BagDeformer: no MeshFilter under the bag — squish disabled (skinned bags aren't supported).", this);
            enabled = false;
            return;
        }

#if UNITY_EDITOR
        // Denting rewrites vertices every frame, which needs Read/Write enabled on the model. Fix the import
        // setting automatically in the editor so the squish just works.
        if (!filter.sharedMesh.isReadable)
        {
            string path = UnityEditor.AssetDatabase.GetAssetPath(filter.sharedMesh);
            if (!string.IsNullOrEmpty(path) && UnityEditor.AssetImporter.GetAtPath(path) is UnityEditor.ModelImporter importer && !importer.isReadable)
            {
                Debug.Log($"BagDeformer: enabling Read/Write on '{path}' so the bag mesh can be dented.", this);
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }
#endif
        if (!filter.sharedMesh.isReadable)
        {
            Debug.LogWarning($"BagDeformer: mesh '{filter.sharedMesh.name}' has Read/Write disabled in its import settings — squish disabled. " +
                             "Select the model in the Project window, tick Read/Write in the Model tab, press Apply.", this);
            enabled = false;
            return;
        }

        mesh = Instantiate(filter.sharedMesh);
        mesh.name = filter.sharedMesh.name + " (deformable)";
        mesh.MarkDynamic();
        filter.mesh = mesh;
        baseVertices = mesh.vertices;
        vertices = (Vector3[])baseVertices.Clone();

        Vector3 s = filter.transform.lossyScale;
        float avg = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
        toLocal = avg > 0.0001f ? 1f / avg : 1f;
    }

    private void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
    }

    private MeshFilter LargestMeshFilter()
    {
        MeshFilter best = null;
        float bestVolume = -1f;
        foreach (MeshFilter f in GetComponentsInChildren<MeshFilter>())
        {
            if (f.sharedMesh == null) continue;
            Vector3 s = Vector3.Scale(f.sharedMesh.bounds.size, f.transform.lossyScale);
            float volume = Mathf.Abs(s.x * s.y * s.z);
            if (volume > bestVolume) { bestVolume = volume; best = f; }
        }
        return best;
    }

    // ---------------------------------------------------------------- Public API

    /// <summary>An impact: dent the surface at a point, then spring back.</summary>
    public void Dent(Vector3 worldPoint, Vector3 worldDirection, float depth, float radius)
    {
        if (!IsReady || depth <= 0f) return;
        Vector3 lp = filter.transform.InverseTransformPoint(worldPoint);
        float lr = Mathf.Max(0.001f, radius * toLocal);

        DentState d = null;
        foreach (DentState existing in dents)
            if (existing.id < 0 && (existing.localPoint - lp).sqrMagnitude < lr * lr * 0.25f) { d = existing; break; }
        if (d == null)
        {
            if (dents.Count >= maxDents) { d = OldestImpactDent(); if (d == null) return; }
            else { d = new DentState(); dents.Add(d); }
        }
        d.id = -1;
        d.localPoint = lp;
        d.localDir = filter.transform.InverseTransformDirection(worldDirection.normalized).normalized;
        d.radius = radius;
        d.depth = Mathf.Max(d.depth, depth);
        d.velocity = 0f;
        // The crater doesn't vanish: a fraction stays as a soft crease and fades out on its own clock.
        d.target = depth * creaseFraction;
        d.creaseRate = creaseSeconds > 0.01f ? d.target / creaseSeconds : d.target;
        d.lastTouch = Time.time;
    }

    /// <summary>
    /// How deep the leather is CURRENTLY carved at a world point (metres) — the visible pocket. The glove
    /// containment measures against this, so the fist may sit exactly as deep as the crater it actually made.
    /// </summary>
    public float CarveDepthAt(Vector3 worldPoint)
    {
        if (!IsReady || dents.Count == 0) return 0f;
        Vector3 v = filter.transform.InverseTransformPoint(worldPoint);
        float carve = 0f;
        foreach (DentState d in dents)
        {
            if (d.depth <= 0f) continue;
            float lr = Mathf.Max(0.001f, d.radius * toLocal);
            float x = (v - d.localPoint).magnitude / lr;
            if (x >= 1f) continue;
            float falloff = 1f - x * x;
            carve += d.depth * falloff * falloff;
        }
        return carve;
    }

    /// <summary>A hard hit: send a circular shockwave across the leather away from the contact.</summary>
    public void Ripple(Vector3 worldPoint, Vector3 worldDirection, float strength)
    {
        if (!IsReady || rippleAmplitude <= 0f) return;
        if (ripples.Count >= 4) ripples.RemoveAt(0);
        ripples.Add(new RippleState
        {
            origin = worldPoint,
            direction = worldDirection.normalized,
            age = 0f,
            amplitude = rippleAmplitude * Mathf.Clamp01(strength),
        });
    }

    /// <summary>A glove pressed into the bag: keep a dent under it, following it, until <see cref="ClearContact"/>.</summary>
    public void SetContact(int id, Vector3 worldPoint, Vector3 worldDirection, float depth, float radius)
    {
        if (!IsReady) return;
        DentState d = null;
        foreach (DentState existing in dents) if (existing.id == id) { d = existing; break; }
        if (d == null)
        {
            if (dents.Count >= maxDents) { d = RecyclableImpactDent(); if (d == null) return; }
            else { d = new DentState(); dents.Add(d); }
            d.depth = 0f;
            d.velocity = 0f;
        }
        d.id = id;
        d.localPoint = filter.transform.InverseTransformPoint(worldPoint);
        d.localDir = filter.transform.InverseTransformDirection(worldDirection.normalized).normalized;
        d.radius = radius;
        d.target = Mathf.Max(0f, depth);
        d.creaseRate = 0f;
        d.lastTouch = Time.time;
    }

    public void ClearContact(int id)
    {
        foreach (DentState d in dents)
            if (d.id == id) { d.id = -1; d.target = 0f; }
    }

    private DentState OldestImpactDent()
    {
        DentState oldest = null;
        foreach (DentState d in dents) if (d.id < 0 && (oldest == null || d.lastTouch < oldest.lastTouch)) oldest = d;
        return oldest;
    }

    /// <summary>
    /// A dent a GLOVE may take over without erasing what the player is watching: the shallowest impact dent
    /// at least 0.3 s old. (The glove's own follow-through contact used to steal the crater it had just made,
    /// wiping it the same frame it was born.)
    /// </summary>
    private DentState RecyclableImpactDent()
    {
        DentState best = null;
        foreach (DentState d in dents)
        {
            if (d.id >= 0 || Time.time - d.lastTouch < 0.3f) continue;
            if (best == null || Mathf.Abs(d.depth) < Mathf.Abs(best.depth)) best = d;
        }
        return best;
    }

    // ---------------------------------------------------------------- Simulation

    private void LateUpdate()
    {
        if (!IsReady) return;
        float dt = Time.deltaTime;
        bool any = false;

        for (int i = dents.Count - 1; i >= 0; i--)
        {
            DentState d = dents[i];
            if (d.id >= 0 && Time.time - d.lastTouch > 0.25f) { d.id = -1; d.target = 0f; }   // glove stopped reporting

            // Contact dents track the glove on the stiff spring; impact dents ring OUT on the soft one and
            // keep a fading crease — so a crater finally OUTLIVES the fist that made it.
            bool impactDent = d.id < 0;
            if (impactDent && d.creaseRate > 0f) d.target = Mathf.MoveTowards(d.target, 0f, d.creaseRate * dt);
            float k = impactDent ? reboundSpring : spring;
            float c = impactDent ? reboundDamping : damping;
            d.velocity += (k * (d.target - d.depth) - c * d.velocity) * dt;
            d.depth += d.velocity * dt;

            if (d.target <= 0f && Mathf.Abs(d.depth) < 0.0005f && Mathf.Abs(d.velocity) < 0.003f) { dents.RemoveAt(i); continue; }
            any = true;
        }

        for (int i = ripples.Count - 1; i >= 0; i--)
        {
            ripples[i].age += dt;
            if (ripples[i].age >= rippleLife) { ripples.RemoveAt(i); continue; }
            any = true;
        }

        if (any || wasDeformed) Rebuild();
        wasDeformed = any;
    }

    private void Rebuild()
    {
        Transform t = filter.transform;
        int n = baseVertices.Length;
        int dentCount = dents.Count;
        int rippleCount = ripples.Count;

        // Dent data in the mesh's local space.
        Vector3[] lp = new Vector3[dentCount];
        Vector3[] ld = new Vector3[dentCount];
        float[] lr = new float[dentCount];
        float[] ldepth = new float[dentCount];
        for (int k = 0; k < dentCount; k++)
        {
            DentState d = dents[k];
            lp[k] = d.localPoint;                       // anchored in MESH space: the dent rides the swinging leather
            ld[k] = d.localDir;
            lr[k] = Mathf.Max(0.001f, d.radius * toLocal);
            ldepth[k] = d.depth * toLocal;
        }

        // Ripple data, local space. The wave: A · sin(2π (dist − c·t) / λ), windowed to one travelling band.
        Vector3[] rp = new Vector3[rippleCount];
        Vector3[] rd = new Vector3[rippleCount];
        float[] rFront = new float[rippleCount];   // how far the ring has travelled (local)
        float[] rAmp = new float[rippleCount];
        float rLen = Mathf.Max(0.01f, rippleLength * toLocal);
        for (int k = 0; k < rippleCount; k++)
        {
            RippleState r = ripples[k];
            rp[k] = t.InverseTransformPoint(r.origin);
            rd[k] = t.InverseTransformDirection(r.direction).normalized;
            rFront[k] = r.age * rippleSpeed * toLocal;
            float fade = 1f - r.age / rippleLife;
            rAmp[k] = r.amplitude * toLocal * fade * fade;
        }

        for (int i = 0; i < n; i++)
        {
            Vector3 v = baseVertices[i];
            Vector3 offset = Vector3.zero;

            for (int k = 0; k < dentCount; k++)
            {
                if (Mathf.Abs(ldepth[k]) < 1e-6f) continue;
                float dist = (v - lp[k]).magnitude;
                if (dist >= lr[k]) continue;
                float x = dist / lr[k];
                float falloff = 1f - x * x;
                falloff *= falloff;                                   // smooth bell
                offset += ld[k] * (ldepth[k] * falloff);
            }

            for (int k = 0; k < rippleCount; k++)
            {
                if (rAmp[k] < 1e-6f) continue;
                float dist = (v - rp[k]).magnitude;
                float band = dist - rFront[k];
                if (band < -rLen || band > rLen) continue;            // only the travelling ring moves
                float envelope = 1f - Mathf.Abs(band) / rLen;         // triangular window over one wavelength
                float wave = Mathf.Sin(band / rLen * Mathf.PI);
                float distanceFade = 1f / (1f + (dist / toLocal) * 2.5f);   // fade over WORLD metres — local units lied by the bag's 30x scale
                offset += rd[k] * (rAmp[k] * wave * envelope * distanceFade);
            }

            vertices[i] = v + offset;
        }

        mesh.vertices = vertices;
        if (recalculateNormals && n <= 20000) mesh.RecalculateNormals();
    }
}
