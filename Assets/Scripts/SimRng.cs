using UnityEngine;

/// <summary>
/// The SIMULATION's random stream — xorshift128, one instance per boxer, seeded. Everything that changes the
/// fight (variation rolls, pose variants, aim scatter, stumbles) draws from here, so the same seed plus the
/// same inputs replays the same fight — the foundation prediction and reconciliation will need. Presentation
/// randomness (particles, audio jitter, camera) stays on UnityEngine.Random and is free to diverge.
/// </summary>
public class SimRng
{
    private uint a, b, c, d;

    public SimRng(uint seed)
    {
        a = seed == 0 ? 0x9E3779B9u : seed;
        b = a ^ 0x85EBCA6Bu;
        c = a ^ 0xC2B2AE35u;
        d = a ^ 0x27D4EB2Fu;
        for (int i = 0; i < 8; i++) Next();   // scramble the seed in
    }

    private uint Next()
    {
        uint t = d, s = a;
        d = c; c = b; b = s;
        t ^= t << 11;
        t ^= t >> 8;
        a = t ^ s ^ (s >> 19);
        return a;
    }

    /// <summary>0 (inclusive) … 1 (exclusive).</summary>
    public float Value01() => (Next() >> 8) * (1f / 16777216f);

    public float Range(float min, float max) => min + (max - min) * Value01();

    /// <summary>min inclusive, max EXCLUSIVE — the UnityEngine.Random.Range(int, int) contract.</summary>
    public int Range(int min, int maxExclusive) => maxExclusive <= min ? min : min + (int)(Next() % (uint)(maxExclusive - min));

    public Vector2 InsideUnitCircle()
    {
        float angle = Value01() * Mathf.PI * 2f;
        float r = Mathf.Sqrt(Value01());   // sqrt → uniform density over the disc
        return new Vector2(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r);
    }
}
