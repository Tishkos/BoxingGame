using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A single-frame anatomical reference: "if the boxer were frozen at a technically clean guard / loaded /
/// straight / hook / uppercut, what shape is the body in?" Stored in humanoid MUSCLE space (Unity's rig-independent
/// pose representation, -1..1 per anatomical degree of freedom), so it blends linearly, retargets to any humanoid,
/// and never carries timing — it is a shape suggestion, never an attack animation.
///
/// Muscles are stored BY NAME (<see cref="muscleNames"/> / <see cref="muscleValues"/>) and resolved against
/// <see cref="HumanTrait.MuscleName"/> at runtime, so an asset can be produced by the in-editor baker or straight
/// from a one-frame clip's curves, and muscles a pose never authored (fingers, eyes…) are left to the animation.
/// </summary>
[CreateAssetMenu(menuName = "Boxer/Reference Pose", fileName = "Pose")]
public class ReferencePose : ScriptableObject
{
    public enum Category { Guard, Loaded, Straight, Hook, Uppercut, Slip, Stunned, Other, GuardBody }

    /// <summary>Where this strike pose is aimed: the head, the body, or low. Punches blend between the variants by aim height.</summary>
    public enum TargetHeight { Head = 0, Body = 1, Low = 2 }

    [Tooltip("Display name, e.g. RightHookHead.")]
    public string poseName;

    public Category category = Category.Other;

    [Tooltip("Which hand this pose is built around (ignored for Guard / Slip / Stunned).")]
    public BoxerPunchController.Hand hand = BoxerPunchController.Hand.Right;

    [Tooltip("Aim height of a strike pose (Head / Body / Low). Ignored for guards and Loaded.")]
    public TargetHeight height = TargetHeight.Body;

    [Tooltip("LOADED poses only: which rung of the wind-up ladder this is, 0 = lowest … 3 = highest. The mixer " +
             "blends between the rungs by where the punch is aimed, so a load for a body shot and a load for a " +
             "head shot are different authored shapes. -1 = not a ladder pose.")]
    public int loadLevel = -1;

    [Header("Source (for re-baking)")]
    public AnimationClip sourceClip;
    public float sourceTime;

    [Header("Metadata")]
    [Tooltip("How extended the striking arm is in this pose: 0 = at guard, 1 = fully straight. Filled by the baker.")]
    [Range(0f, 1f)] public float extension;

    [Tooltip("Chest yaw relative to the hips in this pose (degrees, + = striking side forward). Filled by the baker.")]
    public float chestTwist;

    // Muscle values by HumanTrait muscle name. Parallel arrays.
    [HideInInspector] public string[] muscleNames = new string[0];
    [HideInInspector] public float[] muscleValues = new float[0];

    // Legacy full array (HumanTrait.MuscleCount values, all authored). Still accepted.
    [HideInInspector] public float[] muscles = new float[0];

    [HideInInspector] public Vector3 bodyPosition;
    [HideInInspector] public Quaternion bodyRotation = Quaternion.identity;

    [NonSerialized] private float[] resolved;
    [NonSerialized] private bool[] authored;
    [NonSerialized] private int authoredCount;

    public bool IsValid =>
        (muscleNames != null && muscleValues != null && muscleNames.Length > 0 && muscleNames.Length == muscleValues.Length)
        || (muscles != null && muscles.Length == HumanTrait.MuscleCount);

    /// <summary>Muscle values in HumanTrait order (unauthored entries are 0 — check <see cref="Authored"/>).</summary>
    public float[] Resolved { get { EnsureResolved(); return resolved; } }

    /// <summary>Per muscle: did this pose author it? Unauthored muscles must be left to the animation.</summary>
    public bool[] Authored { get { EnsureResolved(); return authored; } }

    public int AuthoredCount { get { EnsureResolved(); return authoredCount; } }

    /// <summary>Force a re-resolve after editing the arrays (the baker calls this).</summary>
    public void Invalidate() => resolved = null;

    private void EnsureResolved()
    {
        if (resolved != null) return;

        int count = HumanTrait.MuscleCount;
        resolved = new float[count];
        authored = new bool[count];
        authoredCount = 0;

        if (muscleNames != null && muscleValues != null && muscleNames.Length > 0 && muscleNames.Length == muscleValues.Length)
        {
            Dictionary<string, int> index = MuscleIndex;
            List<string> unknown = null;
            for (int i = 0; i < muscleNames.Length; i++)
            {
                if (index.TryGetValue(muscleNames[i], out int m))
                {
                    resolved[m] = muscleValues[i];
                    if (!authored[m]) { authored[m] = true; authoredCount++; }
                }
                else
                {
                    if (unknown == null) unknown = new List<string>();
                    unknown.Add(muscleNames[i]);
                }
            }
            if (unknown != null)
                Debug.LogWarning($"ReferencePose '{name}': {unknown.Count} muscle name(s) not recognised and ignored: {string.Join(", ", unknown)}", this);
        }
        else if (muscles != null && muscles.Length == count)
        {
            Array.Copy(muscles, resolved, count);
            for (int m = 0; m < count; m++) authored[m] = true;
            authoredCount = count;
        }
    }

    private static Dictionary<string, int> muscleIndex;
    private static Dictionary<string, int> MuscleIndex
    {
        get
        {
            if (muscleIndex != null) return muscleIndex;
            muscleIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] names = HumanTrait.MuscleName;
            for (int i = 0; i < names.Length; i++) muscleIndex[names[i]] = i;
            return muscleIndex;
        }
    }
}
