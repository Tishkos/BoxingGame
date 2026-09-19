using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Rewired integration for the boxer, without a hard dependency on Rewired at compile time:
///  • Tools ▸ Boxer ▸ Rewired ▸ Enable Rewired Backend — puts a "Rewired Input Manager" in the scene if there is none,
///    adds the BOXER_REWIRED scripting define (which compiles the Rewired path in BoxerInput), and prints the exact
///    actions to create in the Rewired Editor together with the PS4 / Xbox / keyboard mapping.
///  • Tools ▸ Boxer ▸ Rewired ▸ Disable Rewired Backend — removes the define (the Input System backend takes over).
/// </summary>
public static class BoxerRewiredSetup
{
    private const string Define = "BOXER_REWIRED";
    private const string EnableMenu = "Tools/Boxer/Rewired/Enable Rewired Backend";
    private const string DisableMenu = "Tools/Boxer/Rewired/Disable Rewired Backend";
    private const string InstructionsMenu = "Tools/Boxer/Rewired/Print Action Mapping";

    private static Type InputManagerType => Type.GetType("Rewired.InputManager, Rewired_Core");
    private static bool RewiredInstalled => InputManagerType != null;

    [MenuItem(EnableMenu, true)]
    private static bool ValidateEnable() => RewiredInstalled;

    [MenuItem(EnableMenu)]
    private static void Enable()
    {
        if (!RewiredInstalled)
        {
            EditorUtility.DisplayDialog("Rewired", "Rewired is not installed in this project (Rewired_Core.dll not found).", "OK");
            return;
        }

        // A Rewired Input Manager must exist in the scene for ReInput to initialise.
        UnityEngine.Object existing = UnityEngine.Object.FindAnyObjectByType(InputManagerType);
        if (existing == null)
        {
            GameObject go = new GameObject("Rewired Input Manager");
            Undo.RegisterCreatedObjectUndo(go, "Create Rewired Input Manager");
            Undo.AddComponent(go, InputManagerType);
            Selection.activeGameObject = go;
            Debug.Log("Created 'Rewired Input Manager' in the scene. Open it in the Rewired Editor to add the actions below.", go);
        }

        SetDefine(true);
        PrintMapping();
    }

    [MenuItem(DisableMenu)]
    private static void Disable()
    {
        SetDefine(false);
        Debug.Log("Boxer: Rewired backend disabled — BoxerInput now uses the Unity Input System (DualShock/Xbox pads still work).");
    }

    [MenuItem(InstructionsMenu)]
    private static void PrintMapping()
    {
        string text =
            "Rewired setup for the boxer (Window ▸ Rewired ▸ Input Manager, select the 'Rewired Input Manager' object):\n" +
            "\n1) Actions (Actions tab ▸ Default category):\n" +
            "   Move Horizontal   (Axis)      Move Vertical   (Axis)\n" +
            "   Body Horizontal   (Axis)      Body Vertical   (Axis)\n" +
            "   Punch Left        (Button)    Punch Right     (Button)\n" +
            "   Block             (Button)    Sprint          (Button)\n" +
            "\n2) Controller Maps ▸ Joystick Maps ▸ Gamepad Template (covers PS4/PS5 and Xbox at once):\n" +
            "   Left Stick Horizontal  → Move Horizontal     Left Stick Vertical  → Move Vertical\n" +
            "   Right Stick Horizontal → Body Horizontal     Right Stick Vertical → Body Vertical\n" +
            "   Left Trigger (L2)      → Punch Left          Right Trigger (R2)   → Punch Right\n" +
            "   Left Shoulder (L1)     → Block               Right Shoulder (R1)  → Sprint\n" +
            "   (Right stick = lean/slip with no trigger held, the punching hand while L2/R2 is held. Swap the stick rows for BoxerInput.Layout = Swapped.)\n" +
            "\n3) Keyboard Map:  A/D → Move Horizontal (−/+), W/S → Move Vertical (+/−), Left Ctrl → Block, Left Shift → Sprint.\n" +
            "   Mouse Map:     Left Button → Punch Left, Right Button → Punch Right, Middle Button → Block.\n" +
            "   (The mouse cursor position drives Body when the keyboard/mouse is the active device.)\n" +
            "\n4) Players ▸ Player0: assign the Keyboard, Mouse and Joystick maps (Start Enabled) and tick 'Assign Joysticks'.\n" +
            "\nUntil every action exists, BoxerInput keeps using the Unity Input System, so nothing breaks while you set this up.";
        Debug.Log(text);
    }

    private static void SetDefine(bool enabled)
    {
        NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
        List<string> defines = PlayerSettings.GetScriptingDefineSymbols(target)
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).Where(d => d.Length > 0).ToList();

        bool has = defines.Contains(Define);
        if (enabled && !has) defines.Add(Define);
        if (!enabled && has) defines.Remove(Define);
        if (enabled == has) return;

        PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
        Debug.Log($"Boxer: scripting define {Define} {(enabled ? "added" : "removed")} for {target.TargetName}. Unity will recompile.");
    }
}
