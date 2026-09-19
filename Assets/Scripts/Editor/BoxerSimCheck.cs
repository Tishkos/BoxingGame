using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click sanity check for the simulation's determinism foundation: two SimRng streams with the same seed
/// must agree on every draw; a different seed must diverge. Run it after touching SimRng or the seams.
/// </summary>
public static class BoxerSimCheck
{
    [MenuItem("Tools/Boxer/Verify Determinism (SimRng)")]
    private static void Verify()
    {
        SimRng a = new SimRng(12345u);
        SimRng b = new SimRng(12345u);
        SimRng other = new SimRng(54321u);

        int mismatches = 0;
        bool divergesFromOtherSeed = false;
        float min = float.MaxValue, max = float.MinValue, sum = 0f;
        const int draws = 20000;

        for (int i = 0; i < draws; i++)
        {
            float va = a.Value01(), vb = b.Value01(), vo = other.Value01();
            if (va != vb) mismatches++;
            if (va != vo) divergesFromOtherSeed = true;
            min = Mathf.Min(min, va); max = Mathf.Max(max, va); sum += va;

            if (a.Range(0, 7) != b.Range(0, 7)) mismatches++;
            other.Range(0, 7);
            Vector2 ca = a.InsideUnitCircle(), cb = b.InsideUnitCircle();
            if (ca != cb) mismatches++;
            other.InsideUnitCircle();
            if (ca.sqrMagnitude > 1.0001f) mismatches++;
        }

        float mean = sum / draws;
        bool healthy = mismatches == 0 && divergesFromOtherSeed && min >= 0f && max < 1f && Mathf.Abs(mean - 0.5f) < 0.02f;
        if (healthy)
            Debug.Log($"SimRng OK: {draws} draws ×3 kinds — same seed identical, different seed diverges, " +
                      $"Value01 in [{min:0.0000}, {max:0.0000}], mean {mean:0.000}. The determinism foundation holds.");
        else
            Debug.LogError($"SimRng BROKEN: mismatches {mismatches}, divergesFromOtherSeed {divergesFromOtherSeed}, " +
                           $"range [{min:0.0000}, {max:0.0000}], mean {mean:0.000}.");
    }
}
