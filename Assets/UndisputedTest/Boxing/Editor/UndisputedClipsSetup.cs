using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Switches the boxer to real animation: fills the punch clip slots from the Undisputed
/// library, measures each clip's strike frame, and puts the controller in
/// <c>PunchShape.Clips</c>.
/// </summary>
/// <remarks>
/// In Clips mode the controller disables <see cref="ReferencePoseMixer"/> outright — "the clips
/// (or nothing) own the body; the pose mixer must not touch a single muscle" — so no synthesised
/// chamber, no anti-strike extrapolation, no pose blending. The clip is the punch. The
/// trajectory still owns the CLOCK: it scrubs the clip so the wind-up lasts as long as you hold
/// and the strike lands exactly when the fist arrives.
///
/// That scrubbing is why the strike frame has to be right per clip, and why this measures rather
/// than trusts the single global value. See <see cref="PunchClipStrikeTimes"/>.
/// </remarks>
public static class UndisputedClipsSetup
{
    private const string PunchRoot = "Assets/Animations/Undisputed/Orthodox/Punches";
    private const string TablePath = "Assets/UndisputedTest/Boxing/PunchClipStrikeTimes.asset";

    private static readonly Regex Tagged = new Regex(
        @"OptimizedFor(?<target>Head|Body)_(?<hand>[LR])(?<type>Jab|Hook|Uppercut)",
        RegexOptions.IgnoreCase);

    private static readonly Regex Loose = new Regex(
        @"(?<hand>[LR])[_ ]?(?<type>Jab|Hook|Uppercut|Straight)", RegexOptions.IgnoreCase);

    private static readonly string[] Exclude = { "lowblow", "foul", "block", "dash" };

    private sealed class Entry
    {
        public AnimationClip Clip;
        public bool Left;
        public string Type;        // Jab | Hook | Uppercut
        public bool BodyShot;
        public float Strike;
        public float Extension;
    }

    /************************************************************************************/

    [MenuItem("Tools/Undisputed/Boxing/Use Undisputed Animations (Clips mode)")]
    public static void Setup()
    {
        BoxerPunchController boxer = Object.FindAnyObjectByType<BoxerPunchController>();
        if (boxer == null)
        {
            Debug.LogError("Undisputed clips: no BoxerPunchController in the scene.");
            return;
        }

        Animator animator = boxer.GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogError("Undisputed clips: the boxer needs a Humanoid Animator.", boxer);
            return;
        }

        List<Entry> entries = Collect();
        if (entries.Count == 0)
        {
            Debug.LogError("Undisputed clips: no punch clips under " + PunchRoot);
            return;
        }

        Measure(animator, entries);

        // Head first, then body: SelectClip indexes these arrays by aim height, high to low.
        // Within a height, the most extended clip first — that is the cleanest read of the punch.
        var table = new List<PunchClipStrikeTimes.Entry>();
        int assigned = 0;

        foreach (bool left in new[] { true, false })
        {
            var hand = left ? boxer.leftClips : boxer.rightClips;

            foreach (var pair in new[]
            {
                (BoxerPunchController.PunchType.Straight, "Jab"),
                (BoxerPunchController.PunchType.Hook, "Hook"),
                (BoxerPunchController.PunchType.Uppercut, "Uppercut"),
            })
            {
                AnimationClip[] ordered = entries
                    .Where(e => e.Left == left && e.Type == pair.Item2)
                    .OrderBy(e => e.BodyShot)                 // head (false) before body (true)
                    .ThenByDescending(e => e.Extension)
                    .Select(e => e.Clip)
                    .ToArray();

                hand.Set(pair.Item1, ordered);
                assigned += ordered.Length;
            }

            // Their Body type is "a straight at body height" — give it the body shots so a punch
            // promoted to Body still finds animation instead of borrowing a head clip.
            AnimationClip[] body = entries
                .Where(e => e.Left == left && e.BodyShot)
                .OrderByDescending(e => e.Extension)
                .Select(e => e.Clip)
                .ToArray();

            hand.Set(BoxerPunchController.PunchType.Body, body);
        }

        foreach (Entry e in entries)
            table.Add(new PunchClipStrikeTimes.Entry { Clip = e.Clip, Strike = e.Strike });

        WriteTable(table, boxer);
        SetClipsMode(boxer);
        RemovePoseMixer(boxer);

        EditorUtility.SetDirty(boxer);
        AssetDatabase.SaveAssets();

        Report(boxer, entries, assigned);
    }


    /************************************************************************************/

    /// <summary>Seconds the drive takes at profile speed 1 — PunchTimeline's curve integrates to this.</summary>
    private const float DriveAtSpeedOne = 0.16f;

    [MenuItem("Tools/Undisputed/Boxing/Match Punch Timing To Clips")]
    public static void MatchTiming()
    {
        BoxerPunchController boxer = Object.FindAnyObjectByType<BoxerPunchController>();
        if (boxer == null)
        {
            Debug.LogError("Punch timing: no BoxerPunchController in the scene.");
            return;
        }

        Animator animator = boxer.GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogError("Punch timing: the boxer needs a Humanoid Animator.", boxer);
            return;
        }

        List<Entry> entries = Collect();
        if (entries.Count == 0)
        {
            Debug.LogError("Punch timing: no punch clips under " + PunchRoot);
            return;
        }

        Measure(animator, entries);

        // Orthodox leads with the left, so the lead straight is the left jab and the rear
        // straight is the right cross. These are genuinely different lengths in the mocap.
        var groups = new (string field, System.Func<Entry, bool> match)[]
        {
            ("straightLead", e => e.Type == "Jab" && !e.BodyShot && e.Left),
            ("straightRear", e => e.Type == "Jab" && !e.BodyShot && !e.Left),
            ("hook",         e => e.Type == "Hook" && !e.BodyShot),
            ("uppercut",     e => e.Type == "Uppercut" && !e.BodyShot),
            ("body",         e => e.BodyShot),
        };

        SerializedObject so = new SerializedObject(boxer);
        var report = new List<string>();

        foreach (var g in groups)
        {
            List<Entry> set = entries.Where(g.match).ToList();
            if (set.Count == 0)
                continue;

            float length = Median(set.Select(e => e.Clip.length));
            float strike = Median(set.Select(e => e.Strike));

            float windUp = Mathf.Max(0.05f, strike * length);          // clip time before the strike
            float follow = Mathf.Max(0.05f, (1f - strike) * length);   // clip time after it

            SerializedProperty prop = so.FindProperty(g.field);
            if (prop == null)
            {
                Debug.LogWarning($"Punch timing: no profile field '{g.field}'.", boxer);
                continue;
            }

            SerializedProperty speed = prop.FindPropertyRelative("speed");
            SerializedProperty recover = prop.FindPropertyRelative("recover");

            float wasSpeed = speed != null ? speed.floatValue : 1f;
            float wasRecover = recover != null ? recover.floatValue : 0f;

            // The drive lasts DriveAtSpeedOne / speed, so this stretches it onto the wind-up.
            float newSpeed = Mathf.Clamp(DriveAtSpeedOne / windUp, 0.1f, 4f);

            if (speed != null) speed.floatValue = newSpeed;
            if (recover != null) recover.floatValue = follow;

            report.Add($"   {g.field,-14} n={set.Count,2}  clip {length:0.00}s  strike {strike:0.00}   " +
                       $"speed {wasSpeed:0.00} -> {newSpeed:0.00}   recover {wasRecover:0.00} -> {follow:0.00}");
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(boxer);

        Debug.Log("Punch timing matched to the Undisputed clips:\n" + string.Join("\n", report) +
            "\n\nThe drive curve integrates to ~0.16 s at speed 1, while the median Undisputed " +
            "wind-up is 0.36 s — the clips were being scrubbed about 2.2x too fast, which is why " +
            "they looked rushed. If this now feels sluggish, raise every profile's Speed by the " +
            "same factor (x1.3 keeps the relative feel and takes ~25% off each punch).", boxer);
    }

    private static float Median(IEnumerable<float> values)
    {
        List<float> list = values.OrderBy(v => v).ToList();
        if (list.Count == 0)
            return 0f;
        return list.Count % 2 == 1
            ? list[list.Count / 2]
            : (list[list.Count / 2 - 1] + list[list.Count / 2]) * 0.5f;
    }

    /************************************************************************************/

    private static List<Entry> Collect()
    {
        var list = new List<Entry>();

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { PunchRoot }))
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(guid));
            if (clip == null)
                continue;

            string lower = clip.name.ToLowerInvariant();
            if (Exclude.Any(bad => lower.Contains(bad)))
                continue;

            string hand, type;
            bool bodyShot;

            Match m = Tagged.Match(clip.name);
            if (m.Success)
            {
                hand = m.Groups["hand"].Value;
                type = m.Groups["type"].Value;
                bodyShot = m.Groups["target"].Value.Equals("Body", System.StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Match l = Loose.Match(clip.name);
                if (!l.Success)
                    continue;

                hand = l.Groups["hand"].Value;
                type = l.Groups["type"].Value;
                bodyShot = lower.Contains("body");
            }

            if (type.Equals("Straight", System.StringComparison.OrdinalIgnoreCase))
                type = "Jab";

            list.Add(new Entry
            {
                Clip = clip,
                Left = hand.ToUpperInvariant() == "L",
                Type = char.ToUpperInvariant(type[0]) + type.Substring(1).ToLowerInvariant(),
                BodyShot = bodyShot,
            });
        }

        return list;
    }

    /// <summary>Sweeps each clip for peak arm extension — that frame is the strike.</summary>
    private static void Measure(Animator animator, List<Entry> entries)
    {
        GameObject go = animator.gameObject;

        AnimationMode.StartAnimationMode();
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                EditorUtility.DisplayProgressBar("Measuring punch clips",
                    e.Clip.name, (i + 1f) / entries.Count);

                float length = Mathf.Max(0.01f, e.Clip.length);
                int steps = Mathf.Clamp(Mathf.RoundToInt(length * 60f), 12, 240);
                float bestT = 0.4f, bestExt = -1f;

                for (int s = 0; s <= steps; s++)
                {
                    float t01 = s / (float)steps;

                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(go, e.Clip, t01 * length);
                    AnimationMode.EndSampling();

                    float ext = Extension(animator, e.Left);
                    if (ext > bestExt)
                    {
                        bestExt = ext;
                        bestT = t01;
                    }
                }

                e.Strike = bestT;
                e.Extension = Mathf.Max(0f, bestExt);
            }
        }
        finally
        {
            AnimationMode.StopAnimationMode();
            EditorUtility.ClearProgressBar();
        }
    }

    private static float Extension(Animator animator, bool left)
    {
        Transform upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        Transform lower = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        Transform wrist = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);

        if (upper == null || lower == null || wrist == null)
            return 0f;

        float arm = Vector3.Distance(upper.position, lower.position)
                  + Vector3.Distance(lower.position, wrist.position);

        return arm > 0.01f ? Mathf.Clamp01(Vector3.Distance(upper.position, wrist.position) / arm) : 0f;
    }

    /************************************************************************************/

    private static void WriteTable(List<PunchClipStrikeTimes.Entry> rows, BoxerPunchController boxer)
    {
        var table = AssetDatabase.LoadAssetAtPath<PunchClipStrikeTimes>(TablePath);
        if (table == null)
        {
            table = ScriptableObject.CreateInstance<PunchClipStrikeTimes>();
            AssetDatabase.CreateAsset(table, TablePath);
        }

        table.Entries = rows;
        table.Rebuild();
        EditorUtility.SetDirty(table);

        SerializedObject so = new SerializedObject(boxer);
        SerializedProperty prop = so.FindProperty("clipStrikeTimes");
        if (prop != null)
        {
            prop.objectReferenceValue = table;
            so.ApplyModifiedProperties();
        }
        else
        {
            Debug.LogWarning("Undisputed clips: could not find the 'clipStrikeTimes' field — " +
                "punches will fall back to the single global strike time.", boxer);
        }
    }

    private static void SetClipsMode(BoxerPunchController boxer)
    {
        SerializedObject so = new SerializedObject(boxer);
        SerializedProperty shape = so.FindProperty("punchShape");
        if (shape != null)
        {
            shape.enumValueIndex = (int)BoxerPunchController.PunchShape.Clips;
            so.ApplyModifiedProperties();
        }
        else
        {
            Debug.LogWarning("Undisputed clips: could not find 'punchShape'. Set Punch Shape to " +
                "Clips on the controller by hand.", boxer);
        }
    }

    /// <summary>
    /// Takes the pose mixer off the boxer entirely.
    /// </summary>
    /// <remarks>
    /// Clips mode already disables and nulls it at Awake, so this is belt and braces — but it
    /// also means the component cannot be re-enabled by hand and quietly start fighting the
    /// clips again. Nothing declares RequireComponent on it, and the controller only auto-adds
    /// it inside the Poses branch, so it will not come back.
    /// </remarks>
    private static void RemovePoseMixer(BoxerPunchController boxer)
    {
        var mixer = boxer.GetComponent<ReferencePoseMixer>();
        if (mixer == null)
            return;

        Undo.DestroyObjectImmediate(mixer);
        Debug.Log("Undisputed clips: removed ReferencePoseMixer — the animation owns the body now.", boxer);
    }

    /************************************************************************************/

    [MenuItem("Tools/Undisputed/Boxing/Inventory (what is on the boxer)")]
    public static void Inventory()
    {
        BoxerPunchController boxer = Object.FindAnyObjectByType<BoxerPunchController>();
        if (boxer == null)
        {
            Debug.LogError("Inventory: no BoxerPunchController in the scene.");
            return;
        }

        // name -> (verdict, why)
        var verdicts = new Dictionary<string, (string, string)>
        {
            { "Animator",               ("KEEP",   "the rig itself") },
            { "BoxerPunchController",   ("KEEP",   "the punch brain") },
            { "BoxerPhysics",           ("KEEP",   "the PuppetMaster backend") },
            { "FullBodyBipedIK",        ("KEEP",   "places the fist; the controller drives it") },
            { "LookAtIK",               ("KEEP",   "head/spine onto the target, ordered by BoxerPhysics") },
            { "IKExecutionOrder",       ("KEEP",   "BoxerPhysics stands it down at runtime — harmless") },
            { "CharacterController",    ("KEEP",   "required by Controller — this is WASD") },
            { "Controller",             ("KEEP",   "movement and squaring up") },
            { "BoxerInput",             ("KEEP",   "input source") },
            { "BoxerHealth",            ("KEEP",   "zones, chin, the count") },
            { "BoxerBalance",           ("KEEP",   "centre of mass over the feet") },
            { "BoxerHitboxes",          ("KEEP",   "glove sensors (disabled when physical gloves are used)") },
            { "ImpactFeedback",         ("KEEP",   "hit feel") },
            { "BoxerVFX",               ("KEEP",   "the VFX") },
            { "PunchVariation",         ("KEEP",   "per-throw style") },
            { "PunchDebug",             ("KEEP",   "F9/F10 flight recorder") },
            { "GloveSquash",            ("KEEP",   "glove deformation") },
            { "FootstepAudio",          ("KEEP",   "footsteps") },
            { "BoxerSweat",             ("KEEP",   "cosmetic") },

            { "ReferencePoseMixer",     ("REMOVE", "THE POSES. Clips mode disables it anyway; take it off") },

            { "BoxerAimPunch",          ("OPTIONAL", "the AimIK aiming — keep if you want it") },
            { "AimIK",                  ("OPTIONAL", "driven by BoxerAimPunch") },

            { "BoxingController",       ("REMOVE", "my early gesture controller — superseded by BoxerPunchController") },
            { "BoxerStance",            ("REMOVE", "my early movement component — superseded by Controller") },
            { "UndisputedClipTester",   ("REMOVE", "the old Animancer test rig") },
            { "AnimancerComponent",     ("REMOVE", "Animancer is not used by this system") },
            { "BoxerLocomotion",        ("REMOVE", "my early blend-space rig — the Legs Animator owns movement") },
        };

        var lines = new List<string>();
        foreach (Component c in boxer.GetComponents<Component>())
        {
            if (c == null)
            {
                lines.Add("   <color=red>MISSING SCRIPT</color> — delete this slot");
                continue;
            }

            string n = c.GetType().Name;
            if (verdicts.TryGetValue(n, out var v))
                lines.Add($"   {v.Item1,-8} {n,-24} {v.Item2}");
            else
                lines.Add($"   {"?",-8} {n,-24} (not one I know — leave it unless it is yours to remove)");
        }

        Debug.Log($"Inventory of '{boxer.name}':\n" + string.Join("\n", lines) +
            "\n\nAnything marked REMOVE can be taken off with the component's " +
            "context menu > Remove Component.",
            boxer);
    }

    /************************************************************************************/

    private static void Report(BoxerPunchController boxer, List<Entry> entries, int assigned)
    {
        int off = entries.Count(e => Mathf.Abs(e.Strike - 0.4f) > 0.1f);

        Debug.Log($"Undisputed clips: {assigned} clip slot(s) filled from {entries.Count} punches, " +
            $"Punch Shape set to Clips. The reference pose mixer is now disabled by the controller — " +
            $"no poses touch the body.\n" +
            $"   strike frames measured per clip; {off}/{entries.Count} are more than 0.10 from the " +
            $"global 0.40, which is why the table exists.\n" +
            $"   NEXT: run Tools > Boxer > Setup Upper-Body Punch Rig to build the Animator states " +
            $"for these clips — without it there is no layer to scrub and punches fall back to IK.",
            boxer);

        foreach (var g in entries.GroupBy(e => (e.Left ? "L " : "R ") + e.Type + (e.BodyShot ? " Body" : " Head"))
                                 .OrderBy(g => g.Key))
        {
            Debug.Log($"   {g.Key,-16} x{g.Count()}   strike " +
                $"{g.Min(e => e.Strike):0.00}-{g.Max(e => e.Strike):0.00}");
        }
    }
}
