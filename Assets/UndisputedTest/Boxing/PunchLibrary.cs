using System;
using System.Collections.Generic;
using UnityEngine;

namespace Boxing
{
    public enum Hand { Left, Right }

    public enum PunchType { Jab, Hook, Uppercut }

    public enum PunchTarget { Head, Body }

    /// <summary>Which way the boxer commits his weight, read from the clip's Lunge tag.</summary>
    public enum LungeType { None, In, Out, Left, Right }

    /// <summary>
    /// Every punch clip in the project, tagged by hand / type / target / power.
    /// </summary>
    /// <remarks>
    /// Undisputed encodes all of this in the filename
    /// (<c>..._OptimizedForHead_LHook_Power_Orthodox_MxM_MOD</c>), so the library is built
    /// by parsing names rather than by hand-tagging 52 clips. Rebuild it from
    /// <c>Tools > Undisputed > Boxing > Build Punch Library</c> after adding clips.
    /// </remarks>
    [CreateAssetMenu(menuName = "Boxing/Punch Library")]
    public class PunchLibrary : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public AnimationClip Clip;
            public Hand Hand;
            public PunchType Type;
            public PunchTarget Target;
            public LungeType Lunge;

            [Tooltip("0 = fast inside shot, 1 = mid, 2 = hard, 3 = committed power shot.")]
            public int Tier;

            public float Length;

            /// <summary>Normalised time the glove is most extended, i.e. impact.</summary>
            [Range(0f, 1f)] public float StrikeTime = 0.45f;

            public override string ToString()
                => $"{Hand} {Type} {Target} t{Tier}{(Lunge != LungeType.None ? " " + Lunge : "")}";
        }

        public List<Entry> Entries = new List<Entry>();

        [Header("Additive variation")]
        [Tooltip("Subtle upper-body clips layered additively so no two punches read the same. " +
                 "Slips, weaves and leans work well; anything with big root motion does not.")]
        public List<AnimationClip> AdditiveVariations = new List<AnimationClip>();

        [Header("Idle")]
        public AnimationClip Idle;

        private readonly Dictionary<int, List<Entry>> _Cache = new Dictionary<int, List<Entry>>();

        private static int Key(Hand hand, PunchType type, PunchTarget target)
            => ((int)hand * 8 + (int)type) * 4 + (int)target;

        /// <summary>All punches for one cell of the gesture grid, best-tier-first.</summary>
        public List<Entry> Query(Hand hand, PunchType type, PunchTarget target)
        {
            int key = Key(hand, type, target);
            if (_Cache.TryGetValue(key, out List<Entry> cached))
                return cached;

            var list = new List<Entry>();
            foreach (Entry e in Entries)
            {
                if (e.Clip != null && e.Hand == hand && e.Type == type && e.Target == target)
                    list.Add(e);
            }

            _Cache[key] = list;
            return list;
        }

        /// <summary>
        /// Closest match to the requested tier, preferring the requested lunge.
        /// Falls back through neighbouring tiers rather than returning nothing.
        /// </summary>
        public Entry Best(Hand hand, PunchType type, PunchTarget target, int tier, LungeType lunge,
                          IList<Entry> exclude = null)
        {
            List<Entry> pool = Query(hand, type, target);
            if (pool.Count == 0)
                return null;

            Entry best = null;
            float bestScore = float.MinValue;

            foreach (Entry e in pool)
            {
                float score = -Mathf.Abs(e.Tier - tier) * 2f;

                if (lunge != LungeType.None)
                    score += e.Lunge == lunge ? 3f : (e.Lunge == LungeType.None ? 0f : -1.5f);
                else
                    score += e.Lunge == LungeType.None ? 1f : -0.5f;

                // Push recently used clips down so the same shot does not repeat.
                if (exclude != null && exclude.Contains(e))
                    score -= 6f;

                // Break ties randomly, which is most of the variety in a small cell.
                score += UnityEngine.Random.value * 1.2f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            return best;
        }

        public void ClearCache() => _Cache.Clear();
    }
}
