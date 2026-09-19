using Animancer;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the locomotion rig and reports what each blend space clip actually does, so a
/// clip sitting in the wrong slot shows up in the console instead of at runtime.
/// </summary>
public static class BoxerLocomotionSetup
{
    private const string Folder = "Assets/Animations/Undisputed/Locomotion";
    private const string BennettPath = "Assets/Character/Bennett.fbx";
    private const string RigName = "Bennett_Locomotion";

    [MenuItem("Tools/Undisputed/Build Locomotion Rig")]
    public static void Build()
    {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(BennettPath);
        if (model == null)
        {
            Debug.LogError("Locomotion: could not find " + BennettPath);
            return;
        }

        AnimationClip idle = Load("Move_Idle");
        AnimationClip forward = Load("Move_Forward");
        AnimationClip back = Load("Move_Back");
        AnimationClip left = Load("Move_Left");
        AnimationClip right = Load("Move_Right");

        if (idle == null || forward == null || back == null || left == null || right == null)
        {
            Debug.LogError("Locomotion: missing clips in " + Folder +
                ". Let Unity finish importing, then try again.");
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
            Debug.LogError("Locomotion: Bennett has no Animator.");
            return;
        }

        if (animator.avatar == null || !animator.avatar.isHuman)
            Debug.LogError("Locomotion: Bennett's avatar is not Humanoid; the clips cannot retarget.");

        animator.runtimeAnimatorController = null;
        animator.applyRootMotion = true;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        GameObject host = animator.gameObject;

        AnimancerComponent animancer = host.GetComponent<AnimancerComponent>();
        if (animancer == null)
            animancer = host.AddComponent<AnimancerComponent>();
        animancer.Animator = animator;

        // The clip tester would fight this one for the Animancer graph.
        UndisputedClipTester tester = host.GetComponent<UndisputedClipTester>();
        if (tester != null)
            Object.DestroyImmediate(tester);

        BoxerLocomotion loco = host.GetComponent<BoxerLocomotion>();
        if (loco == null)
            loco = host.AddComponent<BoxerLocomotion>();

        loco.Idle = idle;
        loco.Forward = forward;
        loco.Back = back;
        loco.Left = left;
        loco.Right = right;
        loco.ApplyRootMotion = true;
        loco.AllowManualTurn = true;
        loco.SpeedScale = 1f;

        loco.ForwardVelocity = Threshold("Forward");
        loco.BackVelocity = Threshold("Back");
        loco.LeftVelocity = Threshold("Left");
        loco.RightVelocity = Threshold("Right");

        EditorUtility.SetDirty(loco);
        EditorUtility.SetDirty(animancer);
        EditorSceneManager.MarkSceneDirty(instance.scene);
        Selection.activeGameObject = instance;

        Debug.Log("Locomotion: built '" + RigName + "'. Press Play, then use the left stick or WASD.");
        Report("Forward", forward, new Vector2(0, 1));
        Report("Back", back, new Vector2(0, -1));
        Report("Left", left, new Vector2(-1, 0));
        Report("Right", right, new Vector2(1, 0));
        Report("Idle", idle, Vector2.zero);
    }

    [MenuItem("Tools/Undisputed/Verify Locomotion Clip Directions")]
    public static void Verify()
    {
        Report("Forward", Load("Move_Forward"), new Vector2(0, 1));
        Report("Back", Load("Move_Back"), new Vector2(0, -1));
        Report("Left", Load("Move_Left"), new Vector2(-1, 0));
        Report("Right", Load("Move_Right"), new Vector2(1, 0));
        Report("Idle", Load("Move_Idle"), Vector2.zero);
    }

    /// <summary>
    /// Compares where the clip's root really goes against the slot it has been put in,
    /// using the baked body-relative velocity rather than <see cref="AnimationClip.averageSpeed"/>
    /// (which is clip-space and meaningless for these arbitrarily-oriented takes).
    /// </summary>
    private static void Report(string slot, AnimationClip clip, Vector2 wanted)
    {
        if (clip == null)
        {
            Debug.LogError("Locomotion: no clip for the " + slot + " slot.");
            return;
        }

        Vector2 flat = slot == "Idle" ? Vector2.zero : Threshold(slot);
        Vector3 raw = clip.averageSpeed;
        string common = $"{slot,-8} '{clip.name}'  {clip.length:0.00}s  " +
            $"body-relative ({flat.x,5:0.00},{flat.y,5:0.00}) = {flat.magnitude:0.00} m/s  " +
            $"[clip-space averageSpeed ({raw.x,5:0.00},{raw.z,5:0.00}) - ignored]  " +
            $"loop={clip.isLooping}  humanoid={clip.isHumanMotion}";

        if (wanted == Vector2.zero)
        {
            if (flat.magnitude > 0.15f)
                Debug.LogWarning("Locomotion: " + common + "   <- idle should sit still", clip);
            else
                Debug.Log("Locomotion: " + common, clip);
            return;
        }

        float angle = Vector2.Angle(flat.normalized, wanted);
        if (flat.magnitude < 0.1f)
            Debug.LogWarning("Locomotion: " + common + "   <- barely moves", clip);
        else if (angle > 45f)
            Debug.LogError("Locomotion: " + common + $"   <- travels {angle:0}d off {slot}", clip);
        else
            Debug.Log("Locomotion: " + common + $"   ({angle:0}d off target)", clip);
    }

    private static AnimationClip Load(string name)
        => AssetDatabase.LoadAssetAtPath<AnimationClip>(Folder + "/" + name + ".anim");

    /// <summary>
    /// Reads a body-relative velocity out of thresholds.json, written by the slicing pass.
    /// </summary>
    /// <remarks>
    /// These cannot be derived in-editor. Each source take was captured facing an arbitrary
    /// direction, so the clip's root displacement points nowhere meaningful - the real
    /// direction only appears once each frame's delta is rotated by the inverse of RootQ.
    /// </remarks>
    private static Vector2 Threshold(string slot)
    {
        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(Folder + "/thresholds.json");
        if (asset == null)
        {
            Debug.LogWarning("Locomotion: thresholds.json not found in " + Folder +
                "; the blend space will fall back to averageSpeed, which is unreliable here.");
            return Vector2.zero;
        }

        // Small hand-rolled read - JsonUtility will not parse a bare array of numbers.
        var match = System.Text.RegularExpressions.Regex.Match(
            asset.text,
            "\"" + slot + "\"\\s*:\\s*\\[\\s*(-?[\\d.eE+-]+)\\s*,\\s*(-?[\\d.eE+-]+)\\s*\\]");

        if (!match.Success)
        {
            Debug.LogWarning("Locomotion: no threshold for " + slot + " in thresholds.json.");
            return Vector2.zero;
        }

        return new Vector2(
            float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
