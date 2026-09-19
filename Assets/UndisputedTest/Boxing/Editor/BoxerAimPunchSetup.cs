using RootMotion.Dynamics;
using RootMotion.FinalIK;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds and configures <see cref="BoxerAimPunch"/> — two AimIK chains that point the fists at
/// a target instead of the controller sliding the hand sideways onto it.
/// </summary>
public static class BoxerAimPunchSetup
{
    private const string SetupMenu = "Tools/Undisputed/Boxing/Setup Aim Punch IK";
    private const string RemoveMenu = "Tools/Undisputed/Boxing/Remove Aim Punch IK";

    [MenuItem(SetupMenu, true)]
    [MenuItem(RemoveMenu, true)]
    private static bool Validate() => Find() != null;

    private static BoxerPunchController Find()
    {
        if (Selection.activeGameObject != null)
        {
            var selected = Selection.activeGameObject.GetComponentInParent<BoxerPunchController>();
            if (selected != null)
                return selected;
        }

        return Object.FindAnyObjectByType<BoxerPunchController>();
    }

    [MenuItem(SetupMenu)]
    private static void Setup()
    {
        BoxerPunchController boxer = Find();
        if (boxer == null)
        {
            Debug.LogError("Aim Punch IK: no BoxerPunchController in the scene.");
            return;
        }

        Undo.SetCurrentGroupName("Setup Aim Punch IK");
        int group = Undo.GetCurrentGroup();

        BoxerAimPunch aim = boxer.GetComponent<BoxerAimPunch>();
        if (aim == null)
            aim = Undo.AddComponent<BoxerAimPunch>(boxer.gameObject);

        if (!aim.Configure())
            return;

        // Point at whatever is already in the scene rather than making the user hunt for it.
        if (aim.Target == null)
        {
            var marker = Object.FindFirstObjectByType<BoxingTarget>();
            if (marker != null)
            {
                aim.Target = marker.transform;
            }
            else
            {
                var bag = Object.FindFirstObjectByType<PunchingBag>();
                if (bag != null)
                    aim.Target = bag.transform;
            }
        }

        // PuppetMaster collects SolverManagers from its target root when it initiates. Adding
        // them now means they are present before that happens, which is the quiet requirement.
        PuppetMaster puppet = null;
        foreach (PuppetMaster pm in Object.FindObjectsByType<PuppetMaster>(FindObjectsSortMode.None))
        {
            if (pm.targetRoot == boxer.transform)
            {
                puppet = pm;
                break;
            }
        }

        EditorUtility.SetDirty(aim);
        Undo.CollapseUndoOperations(group);

        Debug.Log($"Aim Punch IK: configured on '{boxer.name}'.\n" +
            $"   left  chain {Describe(aim.LeftAim)}\n" +
            $"   right chain {Describe(aim.RightAim)}\n" +
            $"   target: {(aim.Target != null ? aim.Target.name : "none — falls back to the controller's AimPoint")}\n" +
            $"   puppet: {(puppet != null ? puppet.name + " (solvers collected on Initiate)" : "none found — AimIK will solve in LateUpdate instead")}",
            aim);

        if (aim.Target == null)
            Debug.LogWarning("Aim Punch IK: no BoxingTarget or PunchingBag in the scene, so the " +
                "aim falls back to the controller's own AimPoint. Assign Target for a fixed thing to hit.", aim);
    }

    private static string Describe(AimIK ik)
    {
        if (ik == null || ik.solver == null)
            return "<missing>";

        var names = new System.Text.StringBuilder();
        for (int i = 0; i < ik.solver.bones.Length; i++)
        {
            if (i > 0)
                names.Append(" → ");
            names.Append(ik.solver.bones[i].transform != null ? ik.solver.bones[i].transform.name : "?");
            names.Append($"({ik.solver.bones[i].weight:0.00})");
        }

        string fist = ik.solver.transform != null ? ik.solver.transform.name : "?";
        return $"{names}   aiming '{fist}' along {ik.solver.axis}";
    }

    [MenuItem(RemoveMenu)]
    private static void Remove()
    {
        BoxerPunchController boxer = Find();
        if (boxer == null)
            return;

        BoxerAimPunch aim = boxer.GetComponent<BoxerAimPunch>();
        if (aim != null)
        {
            foreach (AimIK ik in new[] { aim.LeftAim, aim.RightAim })
            {
                if (ik != null)
                    Undo.DestroyObjectImmediate(ik);
            }

            Undo.DestroyObjectImmediate(aim);
            Debug.Log("Aim Punch IK: removed.");
        }
    }
}
