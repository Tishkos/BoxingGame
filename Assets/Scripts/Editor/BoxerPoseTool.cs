using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools ▸ Boxer ▸ Pose Character (Edit Mode): stamp any animation clip's frame onto the character IN EDIT MODE,
/// no Play needed — and always come back: "Back To T-Pose" restores the exact humanoid T-pose (all muscles zero),
/// and a snapshot (auto-saved before the first stamp) restores whatever pose the scene had before.
///
/// Made for cloth/ragdoll authoring: MagicaCloth builds its proxy, paint and collisions from the pose the
/// character is standing in — author the shorts on the fighting stance, never on the T-pose (T-pose puts the
/// legs together, so the reduction welds the two shorts tubes into a skirt). Everything supports Ctrl+Z too.
/// </summary>
public class BoxerPoseTool : EditorWindow
{
    private GameObject character;
    private AnimationClip clip;
    private float time01;

    // Snapshot of a full pose (survives script reloads while the window is open).
    [SerializeField] private List<string> snapPaths = new List<string>();
    [SerializeField] private List<Vector3> snapPositions = new List<Vector3>();
    [SerializeField] private List<Quaternion> snapRotations = new List<Quaternion>();
    [SerializeField] private List<Vector3> snapScales = new List<Vector3>();
    [SerializeField] private string snapCharacterName;

    [MenuItem("Tools/Boxer/Pose Character (Edit Mode)")]
    private static void Open() => GetWindow<BoxerPoseTool>("Pose Character");

    private void OnEnable()
    {
        if (character == null)
        {
            BoxerPunchController boxer = FindAnyObjectByType<BoxerPunchController>();
            if (boxer != null) character = boxer.gameObject;
            else { Animator animator = FindAnyObjectByType<Animator>(); if (animator != null) character = animator.gameObject; }
        }
        if (clip == null && character != null)
        {
            Animator animator = character.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null && animator.runtimeAnimatorController.animationClips.Length > 0)
                clip = animator.runtimeAnimatorController.animationClips[0];   // usually the idle / stance
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Stamps a clip's frame onto the character in edit mode (no Play). Author MagicaCloth on the fighting " +
            "stance, not the T-pose. Coming back: 'Back To T-Pose' is exact (muscles = 0); 'Restore Snapshot' " +
            "returns to the pose from before your first stamp (auto-saved). Ctrl+Z works everywhere.",
            MessageType.Info);

        character = (GameObject)EditorGUILayout.ObjectField("Character", character, typeof(GameObject), true);
        clip = (AnimationClip)EditorGUILayout.ObjectField("Pose from clip", clip, typeof(AnimationClip), false);
        time01 = EditorGUILayout.Slider("Frame (0-1)", time01, 0f, 1f);

        using (new EditorGUI.DisabledScope(character == null || Application.isPlaying))
        {
            using (new EditorGUI.DisabledScope(clip == null))
            {
                if (GUILayout.Button("Apply Pose To Character", GUILayout.Height(30)))
                    ApplyPose();
            }

            GUILayout.Space(8);
            GUILayout.Label("Going back", EditorStyles.boldLabel);
            if (GUILayout.Button("Back To T-Pose", GUILayout.Height(24)))
                BackToTPose();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save Snapshot"))
                    SaveSnapshot(true);
                using (new EditorGUI.DisabledScope(snapPaths.Count == 0))
                {
                    string label = snapPaths.Count == 0 ? "Restore Snapshot (none saved)" : $"Restore Snapshot ({snapCharacterName})";
                    if (GUILayout.Button(label))
                        RestoreSnapshot();
                }
            }
        }
        if (Application.isPlaying)
            EditorGUILayout.HelpBox("Exit Play mode first — this tool is for edit-mode posing.", MessageType.Warning);
    }

    private void ApplyPose()
    {
        if (snapPaths.Count == 0) SaveSnapshot(false);   // first stamp: remember what the scene looked like
        Undo.RegisterFullObjectHierarchyUndo(character, "Apply Pose " + clip.name);
        clip.SampleAnimation(character, Mathf.Clamp01(time01) * Mathf.Max(0.0001f, clip.length));
        Dirty();
        Debug.Log($"BoxerPoseTool: posed '{character.name}' from '{clip.name}' at t={time01:0.00}. " +
                  "Save the scene to keep it; rebuild the MagicaCloth proxy so it is authored on this pose.", character);
    }

    /// <summary>A humanoid with every muscle at zero IS the T-pose — exact, no snapshot needed.</summary>
    private void BackToTPose()
    {
        Animator animator = character.GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogWarning("BoxerPoseTool: Back To T-Pose needs a Humanoid avatar on the character.", character);
            return;
        }
        Undo.RegisterFullObjectHierarchyUndo(character, "Back To T-Pose");
        HumanPoseHandler handler = new HumanPoseHandler(animator.avatar, character.transform);
        HumanPose pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        for (int i = 0; i < pose.muscles.Length; i++) pose.muscles[i] = 0f;
        handler.SetHumanPose(ref pose);                     // keeps her position in the scene; only the shape resets
        handler.Dispose();
        Dirty();
        Debug.Log($"BoxerPoseTool: '{character.name}' back to T-pose.", character);
    }

    private void SaveSnapshot(bool announce)
    {
        snapPaths.Clear(); snapPositions.Clear(); snapRotations.Clear(); snapScales.Clear();
        foreach (Transform t in character.GetComponentsInChildren<Transform>(true))
        {
            snapPaths.Add(AnimationUtility.CalculateTransformPath(t, character.transform));
            snapPositions.Add(t.localPosition);
            snapRotations.Add(t.localRotation);
            snapScales.Add(t.localScale);
        }
        snapCharacterName = character.name;
        if (announce) Debug.Log($"BoxerPoseTool: snapshot of '{character.name}' saved ({snapPaths.Count} transforms).", character);
    }

    private void RestoreSnapshot()
    {
        Undo.RegisterFullObjectHierarchyUndo(character, "Restore Pose Snapshot");
        var lookup = new Dictionary<string, Transform>();
        foreach (Transform t in character.GetComponentsInChildren<Transform>(true))
            lookup[AnimationUtility.CalculateTransformPath(t, character.transform)] = t;

        int applied = 0;
        for (int i = 0; i < snapPaths.Count; i++)
        {
            if (!lookup.TryGetValue(snapPaths[i], out Transform t)) continue;
            t.localPosition = snapPositions[i];
            t.localRotation = snapRotations[i];
            t.localScale = snapScales[i];
            applied++;
        }
        Dirty();
        Debug.Log($"BoxerPoseTool: restored snapshot onto '{character.name}' ({applied}/{snapPaths.Count} transforms).", character);
    }

    private void Dirty()
    {
        EditorSceneManager.MarkSceneDirty(character.scene);
        SceneView.RepaintAll();
    }
}
