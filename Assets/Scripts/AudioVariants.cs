using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SO MUCH VARIATION from a handful of recordings — without authoring a single new file.
///
/// At load, every source clip is decompressed and run through a tiny DSP kitchen to mint extra takes:
///
///   PITCH VARIANTS — resampled a few semitones up/down: the same hit from a slightly different fist.
///   SOFT VARIANTS  — low-passed and eased: the sound of the same contact without the snap, for light
///                    touches and walking pace.
///   BODY VARIANTS  — pitched WAY down and filtered to a chest-thump: layered UNDER the original on heavy
///                    hits, it is what makes a big cross sound big instead of just loud.
///
/// On top of the minted takes, every playback adds live pitch/volume jitter, and a SHUFFLE BAG deals the clips
/// like cards — nothing repeats until the whole bank has played, and the same take never plays twice in a row.
/// Twelve bag recordings become hundreds of distinguishable hits.
///
/// If a clip's samples cannot be read (import not set to Decompress On Load — the setup tool sets it), the bank
/// quietly falls back to the originals with live jitter only. Sound always plays; variants are a bonus.
/// </summary>
public static class AudioVariants
{
    // ------------------------------------------------------------------ Bank

    /// <summary>A dealt deck of clips: random order, no repeats until exhausted, never the same card twice running.</summary>
    public class Bank
    {
        private readonly List<AudioClip> clips = new List<AudioClip>();
        private readonly List<int> deck = new List<int>();
        private int lastDealt = -1;

        public int Count => clips.Count;
        public bool IsEmpty => clips.Count == 0;

        public void Add(AudioClip clip)
        {
            if (clip != null) clips.Add(clip);
        }

        public AudioClip Next()
        {
            if (clips.Count == 0) return null;
            if (clips.Count == 1) return clips[0];

            if (deck.Count == 0)
            {
                for (int i = 0; i < clips.Count; i++) deck.Add(i);
                // Fisher-Yates; then make sure the new deck does not open with the card just played.
                for (int i = deck.Count - 1; i > 0; i--)
                {
                    int j = Random.Range(0, i + 1);
                    (deck[i], deck[j]) = (deck[j], deck[i]);
                }
                if (deck[deck.Count - 1] == lastDealt)
                    (deck[deck.Count - 1], deck[0]) = (deck[0], deck[deck.Count - 1]);
            }

            lastDealt = deck[deck.Count - 1];
            deck.RemoveAt(deck.Count - 1);
            return clips[lastDealt];
        }
    }

    // ------------------------------------------------------------------ Factory

    /// <summary>Originals + resampled pitch variants (± the given semitones). The everyday bank.</summary>
    public static Bank BuildPrimary(IEnumerable<AudioClip> sources, params float[] semitoneVariants)
    {
        Bank bank = new Bank();
        foreach (AudioClip source in Each(sources))
        {
            bank.Add(source);
            float[] data = Samples(source, out int channels, out int frequency);
            if (data == null) continue;
            foreach (float semitones in semitoneVariants)
            {
                float[] shifted = Resample(data, channels, Mathf.Pow(2f, semitones / 12f));
                bank.Add(MakeClip(shifted, channels, frequency, source.name + $" ({semitones:+0.#;-0.#} st)"));
            }
        }
        return bank;
    }

    /// <summary>Low-passed, eased takes of the sources — the same contact without the snap.</summary>
    public static Bank BuildSoft(IEnumerable<AudioClip> sources, float cutoffHz)
    {
        Bank bank = new Bank();
        foreach (AudioClip source in Each(sources))
        {
            float[] data = Samples(source, out int channels, out int frequency);
            if (data == null) { bank.Add(source); continue; }   // fallback: the original IS the soft take
            float[] soft = LowPass(data, channels, frequency, cutoffHz);
            Normalize(soft, Peak(data) * 0.8f);
            bank.Add(MakeClip(soft, channels, frequency, source.name + " (soft)"));
        }
        return bank;
    }

    /// <summary>
    /// Chest-thump layer: pitched far down, low-passed hard, capped in length. Layered under the original on a
    /// heavy hit it reads as WEIGHT; on its own (very slow, very filtered) it also serves as a strap creak.
    /// </summary>
    public static Bank BuildBody(IEnumerable<AudioClip> sources, float pitchFactor, float cutoffHz, float maxSeconds)
    {
        Bank bank = new Bank();
        foreach (AudioClip source in Each(sources))
        {
            float[] data = Samples(source, out int channels, out int frequency);
            if (data == null) continue;                          // no fallback — a missing layer is just quieter
            float[] slow = Resample(data, channels, pitchFactor);
            float[] thump = LowPass(slow, channels, frequency, cutoffHz);
            int maxLength = Mathf.Min(thump.Length, (int)(maxSeconds * frequency) * channels);
            if (maxLength < thump.Length)
            {
                float[] cut = new float[maxLength];
                System.Array.Copy(thump, cut, maxLength);
                thump = cut;
            }
            Fade(thump, channels, frequency);
            Normalize(thump, Peak(data));
            bank.Add(MakeClip(thump, channels, frequency, source.name + " (body)"));
        }
        return bank;
    }

    // ------------------------------------------------------------------ Voice pool

    /// <summary>A pool of 3D one-shot voices. Each play gets its own source so pitch never bends a ringing tail.</summary>
    public class VoicePool
    {
        private readonly AudioSource[] voices;
        private int next;

        public VoicePool(Transform parent, int count, float minDistance, float maxDistance)
        {
            voices = new AudioSource[count];
            for (int i = 0; i < count; i++)
            {
                GameObject go = new GameObject("Voice " + (i + 1));
                go.transform.SetParent(parent, false);
                AudioSource s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 1f;
                s.dopplerLevel = 0f;
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = minDistance;
                s.maxDistance = maxDistance;
                voices[i] = s;
            }
        }

        /// <summary>Play a clip at a world position with exact pitch/volume. Steals the oldest voice if all busy.</summary>
        public AudioSource Play(AudioClip clip, Vector3 position, float volume, float pitch)
        {
            if (clip == null) return null;
            AudioSource voice = voices[next];
            next = (next + 1) % voices.Length;
            voice.transform.position = position;
            voice.pitch = pitch;
            voice.volume = Mathf.Clamp01(volume);
            voice.clip = clip;
            voice.Play();
            return voice;
        }
    }

    // ------------------------------------------------------------------ DSP

    private static IEnumerable<AudioClip> Each(IEnumerable<AudioClip> sources)
    {
        if (sources == null) yield break;
        foreach (AudioClip c in sources) if (c != null) yield return c;
    }

    /// <summary>The raw samples, or null when they cannot be read (compressed-in-memory import, streaming…).</summary>
    private static float[] Samples(AudioClip clip, out int channels, out int frequency)
    {
        channels = clip.channels;
        frequency = clip.frequency;
        if (clip.loadType != AudioClipLoadType.DecompressOnLoad) return null;
        if (clip.loadState != AudioDataLoadState.Loaded) clip.LoadAudioData();
        if (clip.loadState != AudioDataLoadState.Loaded) return null;   // still loading — the original plays fine without variants

        float[] data = new float[clip.samples * clip.channels];
        if (data.Length == 0 || !clip.GetData(data, 0)) return null;
        return data;
    }

    /// <summary>Linear resample: factor > 1 = higher and shorter, < 1 = deeper and longer.</summary>
    private static float[] Resample(float[] source, int channels, float factor)
    {
        int sourceFrames = source.Length / channels;
        int outFrames = Mathf.Max(2, (int)(sourceFrames / Mathf.Max(0.05f, factor)));
        float[] output = new float[outFrames * channels];
        for (int frame = 0; frame < outFrames; frame++)
        {
            float src = frame * factor;
            int i0 = Mathf.Min((int)src, sourceFrames - 1);
            int i1 = Mathf.Min(i0 + 1, sourceFrames - 1);
            float t = src - i0;
            for (int c = 0; c < channels; c++)
                output[frame * channels + c] = Mathf.Lerp(source[i0 * channels + c], source[i1 * channels + c], t);
        }
        return output;
    }

    /// <summary>One-pole low pass per channel — cheap, warm, exactly what a glove or a floorboard does to a sound.</summary>
    private static float[] LowPass(float[] source, int channels, int frequency, float cutoffHz)
    {
        float[] output = new float[source.Length];
        float a = 1f - Mathf.Exp(-2f * Mathf.PI * cutoffHz / frequency);
        for (int c = 0; c < channels; c++)
        {
            float y = 0f;
            for (int frame = 0; frame < source.Length / channels; frame++)
            {
                int i = frame * channels + c;
                y += a * (source[i] - y);
                output[i] = y;
            }
        }
        return output;
    }

    /// <summary>2 ms fade-in and 30 ms fade-out — resampling and truncation must never click.</summary>
    private static void Fade(float[] data, int channels, int frequency)
    {
        int frames = data.Length / channels;
        int inFrames = Mathf.Min(frames / 2, frequency / 500);
        int outFrames = Mathf.Min(frames / 2, frequency * 3 / 100);
        for (int f = 0; f < inFrames; f++)
        {
            float g = f / (float)inFrames;
            for (int c = 0; c < channels; c++) data[f * channels + c] *= g;
        }
        for (int f = 0; f < outFrames; f++)
        {
            float g = f / (float)outFrames;
            for (int c = 0; c < channels; c++) data[(frames - 1 - f) * channels + c] *= g;
        }
    }

    private static float Peak(float[] data)
    {
        float peak = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            float a = Mathf.Abs(data[i]);
            if (a > peak) peak = a;
        }
        return peak;
    }

    /// <summary>Scale so the loudest sample matches the target peak (processing must not change perceived level).</summary>
    private static void Normalize(float[] data, float targetPeak)
    {
        float peak = Peak(data);
        if (peak < 0.0001f || targetPeak <= 0f) return;
        float gain = Mathf.Min(targetPeak / peak, 4f);
        for (int i = 0; i < data.Length; i++) data[i] = Mathf.Clamp(data[i] * gain, -1f, 1f);
    }

    private static AudioClip MakeClip(float[] data, int channels, int frequency, string name)
    {
        AudioClip clip = AudioClip.Create(name, data.Length / channels, channels, frequency, false);
        clip.SetData(data, 0);
        return clip;
    }
}
