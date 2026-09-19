using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One press and there is a FIGHT: an AI opponent — the same character, same rig, same Legs Animator, same pose
/// library, same punch physics, his own cloned ragdoll — who closes range, works combinations across the whole
/// pose library, guards, slips, gets hurt, goes down and can be counted out. Health and knockouts are switched
/// on for BOTH fighters.
///
///     Tools ▸ Boxer ▸ Fight ▸ Setup AI Opponent      — fight on next Play (P respawns him any time)
///     Tools ▸ Boxer ▸ Fight ▸ Bag Training Only      — back to hitting the bag, knockdowns off
///
/// Values are written explicitly onto the scene components (old serialized values would otherwise win).
/// </summary>
public static class BoxerFightSetup
{
    private const string SetupMenu = "Tools/Boxer/Fight/Setup AI Opponent";
    private const string TrainingMenu = "Tools/Boxer/Fight/Bag Training Only";

    [MenuItem(SetupMenu, true)]
    [MenuItem(TrainingMenu, true)]
    private static bool Validate() => Object.FindAnyObjectByType<BoxerPunchController>() != null;

    [MenuItem(SetupMenu)]
    private static void Setup()
    {
        BoxerPunchController boxer = FindPlayerBoxer();
        if (boxer == null) return;

        List<string> did = new List<string>();
        Undo.SetCurrentGroupName("Setup AI Opponent");
        int group = Undo.GetCurrentGroup();
        float scale = Mathf.Max(0.5f, boxer.transform.lossyScale.y);

        // --- The spawner: clones the player (Legs Animator and all) and gives the clone a brain + a body ---
        OpponentSpawner spawner = boxer.GetComponent<OpponentSpawner>();
        if (spawner == null)
        {
            spawner = Undo.AddComponent<OpponentSpawner>(boxer.gameObject);
            did.Add("added OpponentSpawner (clones the player — same rig, same Legs Animator, same punches)");
        }
        SerializedObject so = new SerializedObject(spawner);
        SetBool(so, "spawnOnStart", true, did, "opponent spawns on Play (press P to respawn)");
        SetFloat(so, "spawnDistance", 2.2f * scale, did, $"spawn distance {2.2f * scale:0.0} m (scaled to the character)");
        SetFloat(so, "skill", 0.5f, did, null);
        SetFloat(so, "aggression", 0.55f, did, null);
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(spawner);

        // --- A fight goes both ways: the player must be hittable ---
        if (boxer.GetComponent<BoxerHealth>() == null)
        {
            Undo.AddComponent<BoxerHealth>(boxer.gameObject);
            did.Add("added BoxerHealth to the player (jaw/head/body/liver zones, concussion, count)");
        }

        // --- Knockdowns on: hard shots put fighters on the floor for real ---
        BoxerPhysics physics = boxer.GetComponent<BoxerPhysics>();
        if (physics != null)
        {
            SerializedObject po = new SerializedObject(physics);
            SetBool(po, "allowKnockdown", true, did, "knockdowns ON — a caught chin or lost balance puts a fighter down");
            po.ApplyModifiedProperties();
            EditorUtility.SetDirty(physics);
        }

        Undo.CollapseUndoOperations(group);

        string done = did.Count > 0 ? "  • " + string.Join("\n  • ", did) : "  (already set up)";
        Debug.Log($"FIGHT NIGHT:\n{done}\n\nPress Play: he spawns in front of you and comes to work — full pose " +
                  "library, guards, slips, real gloves on his own ragdoll. Hurt him and he covers; drop him three " +
                  "times and he stays down. P respawns him; skill & aggression live on the OpponentSpawner.", boxer);
    }

    [MenuItem(TrainingMenu)]
    private static void Training()
    {
        BoxerPunchController boxer = FindPlayerBoxer();
        if (boxer == null) return;

        Undo.SetCurrentGroupName("Bag Training Only");
        OpponentSpawner spawner = boxer.GetComponent<OpponentSpawner>();
        if (spawner != null)
        {
            SerializedObject so = new SerializedObject(spawner);
            SetBool(so, "spawnOnStart", false, null, null);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(spawner);
        }

        BoxerPhysics physics = boxer.GetComponent<BoxerPhysics>();
        if (physics != null)
        {
            SerializedObject po = new SerializedObject(physics);
            SetBool(po, "allowKnockdown", false, null, null);
            po.ApplyModifiedProperties();
            EditorUtility.SetDirty(physics);
        }

        Debug.Log("Back to bag training: no opponent on Play (P still summons one), knockdowns off — nothing is " +
                  "less fun than your own balance putting you on the floor mid-drill.", boxer);
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>The human's boxer: the one with no BoxerAI on it.</summary>
    private static BoxerPunchController FindPlayerBoxer()
    {
        foreach (BoxerPunchController b in Object.FindObjectsByType<BoxerPunchController>())
            if (b.GetComponent<BoxerAI>() == null) return b;
        return Object.FindAnyObjectByType<BoxerPunchController>();
    }

    private static void SetBool(SerializedObject so, string name, bool value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || p.boolValue == value) return;
        p.boolValue = value;
        if (note != null) did?.Add(note);
    }

    private static void SetFloat(SerializedObject so, string name, float value, List<string> did, string note)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null || Mathf.Approximately(p.floatValue, value)) return;
        p.floatValue = value;
        if (note != null) did?.Add(note);
    }
}
