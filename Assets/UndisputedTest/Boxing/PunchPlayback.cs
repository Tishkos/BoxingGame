using System;

public static class PunchPlayback
{
    public struct Timing
    {
        public float Strike;
        public float ContactStart;
        public float ContactEnd;
        public float End;
    }

    public static Timing CreateTiming(float strike, float contactStart, float contactEnd, float complete)
    {
        strike = Clamp(Finite(strike, 0.4f), 0.08f, 0.9f);
        float start = contactStart >= 0f ? Finite(contactStart, strike - 0.15f) : strike - 0.15f;
        float end = contactEnd >= 0f ? Finite(contactEnd, strike + 0.1f) : strike + 0.1f;
        start = Clamp(start, 0f, strike - 0.01f);
        end = Clamp(end, strike + 0.025f, 0.96f);
        float recovery = complete >= 0f ? Finite(complete, 1f) : strike + (1f - strike) * 0.8f;
        recovery = Math.Max(recovery, strike + (1f - strike) * 0.65f);
        return new Timing
        {
            Strike = strike,
            ContactStart = start,
            ContactEnd = end,
            End = Clamp(recovery, end + 0.025f, 0.995f),
        };
    }

    public static float ComboTime(Timing timing, float replayAfter, bool alternating)
    {
        float gate = Clamp(Finite(replayAfter, 0.6f), 0f, 1f);
        if (alternating) gate = timing.Strike + Math.Max(0f, gate - timing.Strike) * 0.65f;
        return Math.Min(timing.End, Math.Max(timing.ContactEnd + 0.015f, gate));
    }

    public static float Speed(float length, float strike, float impactSeconds, float speed, float fatigue)
    {
        length = Math.Max(0.01f, Finite(length, 1f));
        strike = Clamp(Finite(strike, 0.4f), 0.01f, 0.99f);
        impactSeconds = Math.Max(0.05f, Finite(impactSeconds, 0.3f));
        speed = Math.Max(0.1f, Finite(speed, 1f));
        fatigue = Clamp(Finite(fatigue, 1f), 0.1f, 1f);
        return Clamp(length * strike / impactSeconds * speed * fatigue, 0.02f, 6f);
    }

    public static float StaminaSpeed(float stamina, float tiredSpeed)
    {
        float energy = Clamp(Finite(stamina, 0f), 0f, 1f);
        float minimum = Clamp(Finite(tiredSpeed, 0.22f), 0.1f, 1f);
        return minimum + (1f - minimum) * energy * energy * (3f - 2f * energy);
    }

    public static float ComboSpeed(float acceleration, int combo, float stamina)
    {
        float energy = Clamp((Finite(stamina, 0f) - 0.35f) / 0.65f, 0f, 1f);
        return 1f + Clamp(Finite(acceleration, 0f), 0f, 0.3f) * Math.Min(2, Math.Max(0, combo))
            * energy * energy * (3f - 2f * energy);
    }

    public static float ReleaseImpactTime(float impactSeconds, float preparationElapsed, float preparationSeconds)
    {
        float progress = Clamp(Finite(preparationElapsed, 0f) / Math.Max(0.02f, Finite(preparationSeconds, 0.18f)), 0f, 1f);
        return Math.Max(0.05f, Finite(impactSeconds, 0.3f)) * (1f - 0.35f * progress * progress * (3f - 2f * progress));
    }

    public static float ReleaseSpeed(float length, float strike, float preparedTime, float impactSeconds, float speed, float fatigue)
    {
        strike = Clamp(Finite(strike, 0.4f), 0.08f, 0.9f);
        preparedTime = Clamp(Finite(preparedTime, 0f), 0f, strike * 0.45f);
        return Speed(length, strike - preparedTime, impactSeconds, speed, fatigue);
    }

    public static float AimEnvelope(float time, float strike, float width, float hold)
    {
        time = Finite(time, 0f);
        if (time <= 0f || time >= 1f) return 0f;
        strike = Clamp(Finite(strike, 0.4f), 0.01f, 0.99f);
        width = Math.Max(0.01f, Finite(width, 0.2f));
        hold = Math.Max(0f, Finite(hold, 0f));
        float start = Math.Max(0f, strike - width);
        float end = Math.Min(1f, strike + hold + width);
        float release = Math.Min(end - 0.001f, strike + hold);
        float value = time <= strike
            ? (time - start) / Math.Max(0.001f, strike - start)
            : 1f - Math.Max(0f, time - release) / Math.Max(0.001f, end - release);
        value = Clamp(value, 0f, 1f);
        return value * value * (3f - 2f * value);
    }

    public static int StickTechnique(float vertical, float threshold)
    {
        vertical = Finite(vertical, 0f);
        threshold = Clamp(Finite(threshold, 0.55f), 0.1f, 0.95f);
        return vertical > threshold ? 1 : vertical < -threshold ? 2 : 0;
    }

    public static int StickTechnique(float horizontal, float vertical, float threshold)
    {
        horizontal = Finite(horizontal, 0f);
        vertical = Finite(vertical, 0f);
        return Math.Abs(vertical) > Math.Abs(horizontal) * 1.2f ? StickTechnique(vertical, threshold) : 0;
    }

    public static int GuardFromShoulders(bool l1, bool r1)
    {
        return r1 ? 2 : l1 ? 1 : 0;
    }

    public static bool CanRaiseGuard(bool punchLive, float time, float contactEnd)
    {
        return !punchLive || Finite(time, 0f) >= Finite(contactEnd, 1f);
    }

    public static float LeanAuthority(bool punchHeld, bool guardHeld, bool flying)
    {
        return flying || (punchHeld && !guardHeld) ? 0f : 1f;
    }

    public static float DeadzoneMagnitude(float magnitude, float deadzone)
    {
        deadzone = Clamp(Finite(deadzone, 0.15f), 0f, 0.95f);
        return Clamp((Finite(magnitude, 0f) - deadzone) / (1f - deadzone), 0f, 1f);
    }

    public static bool CanSteer(bool releaseToPunch, bool preparing, bool triggerHeld)
    {
        return triggerHeld && (!releaseToPunch || preparing);
    }

    public static float SteeringAuthority(float time, float strike)
    {
        float progress = Finite(time, 1f) / Math.Max(0.01f, Finite(strike, 0.4f));
        float release = Clamp((progress - 0.4f) / 0.45f, 0f, 1f);
        return 1f - release * release * (3f - 2f * release);
    }

    public static float ChargeAmount(float held, float tapGrace, float fullCharge)
    {
        tapGrace = Math.Max(0f, Finite(tapGrace, 0.1f));
        fullCharge = Math.Max(tapGrace + 0.01f, Finite(fullCharge, 0.7f));
        float charge = Clamp((Finite(held, 0f) - tapGrace) / (fullCharge - tapGrace), 0f, 1f);
        return charge * charge * (3f - 2f * charge);
    }

    public static float HoldCost(float held, float dt, float tapGrace, float rate)
    {
        held = Math.Max(0f, Finite(held, 0f));
        dt = Math.Max(0f, Finite(dt, 0f));
        tapGrace = Math.Max(0f, Finite(tapGrace, 0.1f));
        rate = Math.Max(0f, Finite(rate, 0f));
        return Math.Max(0f, dt - Math.Max(0f, tapGrace - held)) * rate;
    }

    public static float ChargePower(float charge, float stamina, float bonus)
    {
        charge = Clamp(Finite(charge, 0f), 0f, 1f);
        float energy = Clamp(Finite(stamina, 0f) / 0.35f, 0f, 1f);
        return 1f + charge * energy * Clamp(Finite(bonus, 0f), 0f, 0.5f);
    }

    public static float ChamberTime(Timing timing)
    {
        return Math.Max(0f, Math.Min(Finite(timing.ContactStart, 0f) - 0.02f, Finite(timing.Strike, 0.4f) * 0.45f));
    }

    public static float PreparationTime(float elapsed, float duration, Timing timing)
    {
        float t = Clamp(Finite(elapsed, 0f) / Math.Max(0.02f, Finite(duration, 0.18f)), 0f, 1f);
        return ChamberTime(timing) * t * t * (3f - 2f * t);
    }

    public static float GuardCoverage(bool headHit, float headGuard, float bodyGuard, float facing)
    {
        return Finite(facing, 0f) > 0.25f ? Clamp(Finite(headHit ? headGuard : bodyGuard, 0f), 0f, 1f) : 0f;
    }

    public static float GuardedPower(float power, float coverage, float leak)
    {
        return Clamp(Finite(power, 0f), 0f, 1f) * (1f - Clamp(Finite(coverage, 0f), 0f, 1f)
            * (1f - Clamp(Finite(leak, 0.18f), 0f, 1f)));
    }

    public static bool CanDamageFighter(int owner, int target, bool armed, bool landed, bool down)
    {
        return owner != 0 && target != 0 && owner != target && armed && !landed && !down;
    }

    private static float Finite(float value, float fallback)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }

    private static float Clamp(float value, float min, float max)
    {
        return Math.Max(min, Math.Min(max, value));
    }
}

public sealed class PunchHold
{
    public bool Active { get; private set; }
    public float HeldSeconds { get; private set; }

    public void Begin()
    {
        Active = true;
        HeldSeconds = 0f;
    }

    public float Advance(float dt, float tapGrace, float drainPerSecond)
    {
        if (!Active || dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return 0f;
        float cost = PunchPlayback.HoldCost(HeldSeconds, dt, tapGrace, drainPerSecond);
        HeldSeconds += dt;
        return cost;
    }

    public float Release(float tapGrace, float fullCharge)
    {
        float charge = Active ? PunchPlayback.ChargeAmount(HeldSeconds, tapGrace, fullCharge) : 0f;
        Cancel();
        return charge;
    }

    public void Cancel()
    {
        Active = false;
        HeldSeconds = 0f;
    }
}

public sealed class PunchBuffer<T>
{
    private readonly T[] items;
    private readonly float[] expiry;
    private int head;
    public int Count { get; private set; }

    public PunchBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
        items = new T[capacity];
        expiry = new float[capacity];
    }

    public bool Enqueue(T item, float now, float lifetime)
    {
        Expire(now);
        if (Count == items.Length || lifetime <= 0f || float.IsNaN(now) || float.IsNaN(lifetime)
            || float.IsInfinity(now) || float.IsInfinity(lifetime) || float.IsInfinity(now + lifetime)) return false;
        int index = (head + Count) % items.Length;
        items[index] = item;
        expiry[index] = now + lifetime;
        Count++;
        return true;
    }

    public bool TryPeek(float now, out T item)
    {
        Expire(now);
        item = Count > 0 ? items[head] : default(T);
        return Count > 0;
    }

    public void Dequeue()
    {
        if (Count == 0) return;
        items[head] = default(T);
        head = (head + 1) % items.Length;
        Count--;
    }

    public void Clear()
    {
        while (Count > 0) Dequeue();
        head = 0;
    }

    private void Expire(float now)
    {
        while (Count > 0 && now >= expiry[head]) Dequeue();
    }
}

public sealed class FootstepCadence
{
    private float distance;

    public bool Advance(float metres, float stride, bool moving)
    {
        if (!moving) { Reset(); return false; }
        if (metres <= 0f || stride <= 0f || float.IsNaN(metres) || float.IsInfinity(metres)
            || float.IsNaN(stride) || float.IsInfinity(stride)) return false;
        distance += metres;
        if (distance < stride) return false;
        distance %= stride;
        return true;
    }

    public void Reset() { distance = 0f; }
}

public sealed class FightReactionWindow
{
    private float starts = float.PositiveInfinity;
    private float ends;
    public void Schedule(float now, float delay, float duration)
    {
        starts = now + Math.Max(0f, delay);
        ends = starts + Math.Max(0f, duration);
    }
    public bool Active(float now) { return now >= starts && now < ends; }
    public void Clear() { starts = float.PositiveInfinity; ends = 0f; }
}
