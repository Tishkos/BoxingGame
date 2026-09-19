using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The normalised time in each punch clip where the fist is actually fully extended.
/// </summary>
/// <remarks>
/// <see cref="BoxerPunchController"/> scrubs punch clips against a single global
/// <c>clipStrikeTime</c> of 0.4, whose tooltip says "Mixamo punches ≈ 0.35-0.45". Undisputed is
/// mocap and does not behave like that: measured on the rig, its strike frames run from
/// <b>0.149 to 0.730</b> of clip length. 40% of the clips are more than 0.10 away from 0.4 and
/// the worst is 461 ms out.
///
/// The error is also systematic rather than noise — fast inside shots peak early (0.15-0.26),
/// committed lunging shots late (0.60-0.73) — so a single value is wrong in opposite directions
/// exactly where the fast-versus-committed distinction carries the gameplay. Hence a per-clip
/// table, measured by sweeping each clip for peak arm extension.
///
/// Built by <c>Tools > Undisputed > Boxing > Use Undisputed Animations</c>.
/// </remarks>
[CreateAssetMenu(menuName = "Boxing/Punch Clip Strike Times")]
public class PunchClipStrikeTimes : ScriptableObject
{
    [System.Serializable]
    public struct Entry
    {
        public AnimationClip Clip;

        [Range(0f, 1f)] public float Strike;
    }

    public List<Entry> Entries = new List<Entry>();

    private Dictionary<AnimationClip, float> _Lookup;

    /// <summary>The measured strike time, or -1 when this clip was never measured.</summary>
    public float StrikeOf(AnimationClip clip)
    {
        if (clip == null)
            return -1f;

        if (_Lookup == null)
        {
            _Lookup = new Dictionary<AnimationClip, float>(Entries.Count);
            foreach (Entry e in Entries)
            {
                if (e.Clip != null)
                    _Lookup[e.Clip] = e.Strike;
            }
        }

        return _Lookup.TryGetValue(clip, out float t) ? t : -1f;
    }

    public void Rebuild() => _Lookup = null;
}
