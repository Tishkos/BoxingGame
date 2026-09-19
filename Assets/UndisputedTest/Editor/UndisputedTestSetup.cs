using System.Collections.Generic;
using System.IO;
using System.Linq;
using Animancer;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds a ready-to-play test rig: Bennett, an <see cref="AnimancerComponent"/>, and
/// the clips from Assets/UndisputedTest/Clips wired into <see cref="UndisputedClipTester"/>.
/// </summary>
public static class UndisputedTestSetup
{
    private const string ClipFolder = "Assets/UndisputedTest/Clips";
    private const string LibraryFolder = "Assets/Animations/Undisputed";
    private const string BennettPath = "Assets/Character/Bennett.fbx";
    private const string RigName = "Bennett_ClipTest";

    /// <summary>
    /// One clip per category, so the fallback demo covers the whole moveset rather than
    /// nine variations of the same hit reaction.
    /// </summary>
    private static readonly string[] DemoCategories =
    {
        "Orthodox/Idle",
        "Orthodox/Movement",
        "Orthodox/Punches/Jab",
        "Orthodox/Punches/Hook",
        "Orthodox/Punches/Uppercut",
        "Orthodox/Dodge",
        "Orthodox/HitReaction",
        "Orthodox/Knockdown",
        "Orthodox/Taunt",
    };

    [MenuItem("Tools/Undisputed/Build Clip Test Rig")]
    public static void Build()
    {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(BennettPath);
        if (model == null)
        {
            Debug.LogError("Undisputed test: could not find " + BennettPath);
            return;
        }

        GameObject existing = GameObject.Find(RigName);
        if (existing != null)
            Object.DestroyImmediate(existing);

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        instance.name = RigName;
        instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogError("Undisputed test: Bennett has no Animator. Check the FBX rig import settings.");
            return;
        }

        if (animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogError("Undisputed test: Bennett's avatar is not Humanoid (" +
                (animator.avatar == null ? "no avatar" : animator.avatar.name) +
                "). The ripped muscle clips cannot retarget without one.");
        }

        // Animancer replaces the controller entirely, so clear it to avoid a fight.
        animator.runtimeAnimatorController = null;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        GameObject host = animator.gameObject;
        AnimancerComponent animancer = host.GetComponent<AnimancerComponent>();
        if (animancer == null)
            animancer = host.AddComponent<AnimancerComponent>();
        animancer.Animator = animator;

        UndisputedClipTester tester = host.GetComponent<UndisputedClipTester>();
        if (tester == null)
            tester = host.AddComponent<UndisputedClipTester>();

        List<AnimationClip> clips = LoadClips();
        if (clips.Count == 0)
        {
            Debug.LogError("Undisputed test: no clips found in " + ClipFolder + " or " + LibraryFolder);
            return;
        }

        tester.Clips = clips;
        tester.FadeDuration = 0.25f;
        tester.Speed = 1f;
        tester.ApplyRootMotion = false;

        // Idle first so it becomes both key 1 and the WASD fallback.
        AnimationClip idle = Pick(clips, "Idle");
        if (idle != null)
        {
            clips.Remove(idle);
            clips.Insert(0, idle);
        }

        // Any footwork clip will do - this only has to prove fading works on held keys.
        tester.Forward = Pick(clips, "Forward_Back", "Forwad_Back", "Movement", "Steps");
        tester.Back = tester.Forward;
        tester.Left = Pick(clips, "Sides", "Circles", "Corners", "Weave", "Dodge");
        if (tester.Left == null)
            tester.Left = tester.Forward;
        tester.Right = tester.Left;

        EditorUtility.SetDirty(tester);
        EditorUtility.SetDirty(animancer);
        EditorSceneManager.MarkSceneDirty(instance.scene);
        Selection.activeGameObject = instance;

        Debug.Log("Undisputed test: built '" + RigName + "' with " + clips.Count +
            " clip(s). Avatar = " + (animator.avatar != null ? animator.avatar.name : "none") +
            ". Press Play, then use 1-" + Mathf.Min(clips.Count, 9) + " or hold WASD.");

        foreach (AnimationClip clip in clips)
        {
            Debug.Log("  clip '" + clip.name + "'  " + clip.length.ToString("0.00") + "s  " +
                (clip.isHumanMotion ? "humanoid" : "NOT HUMANOID - will not retarget") +
                "  (" + clip.frameRate + " fps)", clip);
        }
    }

    [MenuItem("Tools/Undisputed/Log Clip Import Status")]
    public static void LogStatus()
    {
        List<AnimationClip> clips = LoadClips();
        if (clips.Count == 0)
        {
            Debug.LogError("Undisputed test: no clips found in " + ClipFolder + " or " + LibraryFolder +
                ". Let Unity finish importing, then try again.");
            return;
        }

        int humanoid = clips.Count(clip => clip.isHumanMotion);
        Debug.Log("Undisputed test: " + clips.Count + " clip(s), " + humanoid + " humanoid.");

        foreach (AnimationClip clip in clips)
        {
            Debug.Log("  '" + clip.name + "'  " + clip.length.ToString("0.00") + "s  " +
                clip.frameRate + " fps  " +
                (clip.isHumanMotion ? "humanoid" : "NOT HUMANOID") +
                "  empty=" + clip.empty, clip);
        }
    }

    private static List<AnimationClip> LoadClips()
    {
        // The scratch folder wins while it exists, so an in-progress test keeps its clips.
        List<AnimationClip> clips = LoadFrom(ClipFolder);
        if (clips.Count > 0)
        {
            clips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return clips;
        }

        // Otherwise take the shortest clip from each category of the organised library.
        // Shortest because the long takes run 30-140s, which is useless for a smoke test.
        foreach (string category in DemoCategories)
        {
            List<AnimationClip> candidates = LoadFrom(LibraryFolder + "/" + category);
            if (candidates.Count == 0)
                continue;

            candidates.Sort((a, b) => a.length.CompareTo(b.length));
            clips.Add(candidates[0]);
        }

        return clips;
    }

    private static List<AnimationClip> LoadFrom(string folder)
    {
        var clips = new List<AnimationClip>();

        if (!Directory.Exists(folder))
            return clips;

        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip != null)
                clips.Add(clip);
        }

        return clips;
    }

    private static AnimationClip Pick(List<AnimationClip> clips, params string[] keywords)
    {
        foreach (string keyword in keywords)
        {
            foreach (AnimationClip clip in clips)
            {
                if (clip.name.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return clip;
            }
        }

        return null;
    }
}
