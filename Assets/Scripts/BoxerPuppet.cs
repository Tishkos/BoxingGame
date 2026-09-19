using RootMotion.Dynamics;
using UnityEngine;

/// <summary>
/// Minimal PuppetMaster bridge for Bennett (the full integration session comes later). Its one job right now:
/// stop the puppet from fighting the Legs Animator — the LEG and FOOT muscles are taken out of the visual
/// mapping, so the visible legs render pure animation while the rest of the puppet keeps simulating.
/// Add next to <see cref="BoxerPunchController"/>; it finds the PuppetMaster by target root itself.
/// Works in Kinematic mode today and keeps working when Active mode is fixed.
/// </summary>
[DefaultExecutionOrder(60)]
public class BoxerPuppet : MonoBehaviour
{
    [Tooltip("The PuppetMaster driving this character. Auto-found (by its Target Root) when empty.")]
    [SerializeField] private PuppetMaster puppetMaster;

    [Tooltip("Legs render pure animation (Legs Animator owns them): leg/foot muscles stop mapping to the visible bones.")]
    [SerializeField] private bool legsFromAnimationOnly = true;

    public PuppetMaster Puppet => puppetMaster;

    private void Start()
    {
        if (puppetMaster == null)
        {
            foreach (PuppetMaster pm in FindObjectsByType<PuppetMaster>())
                if (pm.targetRoot == transform) { puppetMaster = pm; break; }
        }
        if (puppetMaster == null)
        {
            Debug.LogWarning("BoxerPuppet: no PuppetMaster found for this character (assign one or add it to the rig).", this);
            enabled = false;
            return;
        }
        if (legsFromAnimationOnly) UnmapLegs();
    }

    private void UnmapLegs()
    {
        int changed = 0;
        foreach (Muscle m in puppetMaster.muscles)
        {
            if (m == null || m.props == null) continue;
            if (m.props.group != Muscle.Group.Leg && m.props.group != Muscle.Group.Foot) continue;
            m.props.mappingWeight = 0f;   // the visible leg = animation; the muscle still simulates for collisions
            m.props.pinWeight = 1f;       // and stays glued to the animated pose so it never drags behind
            changed++;
        }
        Debug.Log($"BoxerPuppet: {changed} leg/foot muscle(s) unmapped — Legs Animator owns the visible legs.", this);
    }
}
