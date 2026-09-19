using System.Collections.Generic;
using System.Linq;
using System.Text;
using RootMotion.Dynamics;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Explains why a boxer is not behaving like the PuppetMaster demo dummy.
/// </summary>
/// <remarks>
/// Almost every "I applied the same thing and it does not work" turns out to be a wiring
/// problem rather than a settings problem, and it is invisible in the Inspector because the
/// objects share a name. <see cref="BoxerPhysics"/> claims a puppet by
/// <c>pm.targetRoot == transform</c> — one identity test, no fallback. Point a PuppetMaster at
/// a different copy of the character and the scripted one simply never gets a body: it logs
/// once and then runs IK-only punches that look nothing like the dummy.
///
/// This walks the scene and names the objects involved, so the mismatch is visible instead of
/// inferred.
/// </remarks>
public static class BoxerSceneDiagnose
{
    [MenuItem("Tools/Undisputed/Boxing/Diagnose Scene")]
    public static void Diagnose()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BOXER SCENE DIAGNOSIS");
        sb.AppendLine();

        Animator[] humanoids = Object.FindObjectsByType<Animator>(FindObjectsSortMode.None)
            .Where(a => a.avatar != null && a.avatar.isHuman)
            .ToArray();

        PuppetMaster[] puppets = Object.FindObjectsByType<PuppetMaster>(FindObjectsSortMode.None);
        BoxerPunchController[] boxers = Object.FindObjectsByType<BoxerPunchController>(FindObjectsSortMode.None);

        int problems = 0;

        /* ---------------------------------------------------------------- characters */

        sb.AppendLine($"HUMANOID CHARACTERS: {humanoids.Length}");
        foreach (Animator a in humanoids)
        {
            bool hasController = a.GetComponent<BoxerPunchController>() != null;
            sb.AppendLine($"   {Path(a.transform)}");
            sb.AppendLine($"      controller {(hasController ? "YES" : "no")}" +
                          $"   physics {(a.GetComponent<BoxerPhysics>() != null ? "BoxerPhysics" : a.GetComponent<BoxerPhysicsBody>() != null ? "BoxerPhysicsBody (legacy)" : "none")}" +
                          $"   FBBIK {(a.GetComponent<FullBodyBipedIK>() != null ? "yes" : "no")}" +
                          $"   updateMode {a.updateMode}");

            PuppetMaster owned = puppets.FirstOrDefault(p => p.targetRoot == a.transform);
            sb.AppendLine($"      puppet aimed at it: {(owned != null ? Path(owned.transform) : "NONE")}");

            if (hasController && owned == null)
            {
                problems++;
                sb.AppendLine("      *** PROBLEM: this is the scripted boxer but NO PuppetMaster targets it.");
                sb.AppendLine("          BoxerPhysics matches on pm.targetRoot == transform, so it will log");
                sb.AppendLine("          'no PuppetMaster with muscles found' and fall back to IK-only punches.");
            }
        }

        if (humanoids.Length > boxers.Length)
        {
            problems++;
            sb.AppendLine();
            sb.AppendLine($"*** PROBLEM: {humanoids.Length} humanoid characters but only {boxers.Length} " +
                          "with a BoxerPunchController.");
            sb.AppendLine("    Spare copies are the usual cause of a PuppetMaster driving the wrong one, and");
            sb.AppendLine("    every setup tool uses FindAnyObjectByType, so they can configure the wrong one too.");
            sb.AppendLine("    Delete the spares.");
        }

        /* ------------------------------------------------------------------ puppets */

        sb.AppendLine();
        sb.AppendLine($"PUPPETMASTERS: {puppets.Length}");
        foreach (PuppetMaster p in puppets)
        {
            int muscles = p.muscles != null ? p.muscles.Length : 0;
            sb.AppendLine($"   {Path(p.transform)}");
            sb.AppendLine($"      targetRoot {(p.targetRoot != null ? Path(p.targetRoot) : "<NONE>")}" +
                          $"   muscles {muscles}   mode {p.mode}   state {p.state}");

            if (p.targetRoot == null)
            {
                problems++;
                sb.AppendLine("      *** PROBLEM: no targetRoot. This puppet drives nothing — delete it.");
            }
            else if (p.targetRoot.GetComponent<BoxerPunchController>() == null)
            {
                problems++;
                sb.AppendLine("      *** PROBLEM: its targetRoot has no BoxerPunchController, so this puppet");
                sb.AppendLine("          is animating a spare copy of the character. Either delete it, or");
                sb.AppendLine("          repoint targetRoot at the scripted boxer.");
            }

            if (muscles == 0)
                sb.AppendLine("      note: 0 muscles until Play — PuppetMaster builds them on Initiate.");
        }

        /* ----------------------------------------------------------------- the boxer */

        sb.AppendLine();
        sb.AppendLine($"BOXERS: {boxers.Length}");
        foreach (BoxerPunchController b in boxers)
        {
            SerializedObject so = new SerializedObject(b);
            string backend = Read(so, "physicsBackend") == "0" ? "PuppetMaster" : "SelfBuilt (legacy)";
            string shape = Read(so, "punchShape") switch
            {
                "0" => "Poses", "1" => "Clips", "2" => "IKOnly", _ => "?"
            };

            sb.AppendLine($"   {Path(b.transform)}");
            sb.AppendLine($"      backend {backend}   punch shape {shape}" +
                          $"   usePhysicsBody {Read(so, "usePhysicsBody")}");

            if (backend.StartsWith("SelfBuilt"))
            {
                problems++;
                sb.AppendLine("      *** PROBLEM: backend is SelfBuilt, so PuppetMaster is not driving this");
                sb.AppendLine("          boxer at all. Set Physics Backend = PuppetMaster.");
            }

            if (shape == "Clips")
            {
                bool empty = b.leftClips.IsEmpty && b.rightClips.IsEmpty;
                if (empty)
                {
                    problems++;
                    sb.AppendLine("      *** PROBLEM: Clips mode but no clips assigned — punches will be a bare");
                    sb.AppendLine("          IK reach. Run Use Undisputed Animations, then Setup Upper-Body Punch Rig.");
                }

                if (b.GetComponent<Animator>()?.runtimeAnimatorController == null)
                {
                    problems++;
                    sb.AppendLine("      *** PROBLEM: Clips mode needs an Animator Controller with the punch layer.");
                    sb.AppendLine("          Run Tools > Boxer > Setup Upper-Body Punch Rig.");
                }
            }

            if (b.GetComponent<ReferencePoseMixer>() != null && shape != "Poses")
                sb.AppendLine("      note: ReferencePoseMixer still attached; harmless (the controller disables it).");

            if (b.GetComponent<BoxerPhysicsBody>() != null && backend == "PuppetMaster")
                sb.AppendLine("      note: legacy BoxerPhysicsBody still attached; Setup Physics Boxer stands it " +
                              "down, so it is inert — remove it if you want the object tidy.");
        }

        sb.AppendLine();
        sb.AppendLine(problems == 0
            ? "No wiring problems found."
            : $"{problems} problem(s) above — fix the *** lines in order.");

        if (problems == 0)
            Debug.Log(sb.ToString());
        else
            Debug.LogWarning(sb.ToString());
    }

    private static string Read(SerializedObject so, string field)
    {
        SerializedProperty p = so.FindProperty(field);
        if (p == null)
            return "?";

        return p.propertyType switch
        {
            SerializedPropertyType.Enum => p.enumValueIndex.ToString(),
            SerializedPropertyType.Boolean => p.boolValue ? "1" : "0",
            SerializedPropertyType.Integer => p.intValue.ToString(),
            _ => p.ToString(),
        };
    }

    /// <summary>Full hierarchy path — three objects called "Bennett" are otherwise indistinguishable.</summary>
    private static string Path(Transform t)
    {
        if (t == null)
            return "<null>";

        var parts = new List<string>();
        for (Transform c = t; c != null; c = c.parent)
            parts.Add(c.name);

        parts.Reverse();
        return string.Join("/", parts);
    }
}
