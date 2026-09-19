using UnityEngine;

/// <summary>
/// Where one boxer's intent comes from. Everything above this — the punch controller, the locomotion — reads a
/// boxer's input through this and never touches the static <see cref="BoxerInput"/> directly.
///
/// This exists so a SECOND boxer can exist at all. <see cref="BoxerInput"/> is a static singleton reading the
/// player's gamepad and mouse; two characters both reading it would mirror each other move for move. An AI
/// opponent writes into an <see cref="AIBoxerInput"/> instead, and the controllers cannot tell the difference.
///
/// Player-only feedback (rumble, the light bar, cursor lock, the reticle) is gated on <see cref="IsPlayer"/>,
/// so an AI throwing a punch never buzzes the player's controller.
/// </summary>
public interface IBoxerInput
{
    /// <summary>True for the human. Gates rumble, cursor capture and the on-screen reticle.</summary>
    bool IsPlayer { get; }

    /// <summary>Footwork: x = strafe / circle, y = in / out. Magnitude ≤ 1.</summary>
    Vector2 Move { get; }

    /// <summary>The body / hand stick: shape and height of the punch, or lean when no hand is held.</summary>
    Vector2 Body { get; }

    /// <summary>How fast <see cref="Body"/> is moving — flicks show up here.</summary>
    Vector2 BodyVelocity { get; }

    /// <summary>Is the punch input for that hand held? 0 = left, 1 = right.</summary>
    bool PunchHeld(int hand);

    /// <summary>High guard.</summary>
    bool Block { get; }

    /// <summary>Body guard.</summary>
    bool BodyGuard { get; }

    bool Sprint { get; }
    bool JumpDown { get; }

    /// <summary>Refresh for this frame. Safe to call more than once.</summary>
    void Poll();
}

/// <summary>The human. A thin forward to the static <see cref="BoxerInput"/>.</summary>
public sealed class PlayerBoxerInput : IBoxerInput
{
    public static readonly PlayerBoxerInput Instance = new PlayerBoxerInput();
    private PlayerBoxerInput() { }

    public bool IsPlayer => true;
    public Vector2 Move => BoxerInput.Move;
    public Vector2 Body => BoxerInput.Body;
    public Vector2 BodyVelocity => BoxerInput.BodyVelocity;
    public bool PunchHeld(int hand) => BoxerInput.PunchHeld(hand);
    public bool Block => BoxerInput.Block;
    public bool BodyGuard => BoxerInput.BodyGuard;
    public bool Sprint => BoxerInput.Sprint;
    public bool JumpDown => BoxerInput.JumpDown;
    public void Poll() => BoxerInput.Poll();
}

/// <summary>
/// An opponent's intent, written by <see cref="BoxerAI"/> and read by the same controllers the player uses. The
/// AI does not get privileged access to anything: it holds a trigger, pushes a stick and releases, exactly like a
/// human, so every mechanic the player has — the wind-up, the kinetic chain, effective mass, stamina, whiffing —
/// applies to it identically and for free.
/// </summary>
public sealed class AIBoxerInput : IBoxerInput
{
    public bool IsPlayer => false;

    public Vector2 Move { get; set; }
    public Vector2 Body { get; set; }
    public Vector2 BodyVelocity { get; private set; }
    public bool Block { get; set; }
    public bool BodyGuard { get; set; }
    public bool Sprint { get; set; }
    public bool JumpDown { get; set; }

    private readonly bool[] punch = new bool[2];
    private Vector2 previousBody;
    private bool hasPrevious;

    public bool PunchHeld(int hand) => punch[hand];
    public void SetPunch(int hand, bool held) => punch[hand] = held;

    /// <summary>Derives stick velocity the same way the player's does, so Flick throw mode works for the AI too.</summary>
    public void Poll()
    {
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        BodyVelocity = hasPrevious ? Vector2.Lerp(BodyVelocity, (Body - previousBody) / dt, 0.6f) : Vector2.zero;
        previousBody = Body;
        hasPrevious = true;
    }

    /// <summary>Drop everything — used when the boxer is knocked down or the fight stops.</summary>
    public void Clear()
    {
        Move = Vector2.zero;
        Body = Vector2.zero;
        Block = false;
        BodyGuard = false;
        Sprint = false;
        JumpDown = false;
        punch[0] = punch[1] = false;
    }
}
