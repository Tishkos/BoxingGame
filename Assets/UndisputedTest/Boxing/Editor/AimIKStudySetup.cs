using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds a clean scene object for studying AimIK: Bennett, an Animator, the study component,
/// and nothing else. Deliberately no controller, no PuppetMaster, no physics — those are what
/// make the behaviour hard to read.
/// </summary>
public static class AimIKStudySetup
{
    private const string BennettPath = "Assets/Character/Bennett.fbx";
    private const string RigName = "AimIK_Study";

    [MenuItem("Tools/Undisputed/Boxing/Aim IK Study Rig")]
    public static void Build()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(BennettPath);
        if (model == null)
        {
            Debug.LogError("Aim IK study: could not find " + BennettPath);
            return;
        }

        GameObject existing = GameObject.Find(RigName);
        if (existing != null)
            Object.DestroyImmediate(existing);

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        instance.name = RigName;
        instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        Animator animator = instance.GetComponentInChildren<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogError("Aim IK study: Bennett needs a Humanoid Animator.", instance);
            return;
        }

        // An idle underneath makes the clamp and the chain weights readable — against a frozen
        // bind pose you cannot tell what the solver changed and what the animation did.
        animator.runtimeAnimatorController = null;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.applyRootMotion = false;

        AimIKStudy study = animator.gameObject.AddComponent<AimIKStudy>();
        study.Hand = AimIKStudy.Side.Right;

        EditorSceneManager.MarkSceneDirty(instance.scene);
        Selection.activeGameObject = instance;

        Debug.Log("Aim IK study: built '" + RigName + "'.\n" +
            "   Press Play. A target sphere is created in front of him — drag it around in the\n" +
            "   Scene view and watch the GREEN ray (where the fist points) chase the YELLOW line\n" +
            "   (where it should point). The error in degrees is on the panel.\n" +
            "   Toggle spine / chest / upper arm / forearm to see what each bone contributes.",
            instance);
    }
}
