using System;
using UnityEngine;

/// <summary>
/// Receives the AnimationEvents that Undisputed's clips still carry, and turns the useful ones
/// into something this project can subscribe to.
/// </summary>
/// <remarks>
/// 475 of the 901 ripped clips carry events -- 868 calls across 26 names -- because they are
/// part of the clip asset, not of the original game's controller. Unity warns once per call per
/// frame when nothing answers them, which is what fills the console.
///
/// Declaring the methods is the fix. Deleting the events would also silence it, but it would
/// throw away timing the original developers authored by hand: ExpectedImpact is the frame the
/// punch was MEANT to land on. Measuring that from the motion instead -- peak arm extension --
/// disagrees with the authored marker by a median of 7% of clip length and by as much as 22%,
/// so the marker is worth keeping and the guess is not.
///
/// Every event in the library is parameterless, so plain no-argument methods receive them all.
/// </remarks>
[DisallowMultipleComponent]
public class UndisputedClipEvents : MonoBehaviour
{
    /// <summary>The frame the punch was authored to land on.</summary>
    public event Action Impact;

    /// <summary>Hitbox window opening or closing. Hand is 0 left, 1 right, -1 head.</summary>
    public event Action<int, bool> AttackWindow;

    /// <summary>The punch animation has run its course.</summary>
    public event Action Completed;

    /// <summary>Openings the original game scored counter-hits against.</summary>
    public event Action<bool> Vulnerable;

    [Tooltip("Log each event as it arrives. Useful once, noisy forever.")]
    public bool Verbose;

    /// <remarks>
    /// The study rig plays one clip on three masked layers, so every event arrives three times
    /// in the same frame -- once per layer. Collapsing by name and frame turns that back into
    /// the single event the clip actually describes.
    /// </remarks>
    private readonly System.Collections.Generic.Dictionary<string, int> _LastFrame
        = new System.Collections.Generic.Dictionary<string, int>();

    private bool First(string name)
    {
        int frame = Time.frameCount;
        if (_LastFrame.TryGetValue(name, out int seen) && seen == frame)
            return false;

        _LastFrame[name] = frame;

        if (Verbose)
            Debug.Log("clip event: " + name, this);

        return true;
    }

    /************************************************************************************
     * The ones worth acting on.
     ************************************************************************************/

    public void ExpectedImpact()      { if (First("ExpectedImpact")) Impact?.Invoke(); }
    public void PunchComplete()       { if (First("PunchComplete")) Completed?.Invoke(); }

    public void Left_Arm_Attack_On()  { if (First("L_On"))  AttackWindow?.Invoke(0, true); }
    public void Left_Arm_Attack_Off() { if (First("L_Off")) AttackWindow?.Invoke(0, false); }
    public void Right_Arm_Attack_On() { if (First("R_On"))  AttackWindow?.Invoke(1, true); }
    public void Right_Arm_Attack_Off(){ if (First("R_Off")) AttackWindow?.Invoke(1, false); }
    public void Head_Attack_On()      { if (First("H_On"))  AttackWindow?.Invoke(-1, true); }
    public void Head_Attack_Off()     { if (First("H_Off")) AttackWindow?.Invoke(-1, false); }

    public void VulnerabilityOn()     { if (First("VulnOn"))  Vulnerable?.Invoke(true); }
    public void VulnerabilityOff()    { if (First("VulnOff")) Vulnerable?.Invoke(false); }

    /************************************************************************************
     * Undisputed's own bookkeeping. Nothing here needs them, but they must be answered or
     * Unity warns on every single call.
     ************************************************************************************/

    public void RagdollOn()           { First("RagdollOn"); }
    public void ResetStun()           { First("ResetStun"); }
    public void ResetTaunt()          { First("ResetTaunt"); }
    public void ResetFeint()          { First("ResetFeint"); }
    public void ResetStanding()       { First("ResetStanding"); }
    public void ResetPhysics()        { First("ResetPhysics"); }
    public void EnablePunch()         { First("EnablePunch"); }
    public void InvulnerableOn()      { First("InvulnerableOn"); }
    public void InvulnerableOff()     { First("InvulnerableOff"); }
    public void WasClinched()         { First("WasClinched"); }
    public void SuccessfulClinch()    { First("SuccessfulClinch"); }
    public void ClinchPunchComplete() { First("ClinchPunchComplete"); }
    public void SwapComplete()        { First("SwapComplete"); }
    public void PushOverlap()         { First("PushOverlap"); }
    public void PushComplete()        { First("PushComplete"); }
    public void TakeAKneeConfirmed()  { First("TakeAKneeConfirmed"); }
}
