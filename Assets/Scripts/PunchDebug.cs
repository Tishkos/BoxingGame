using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// THE FLIGHT RECORDER for the punch pipeline — so "the arm does something weird" becomes a named culprit
/// instead of a guessing game.
///
/// Every frame it captures the whole chain for both hands:
///
///     posed hand (pose chain)  →  aim correction  →  straight-line projection  →  virtual fist
///     → puppet muscle vs its target → mapping fade → THE HAND YOU ACTUALLY SEE (the rendered bone)
///
/// and watches the RENDERED hand for a sideways dart: lateral speed (perpendicular to the punch line) beyond
/// what the punch itself explains. The moment one happens it writes a report to <c>PunchDebug/</c> next to the
/// Assets folder and prints a VERDICT in the Console naming which component moved the hand — the pose, the aim
/// correction, the projection, the puppet lagging, or the mapping fade switching. Send that verdict (or the
/// whole file) and the bug is identified, not guessed at.
///
///     F9  — toggle the on-screen overlay (live pipeline values + tuning check, top-right)
///     F10 — dump a report manually (e.g. "that one looked wrong")
///
/// Auto-dumps are rate-limited and capped per session so it can never spam. Player boxer only.
/// Added by Tools ▸ Boxer ▸ Rebuild Boxing System; remove the component when the hunt is over.
/// </summary>
[DefaultExecutionOrder(5000)]   // after IK, pose mixer and PuppetMaster mapping — we record what is RENDERED
[DisallowMultipleComponent]
public class PunchDebug : MonoBehaviour
{
    [Header("Dart detection")]
    [Tooltip("Sideways speed of the RENDERED hand (m/s at 1x scale, perpendicular to the punch line) that " +
             "counts as a dart while a punch is live.")]
    [Min(0.5f)] [SerializeField] private float dartSpeed = 3.5f;

    [Tooltip("The sideways speed must also exceed the along-the-punch speed by this factor — a fast punch is " +
             "fast forward; a dart is fast SIDEWAYS.")]
    [Min(0.5f)] [SerializeField] private float dartRatio = 1.1f;

    [Tooltip("Auto-dumps per session (F10 manual dumps are always allowed).")]
    [Range(1, 20)] [SerializeField] private int maxAutoDumps = 6;

    [Header("Overlay")]
    [SerializeField] private bool showOverlay = true;

    private struct Frame
    {
        public float time;
        public BoxerPunchController.HandPipelineState[] hands;   // per hand, from the controller
        public Vector3[] rendered;                               // the hand bone you SEE
        public Vector3[] muscle;                                 // puppet hand muscle position
        public Vector3[] muscleTarget;                           // where that muscle is trying to be
        public float[] pin;
        public bool[] physicsValid;
        public float mappingFade;
        public float stretch;
    }

    private const int Capacity = 600;   // ~10 s at 60 fps
    private readonly Frame[] buffer = new Frame[Capacity];
    private int head;
    private int count;

    private BoxerPunchController controller;
    private BoxerPhysics physics;
    private readonly Transform[] handBones = new Transform[2];
    private float scale = 1f;

    private int autoDumps;
    private float nextAutoDump;
    private int fadePumps;                 // times the mapping fade dropped — each one is a visible arm switch
    private float lastFadeSeen = 1f;
    private float fadePumpWindowStart;
    private readonly float[] lastActivePunch = { -10f, -10f };
    private string lastVerdict = "no dart seen yet";
    private GUIStyle overlayStyle;

    private void Awake()
    {
        controller = GetComponent<BoxerPunchController>();
        physics = GetComponent<BoxerPhysics>();
        scale = Mathf.Max(0.5f, transform.lossyScale.y);

        Animator animator = GetComponent<Animator>();
        if (animator != null && animator.isHuman)
        {
            handBones[0] = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            handBones[1] = animator.GetBoneTransform(HumanBodyBones.RightHand);
        }

        for (int f = 0; f < Capacity; f++)
        {
            buffer[f].hands = new BoxerPunchController.HandPipelineState[2];
            buffer[f].rendered = new Vector3[2];
            buffer[f].muscle = new Vector3[2];
            buffer[f].muscleTarget = new Vector3[2];
            buffer[f].pin = new float[2];
            buffer[f].physicsValid = new bool[2];
        }
    }

    private void LateUpdate()
    {
        if (controller == null) return;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.f9Key.wasPressedThisFrame) showOverlay = !showOverlay;
            if (kb.f10Key.wasPressedThisFrame) Dump("manual (F10)", 0, Time.time);
        }

        Capture();

        if (controller.IsPlayerControlled) Detect();
    }

    // ---------------------------------------------------------------- Capture

    private void Capture()
    {
        head = (head + 1) % Capacity;
        if (count < Capacity) count++;
        ref Frame frame = ref buffer[head];

        frame.time = Time.time;
        frame.mappingFade = physics != null ? physics.MappingFade : 1f;
        frame.stretch = physics != null ? physics.Stretch : 0f;

        for (int h = 0; h < 2; h++)
        {
            frame.hands[h] = controller.PipelineState(h);
            frame.rendered[h] = handBones[h] != null ? handBones[h].position : Vector3.zero;
            frame.physicsValid[h] = physics != null &&
                physics.TryGetHandState(h, out frame.muscle[h], out frame.muscleTarget[h], out frame.pin[h]);
            if (frame.hands[h].phase >= BoxerPunchController.Phase.Drive) lastActivePunch[h] = Time.time;
        }

        // A mapping-fade drop is one visible switch between "you see the animation" and "you see the puppet".
        if (Time.time - fadePumpWindowStart > 10f) { fadePumps = 0; fadePumpWindowStart = Time.time; }
        if (frame.mappingFade < 0.9f && lastFadeSeen >= 0.9f) fadePumps++;
        lastFadeSeen = frame.mappingFade;
    }

    // ---------------------------------------------------------------- Detection

    private void Detect()
    {
        if (count < 3 || autoDumps >= maxAutoDumps || Time.time < nextAutoDump) return;

        ref Frame now = ref buffer[head];
        ref Frame prev = ref buffer[(head - 1 + Capacity) % Capacity];
        float dt = now.time - prev.time;
        if (dt <= 0.0001f) return;

        for (int h = 0; h < 2; h++)
        {
            // Only while this hand's punch is live (or just finished — the recover snap counts too).
            if (Time.time - lastActivePunch[h] > 0.3f) continue;
            if (now.rendered[h] == Vector3.zero || prev.rendered[h] == Vector3.zero) continue;

            Vector3 axis = now.hands[h].target - now.hands[h].launch;
            if (axis.sqrMagnitude < 0.01f) continue;
            axis.Normalize();

            Vector3 velocity = (now.rendered[h] - prev.rendered[h]) / dt;
            float along = Vector3.Dot(velocity, axis);
            Vector3 lateral = velocity - axis * along;

            if (lateral.magnitude > dartSpeed * scale && lateral.magnitude > Mathf.Abs(along) * dartRatio)
            {
                autoDumps++;
                nextAutoDump = Time.time + 2f;
                Dump($"AUTO — sideways dart, {(h == 0 ? "LEFT" : "RIGHT")} hand, {lateral.magnitude:0.0} m/s lateral vs {Mathf.Abs(along):0.0} m/s along the punch", h, now.time);
                return;
            }
        }
    }

    // ---------------------------------------------------------------- Reporting

    /// <summary>Between two frames, how far each pipeline component moved this hand SIDEWAYS (m, at world scale).</summary>
    private void Attribute(ref Frame a, ref Frame b, int h, Vector3 axis, StringBuilder verdict)
    {
        Vector3 Lat(Vector3 v) => v - axis * Vector3.Dot(v, axis);

        float pose = Lat(b.hands[h].animatedHand - a.hands[h].animatedHand).magnitude;
        float correction = Lat(b.hands[h].correction - a.hands[h].correction).magnitude;
        float projection = Lat(b.hands[h].projection - a.hands[h].projection).magnitude;
        // What the mesh shows minus what the controller asked for: puppet displacement + mapping blend.
        float mapping = Lat((b.rendered[h] - b.hands[h].virtualFist) - (a.rendered[h] - a.hands[h].virtualFist)).magnitude;

        float top = Mathf.Max(pose, Mathf.Max(correction, Mathf.Max(projection, mapping)));
        string guilty = top <= 0.0001f ? "nothing measurable"
            : top == pose ? "THE POSE CHAIN (authored motion / follow-through)"
            : top == correction ? "THE AIM CORRECTION (steer toward the target)"
            : top == projection ? "THE STRAIGHT-LINE PROJECTION"
            : "THE PUPPET / MAPPING (physics hand displaced, or the mapping fade switching)";

        verdict.AppendLine($"    lateral movement this frame — pose {pose * 1000f:0} mm · aim {correction * 1000f:0} mm · " +
                           $"projection {projection * 1000f:0} mm · puppet/mapping {mapping * 1000f:0} mm");
        verdict.AppendLine($"    → GUILTY: {guilty}");
    }

    private void Dump(string reason, int dartHand, float dartTime)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("PUNCH DEBUG REPORT");
        sb.AppendLine($"reason: {reason}");
        sb.AppendLine($"time: {dartTime:0.000}   character scale: {scale:0.##}x");
        if (physics != null)
            sb.AppendLine($"physics tuning: spring {physics.MuscleSpringValue:0} · pinPow {physics.PinPowValue:0.#} · " +
                          $"assistVelGain {physics.AssistVelocityGainValue:0} · assistMaxForce {physics.AssistMaxForceValue:0} · " +
                          $"mappingFade now {physics.MappingFade:0.00} · stretch now {physics.Stretch:0.00}");
        else
            sb.AppendLine("physics: none (IK mode)");
        sb.AppendLine($"mapping-fade pumps in the last 10 s: {fadePumps}  (each one is a visible arm switch)");
        sb.AppendLine();

        // The verdict: attribute the dart frame (newest vs previous).
        StringBuilder verdict = new StringBuilder();
        if (count >= 2)
        {
            ref Frame now = ref buffer[head];
            ref Frame prev = ref buffer[(head - 1 + Capacity) % Capacity];
            Vector3 axis = now.hands[dartHand].target - now.hands[dartHand].launch;
            axis = axis.sqrMagnitude > 0.01f ? axis.normalized : transform.forward;
            verdict.AppendLine($"VERDICT ({(dartHand == 0 ? "left" : "right")} hand, phase {now.hands[dartHand].phase}, x {now.hands[dartHand].x:0.00}):");
            Attribute(ref prev, ref now, dartHand, axis, verdict);
        }
        sb.AppendLine(verdict.ToString());

        // The last 0.6 s, both hands, TSV — everything needed to reconstruct the punch.
        sb.AppendLine("t\thand\tphase\tx\tsteer\t|corr|mm\t|proj|mm\tposeLatV\tfistLatV\trenderLatV\tpuppetGapMm\tpin\tmapFade\tstretch");
        int frames = Mathf.Min(count, Mathf.CeilToInt(0.6f / Mathf.Max(0.005f, Time.smoothDeltaTime)));
        for (int back = frames - 1; back >= 0; back--)
        {
            int idx = (head - back + Capacity) % Capacity;
            int prevIdx = (idx - 1 + Capacity) % Capacity;
            ref Frame f = ref buffer[idx];
            ref Frame p = ref buffer[prevIdx];
            float dt = Mathf.Max(0.0001f, f.time - p.time);

            for (int h = 0; h < 2; h++)
            {
                if (f.hands[h].phase == BoxerPunchController.Phase.Guard && back > 5) continue;   // quiet hands stay out of the log
                Vector3 axis = f.hands[h].target - f.hands[h].launch;
                axis = axis.sqrMagnitude > 0.01f ? axis.normalized : transform.forward;
                Vector3 Lat(Vector3 v) => v - axis * Vector3.Dot(v, axis);

                float poseLatV = Lat(f.hands[h].animatedHand - p.hands[h].animatedHand).magnitude / dt;
                float fistLatV = Lat(f.hands[h].virtualFist - p.hands[h].virtualFist).magnitude / dt;
                float renderLatV = Lat(f.rendered[h] - p.rendered[h]).magnitude / dt;
                float gap = f.physicsValid[h] ? (f.muscle[h] - f.muscleTarget[h]).magnitude * 1000f : 0f;

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:0.000}\t{1}\t{2}\t{3:0.00}\t{4:0.00}\t{5:0}\t{6:0}\t{7:0.0}\t{8:0.0}\t{9:0.0}\t{10:0}\t{11:0.00}\t{12:0.00}\t{13:0.00}",
                    f.time, h == 0 ? "L" : "R", f.hands[h].phase, f.hands[h].x, f.hands[h].steer,
                    f.hands[h].correction.magnitude * 1000f, f.hands[h].projection.magnitude * 1000f,
                    poseLatV, fistLatV, renderLatV, gap, f.pin[h], f.mappingFade, f.stretch));
            }
        }

        string dir = Path.Combine(Application.dataPath, "..", "PunchDebug");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"punchdebug_{System.DateTime.Now:HHmmss}.txt");
        File.WriteAllText(path, sb.ToString());

        lastVerdict = verdict.Length > 0 ? verdict.ToString().TrimEnd() : reason;
        Debug.LogWarning($"PUNCH DEBUG — {reason}\n{lastVerdict}\nfull report: {path}", this);
    }

    // ---------------------------------------------------------------- Overlay

    private void OnGUI()
    {
        if (!showOverlay || !Application.isPlaying || controller == null || !controller.IsPlayerControlled) return;
        if (overlayStyle == null)
        {
            overlayStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
            overlayStyle.normal.textColor = Color.white;
        }

        float w = 330f;
        float x = Screen.width - w - 12f;
        float y = 12f;
        GUI.Box(new Rect(x - 6f, y - 6f, w + 12f, 190f), GUIContent.none);
        GUI.Label(new Rect(x, y, w, 20f), "<b>Punch debug</b>  (F9 hide · F10 dump)", overlayStyle); y += 20f;

        for (int h = 0; h < 2; h++)
        {
            BoxerPunchController.HandPipelineState s = controller.PipelineState(h);
            string side = h == 0 ? "L" : "R";
            GUI.Label(new Rect(x, y, w, 18f),
                $"{side}  {s.phase}  x {s.x:0.00}  steer {s.steer:0.00}  corr {s.correction.magnitude * 100f:0} cm  proj {s.projection.magnitude * 100f:0} cm",
                overlayStyle);
            y += 18f;
        }

        if (physics != null)
        {
            string fade = physics.MappingFade < 0.9f ? $"<color=#ff6b5e>{physics.MappingFade:0.00}</color>" : $"{physics.MappingFade:0.00}";
            string pumps = fadePumps > 0 ? $"<color=#ff6b5e>{fadePumps}</color>" : "0";
            GUI.Label(new Rect(x, y, w, 18f), $"mapping fade {fade}   fade pumps (10 s) {pumps}   stretch {physics.Stretch:0.00}", overlayStyle); y += 18f;
            bool tuned = physics.AssistVelocityGainValue >= 39f && physics.MuscleSpringValue >= 649f && physics.PinPowValue <= 2.01f;
            GUI.Label(new Rect(x, y, w, 18f),
                tuned ? $"tuning: spring {physics.MuscleSpringValue:0} · pinPow {physics.PinPowValue:0.#} · velGain {physics.AssistVelocityGainValue:0} ✓"
                      : $"<color=#ff6b5e>tuning STALE (spring {physics.MuscleSpringValue:0}, pinPow {physics.PinPowValue:0.#}, velGain {physics.AssistVelocityGainValue:0}) — run Tools ▸ Boxer ▸ Rebuild Boxing System</color>",
                overlayStyle);
            y += 18f;
        }
        else
        {
            GUI.Label(new Rect(x, y, w, 18f), "physics: none (IK mode)", overlayStyle); y += 18f;
        }

        GUI.Label(new Rect(x, y, w, 54f), $"last verdict:\n{Short(lastVerdict)}", overlayStyle);
    }

    private static string Short(string s) => s.Length > 170 ? s.Substring(0, 170) + "…" : s;
}
