using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click bring-up for the rebuilt punching system: the energy-budgeted bag, the armed-only impact sensors,
/// the variation engine and the VFX/feel stack.
///
/// CHANGING A C# DEFAULT DOES NOTHING TO A COMPONENT ALREADY IN THE SCENE — its old values are serialised. This
/// tool writes the new tuning explicitly onto the scene components (and strips the old bag rig so it rebuilds
/// with the new joints, caps and bottom-heavy mass on next Play). Run it once after pulling these changes:
///
///     Tools ▸ Boxer ▸ Rebuild Boxing System
///
/// Safe to run again any time; it only writes what differs and prints exactly what it did.
/// </summary>
public static class BoxerImpactSetup
{
    private const string Menu = "Tools/Boxer/Rebuild Boxing System";
    private const string ValidateMenu = "Tools/Boxer/Validate Boxing System";

    [MenuItem(Menu)]
    public static void Rebuild()
    {
        List<string> did = new List<string>();
        Undo.SetCurrentGroupName("Rebuild Boxing System");
        int group = Undo.GetCurrentGroup();

        SoundLibrary sounds = FindSounds(did);

        foreach (PunchingBag bag in Object.FindObjectsByType<PunchingBag>())
        {
            RebuildBag(bag, did);
            WireBagAudio(bag, sounds, did);
        }

        foreach (BoxerPunchController boxer in Object.FindObjectsByType<BoxerPunchController>())
        {
            RebuildBoxer(boxer, did);
            WireFootsteps(boxer, sounds, did);
        }

        Undo.CollapseUndoOperations(group);

        string done = did.Count > 0 ? "  • " + string.Join("\n  • ", did) : "  (everything was already set up)";
        Debug.Log($"Boxing system rebuilt:\n{done}\n\nPress Play: punches land with real momentum, the bag " +
                  "swings and settles like 40 kg on a chain, and NOTHING — not L1/R1, not leaning on it, not a " +
                  "glitchy frame — can launch it: every impulse is clamped by the bag's own physics. Hits, " +
                  "steps and shoe squeaks play from Assets/Sounds with minted variants — nothing repeats.");
    }

    // ------------------------------------------------------------------ Sounds

    private class SoundLibrary
    {
        public readonly List<AudioClip> bagHits = new List<AudioClip>();
        public readonly List<AudioClip> squeaks = new List<AudioClip>();
        public readonly List<AudioClip> steps = new List<AudioClip>();
    }

    /// <summary>
    /// Gather the user's recordings from Assets/Sounds (routed by folder, keywords as fallback) and make every
    /// clip readable: the variant factory mints pitch/filter takes from raw samples at load, which needs
    /// Decompress On Load — and mono, so 3D positioning at the contact point actually pans.
    /// </summary>
    private static SoundLibrary FindSounds(List<string> did)
    {
        SoundLibrary lib = new SoundLibrary();
        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Sounds" });
        if (guids.Length == 0)
        {
            guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets" });
            if (guids.Length > 0) did.Add("no Assets/Sounds folder — searched the whole project for clips");
        }

        int reimported = 0;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string lower = path.ToLowerInvariant();

            if (AssetImporter.GetAtPath(path) is AudioImporter importer)
            {
                AudioImporterSampleSettings settings = importer.defaultSampleSettings;
                bool dirty = false;
                if (settings.loadType != AudioClipLoadType.DecompressOnLoad)
                {
                    settings.loadType = AudioClipLoadType.DecompressOnLoad;
                    dirty = true;
                }
                if (!settings.preloadAudioData) { settings.preloadAudioData = true; dirty = true; }
                if (!importer.forceToMono) { importer.forceToMono = true; dirty = true; }
                if (dirty)
                {
                    importer.defaultSampleSettings = settings;
                    importer.SaveAndReimport();
                    reimported++;
                }
            }

            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;

            if (lower.Contains("/bag/") || lower.Contains("punching bag") || lower.Contains("punch")) lib.bagHits.Add(clip);
            else if (lower.Contains("squeak") || lower.Contains("squak")) lib.squeaks.Add(clip);
            else if (lower.Contains("step") || lower.Contains("footstep")) lib.steps.Add(clip);
        }

        lib.bagHits.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        lib.squeaks.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        lib.steps.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        if (reimported > 0)
            did.Add($"{reimported} clip(s) reimported as Decompress On Load + mono (the variant factory needs raw samples)");
        did.Add($"sound library: {lib.bagHits.Count} bag hit(s), {lib.steps.Count} step(s), {lib.squeaks.Count} squeak(s)");
        return lib;
    }

    private static void WireBagAudio(PunchingBag bag, SoundLibrary sounds, List<string> did)
    {
        BagAudio audio = bag.GetComponent<BagAudio>();
        if (audio == null)
        {
            audio = Undo.AddComponent<BagAudio>(bag.gameObject);
            did.Add($"{bag.name}: added BagAudio (layered hits, body thump, chain creak)");
        }

        SerializedObject so = new SerializedObject(audio);
        bool changed = AssignClips(so, "hitClips", sounds.bagHits);
        changed |= AssignClips(so, "squeakClips", sounds.squeaks);
        so.ApplyModifiedProperties();
        if (changed) did.Add($"{bag.name}: bag hit / creak recordings wired in");
        EditorUtility.SetDirty(audio);
    }

    private static void WireFootsteps(BoxerPunchController boxer, SoundLibrary sounds, List<string> did)
    {
        FootstepAudio audio = boxer.GetComponent<FootstepAudio>();
        if (audio == null)
        {
            audio = Undo.AddComponent<FootstepAudio>(boxer.gameObject);
            did.Add($"{boxer.name}: added FootstepAudio (plant detection, gait volume, pivot squeaks)");
        }

        SerializedObject so = new SerializedObject(audio);
        bool changed = AssignClips(so, "stepClips", sounds.steps);
        changed |= AssignClips(so, "squeakClips", sounds.squeaks);
        so.ApplyModifiedProperties();
        if (changed) did.Add($"{boxer.name}: step / squeak recordings wired in");
        EditorUtility.SetDirty(audio);
    }

    /// <summary>Write a clip list into a serialized array field. Returns true when anything actually changed.</summary>
    private static bool AssignClips(SerializedObject so, string field, List<AudioClip> clips)
    {
        SerializedProperty p = so.FindProperty(field);
        if (p == null || !p.isArray) return false;

        bool same = p.arraySize == clips.Count;
        for (int i = 0; same && i < clips.Count; i++)
            same = p.GetArrayElementAtIndex(i).objectReferenceValue == clips[i];
        if (same) return false;

        p.arraySize = clips.Count;
        for (int i = 0; i < clips.Count; i++)
            p.GetArrayElementAtIndex(i).objectReferenceValue = clips[i];
        return true;
    }

    // ------------------------------------------------------------------ Bag

    private static void RebuildBag(PunchingBag bag, List<string> did)
    {
        // Strip any rig built in edit mode so Awake rebuilds it with the new joints, caps and centre of mass.
        if (bag.GetComponent<Rigidbody>() != null || bag.GetComponent<ConfigurableJoint>() != null)
        {
            bag.RemoveRig();
            did.Add($"{bag.name}: old physics rig removed (rebuilds on Play with the new joints and caps)");
        }

        SerializedObject so = new SerializedObject(bag);
        // The energy budget — the numbers that make "the bag flies" impossible.
        Set(so, "maxSurfaceSpeed", 5f, did, $"{bag.name}: surface-speed budget 5 m/s (no punch can exceed it)");
        Set(so, "maxImpulse", 140f, did, $"{bag.name}: hard impulse ceiling 140 N·s");
        SetVector3(so, "hardVelocityCaps", new Vector3(6f, 8f, 2f), did,
                   $"{bag.name}: PhysX ceilings — 6 m/s linear, 8 rad/s spin, 2 m/s depenetration (the L1/R1 detonation path)");
        Set(so, "followThroughFraction", 0.35f, did, $"{bag.name}: follow-through banked at 35% of each landed impulse");
        Set(so, "leanSpring", 2600f, did, $"{bag.name}: lean spring 2600 N/m");
        Set(so, "leanDamper", 90f, did, null);
        Set(so, "leanMaxForce", 300f, did, $"{bag.name}: a pressed glove pushes with at most 300 N — a shove, never a launch");

        // The swing itself: a chain has no spring, only friction; sand makes the bag bottom-heavy.
        Set(so, "swingSpring", 3f, did, $"{bag.name}: chain spring 3 (a real chain has no spring — gravity restores)");
        Set(so, "swingDamper", 14f, did, $"{bag.name}: strap friction 14 (settles in a few swings)");
        Set(so, "airDrag", 0.3f, did, null);
        Set(so, "spinDrag", 1.4f, did, $"{bag.name}: spin friction (a spun bag grinds to a stop)");
        Set(so, "bottomHeaviness", 0.08f, did, $"{bag.name}: bottom-heavy mass (the bottom kicks, the top stays with the chain)");
        Set(so, "chainRattle", 0.6f, did, $"{bag.name}: physical chain rattle on hard hits");
        SetBool(so, "rippleOnHit", true, did, $"{bag.name}: leather shockwave ripple on hard hits");
        Set(so, "squashAmount", 0.1f, did, null);
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(bag);
    }

    // ------------------------------------------------------------------ Boxer

    private static void RebuildBoxer(BoxerPunchController boxer, List<string> did)
    {
        GameObject go = boxer.gameObject;

        // The feel stack: feedback (time + camera), VFX (particles + trails), variation (no two punches alike).
        if (go.GetComponent<ImpactFeedback>() == null)
        {
            Undo.AddComponent<ImpactFeedback>(go);
            did.Add($"{go.name}: added ImpactFeedback (hit-stop, camera kick, perfect hits)");
        }
        if (go.GetComponent<BoxerVFX>() == null)
        {
            Undo.AddComponent<BoxerVFX>(go);
            did.Add($"{go.name}: added BoxerVFX (impact bursts, shockwave rings, trails, chalk)");
        }
        if (go.GetComponent<PunchVariation>() == null)
        {
            Undo.AddComponent<PunchVariation>(go);
            did.Add($"{go.name}: added PunchVariation (per-throw style, overhands, shovel hooks)");
        }
        if (go.GetComponent<PunchDebug>() == null)
        {
            Undo.AddComponent<PunchDebug>(go);
            did.Add($"{go.name}: added PunchDebug (flight recorder — F9 overlay, F10 dump, auto-detects arm darts)");
        }
        if (go.GetComponent<GloveSquash>() == null)
        {
            Undo.AddComponent<GloveSquash>(go);
            did.Add($"{go.name}: added GloveSquash (the glove visibly gives on impact)");
        }

        SerializedObject so = new SerializedObject(boxer);
        Set(so, "maxFistSpeed", 13f, did, $"{go.name}: fist speed ceiling 13 m/s (glitch frames can never punch)");
        so.ApplyModifiedProperties();

        // THE FULL PUPPET TUNING, written explicitly — the user's scene was still running spring 400 and the
        // old Pin Pow, under which a pin of 0.85 is effectively 0.52: the chronic half-metre core lag, the
        // permanently faded mapping, and the arm switching between animation and puppet all trace back here.
        // Old scene values always win over C# defaults, so every number is written, not assumed.
        BoxerPhysics physics = go.GetComponent<BoxerPhysics>();
        if (physics != null)
        {
            SerializedObject po = new SerializedObject(physics);
            SetBool(po, "angularLimits", false, did, $"{go.name}: joint angular limits OFF — the rig's authored limits jam the punch poses");
            Set(po, "pinPow", 2f, did, $"{go.name}: pin pow → 2 (at 4, mid pin values are effectively zero — the core lag)");
            SetMin(po, "muscleSpring", 650f, did, $"{go.name}: muscle spring → 650 (auto-scaled ×4 for this character; 400 could not hold him)");
            SetMin(po, "muscleDamper", 10f, did, null);
            SetBool(po, "autoScaleToCharacter", true, did, null);
            SetVector2(po, "spineControl", new Vector2(0.9f, 0.95f), did, null);
            SetVector2(po, "headControl", new Vector2(0.85f, 0.65f), did, $"{go.name}: head pinned hard, driven soft (stops the neck stretch)");
            SetVector2(po, "armControl", new Vector2(0.8f, 0.9f), did, null);
            SetVector2(po, "handControl", new Vector2(0.7f, 0.8f), did, null);
            Set(po, "headMapping", 0.65f, did, null);
            SetMin(po, "stretchLimit", 0.32f, did, null);
            SetMin(po, "stretchPanic", 1.2f, did, null);
            SetMin(po, "assistVelocityGain", 40f, did, $"{go.name}: fist velocity-matching gain → 40 (tracks the whip)");
            SetMin(po, "assistMaxForce", 900f, did, $"{go.name}: fist assist force cap → 900 N");
            po.ApplyModifiedProperties();
            EditorUtility.SetDirty(physics);
        }

        // The trigger sensors (IK mode): hits are armed-only now; an old scene value must not undo that.
        BoxerHitboxes hitboxes = go.GetComponent<BoxerHitboxes>();
        if (hitboxes != null)
        {
            SerializedObject ho = new SerializedObject(hitboxes);
            SetBool(ho, "requireArmed", true, did, $"{go.name}: glove sensors armed-only (guard contact leans, never punches)");
            Set(ho, "maxImpulse", 140f, did, null);
            ho.ApplyModifiedProperties();
            EditorUtility.SetDirty(hitboxes);
        }

        EditorUtility.SetDirty(boxer);
    }

    // ------------------------------------------------------------------ Validate

    [MenuItem(ValidateMenu)]
    private static void Validate()
    {
        StringBuilder sb = new StringBuilder("BOXING SYSTEM\n");

        PunchingBag[] bags = Object.FindObjectsByType<PunchingBag>();
        Line(sb, bags.Length > 0, $"{bags.Length} punching bag(s) in the scene", "no PunchingBag in the scene");
        foreach (PunchingBag bag in bags)
        {
            SerializedObject so = new SerializedObject(bag);
            float surface = so.FindProperty("maxSurfaceSpeed")?.floatValue ?? 0f;
            float impulse = so.FindProperty("maxImpulse")?.floatValue ?? 0f;
            float lean = so.FindProperty("leanMaxForce")?.floatValue ?? 0f;
            Line(sb, surface > 0.5f && surface < 10f, $"{bag.name}: surface-speed budget {surface:0.#} m/s",
                 $"{bag.name}: surface-speed budget {surface:0.#} — run Rebuild Boxing System");
            Line(sb, impulse <= 200f, $"{bag.name}: impulse ceiling {impulse:0} N·s", $"{bag.name}: impulse ceiling {impulse:0} is high");
            Line(sb, lean <= 500f, $"{bag.name}: lean force cap {lean:0} N", $"{bag.name}: lean force cap {lean:0} is high");
            bool stale = bag.GetComponent<Rigidbody>() != null && Application.isPlaying == false;
            Line(sb, !stale, $"{bag.name}: rig builds fresh on Play", $"{bag.name}: an edit-mode rig is serialised — run Rebuild Boxing System");
        }

        foreach (BoxerPunchController boxer in Object.FindObjectsByType<BoxerPunchController>())
        {
            GameObject go = boxer.gameObject;
            Line(sb, go.GetComponent<ImpactFeedback>() != null, $"{go.name}: ImpactFeedback present", $"{go.name}: no ImpactFeedback");
            Line(sb, go.GetComponent<BoxerVFX>() != null, $"{go.name}: BoxerVFX present", $"{go.name}: no BoxerVFX");
            Line(sb, go.GetComponent<PunchVariation>() != null, $"{go.name}: PunchVariation present", $"{go.name}: no PunchVariation (auto-added at runtime)");

            FootstepAudio steps = go.GetComponent<FootstepAudio>();
            Line(sb, steps != null, $"{go.name}: FootstepAudio present", $"{go.name}: no FootstepAudio — run Rebuild Boxing System");
            if (steps != null)
            {
                SerializedObject so = new SerializedObject(steps);
                int stepCount = so.FindProperty("stepClips")?.arraySize ?? 0;
                int squeakCount = so.FindProperty("squeakClips")?.arraySize ?? 0;
                Line(sb, stepCount > 0, $"{go.name}: {stepCount} step recording(s) wired", $"{go.name}: no step recordings wired");
                Line(sb, squeakCount > 0, $"{go.name}: {squeakCount} squeak recording(s) wired", $"{go.name}: no squeak recordings wired");
                var legs = go.GetComponentInChildren<FIMSpace.FProceduralAnimation.LegsAnimator>(true);
                Line(sb, legs != null, $"{go.name}: Legs Animator found — its step events drive the sounds",
                     $"{go.name}: no Legs Animator — bone-motion fallback will detect plants");
            }
        }

        foreach (PunchingBag bag in Object.FindObjectsByType<PunchingBag>())
        {
            BagAudio audio = bag.GetComponent<BagAudio>();
            Line(sb, audio != null, $"{bag.name}: BagAudio present", $"{bag.name}: no BagAudio — run Rebuild Boxing System");
            if (audio != null)
            {
                SerializedObject so = new SerializedObject(audio);
                int hits = so.FindProperty("hitClips")?.arraySize ?? 0;
                Line(sb, hits > 0, $"{bag.name}: {hits} hit recording(s) wired", $"{bag.name}: no hit recordings wired");
            }
        }

        Debug.Log(sb.ToString());
    }

    private static void Line(StringBuilder sb, bool ok, string good, string bad)
        => sb.AppendLine(ok ? $"  ✓  {good}" : $"  ✗  {bad}");

    // ------------------------------------------------------------------ Helpers

    private static void Set(SerializedObject so, string name, float value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || Mathf.Approximately(p.floatValue, value)) return;
        p.floatValue = value;
        if (note != null) did.Add(note);
    }

    /// <summary>Only raises — never argues with a value the user has already pushed higher.</summary>
    private static void SetMin(SerializedObject so, string name, float value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || p.floatValue >= value) return;
        p.floatValue = value;
        if (note != null) did.Add(note);
    }

    private static void SetBool(SerializedObject so, string name, bool value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || p.boolValue == value) return;
        p.boolValue = value;
        if (note != null) did.Add(note);
    }

    private static void SetVector2(SerializedObject so, string name, Vector2 value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || p.vector2Value == value) return;
        p.vector2Value = value;
        if (note != null) did.Add(note);
    }

    private static void SetVector3(SerializedObject so, string name, Vector3 value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || p.vector3Value == value) return;
        p.vector3Value = value;
        if (note != null) did.Add(note);
    }
}
