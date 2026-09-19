using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;

/// <summary>
/// One input surface for the whole boxer: footwork, body/aim stick, punch triggers, block, sprint, rumble, the
/// DualShock light bar and the mouse cursor. Two backends:
///
///  • Unity Input System (default). Keyboard + mouse, and any gamepad — DualShock 4/5 and Xbox pads are
///    recognised natively, so L2 / R2 / L1 / R1 and both sticks just work, with rumble.
///  • Rewired (define BOXER_REWIRED via Tools ▸ Boxer ▸ Rewired ▸ Enable). Reads the actions listed in
///    <see cref="RewiredActions"/> from Player 0. Falls back to the Input System until Rewired is ready and the
///    actions exist, so nothing breaks while you are still mapping things in the Rewired Editor.
///
/// Gamepad layout (<see cref="StickLayout.Classic"/>, default): LEFT stick = move the fighter,
/// RIGHT stick = body — slips/lean when no trigger is held, the punching hand while L2 / R2 is held.
/// L2 = left hand, R2 = right hand, L1 = guard (block), R1 = body guard, L3 = sprint.
/// Keyboard/mouse: WASD move, mouse = body/hand, LMB/RMB hands, MMB or Left Ctrl block, Shift sprint.
/// Clicking the game hides and locks the cursor (Esc releases it); the mouse then moves a virtual pointer.
/// </summary>
public static class BoxerInput
{
    public enum StickLayout
    {
        /// <summary>Left stick = footwork, right stick = body/hand.</summary>
        Classic,
        /// <summary>Left stick = body/hand, right stick = footwork.</summary>
        Swapped,
    }

    public enum Device { KeyboardMouse, Gamepad }

    /// <summary>Rewired action names the Rewired backend reads. Create these in the Rewired Editor.</summary>
    public static class RewiredActions
    {
        public const string MoveHorizontal = "Move Horizontal";
        public const string MoveVertical = "Move Vertical";
        public const string BodyHorizontal = "Body Horizontal";
        public const string BodyVertical = "Body Vertical";
        public const string PunchLeft = "Punch Left";
        public const string PunchRight = "Punch Right";
        public const string Block = "Block";
        public const string Sprint = "Sprint";
        /// <summary>Optional: body-guard pose (R1). Read only if the action exists.</summary>
        public const string BodyGuard = "Body Guard";
        public static readonly string[] All = { MoveHorizontal, MoveVertical, BodyHorizontal, BodyVertical, PunchLeft, PunchRight, Block, Sprint };
    }

    // ------------------------------------------------------------------ Settings

    public static StickLayout Layout = StickLayout.Classic;

    /// <summary>Radial dead zone for sticks (0-1), re-scaled so full deflection still reads 1.</summary>
    public static float StickDeadZone = 0.15f;

    /// <summary>Trigger depth that counts as pressed, and the depth it must fall back below to release (hysteresis).</summary>
    public static float TriggerPress = 0.35f;
    public static float TriggerRelease = 0.2f;

    /// <summary>Hide and lock the cursor when the game is clicked; Escape (or losing focus) releases it.</summary>
    public static bool HideCursorOnClick = true;

    /// <summary>How far the virtual pointer moves per mouse travel while the cursor is locked (1 = half a screen height per ~310 px).</summary>
    public static float MouseSensitivity = 1.6f;

    // ------------------------------------------------------------------ State (valid after Poll)

    public static Device ActiveDevice { get; private set; } = Device.KeyboardMouse;
    public static bool GamepadActive => ActiveDevice == Device.Gamepad;

    /// <summary>Footwork: x = strafe, y = forward/back, magnitude ≤ 1.</summary>
    public static Vector2 Move { get; private set; }

    /// <summary>Body / hand stick: gamepad body stick, or the (virtual) mouse pointer offset from the screen centre, -1..1.</summary>
    public static Vector2 Body { get; private set; }

    /// <summary>How fast <see cref="Body"/> is moving (units per second, lightly smoothed). Flicks show up here.</summary>
    public static Vector2 BodyVelocity { get; private set; }

    /// <summary>Screen position of the pointer (real cursor, or the virtual one while locked). For raycasts and reticles.</summary>
    public static Vector2 PointerScreenPosition { get; private set; }

    public static bool CursorLocked { get; private set; }

    public static bool Sprint { get; private set; }
    public static bool Block { get; private set; }

    /// <summary>Body guard held (R1 / Q): the low guard reference pose.</summary>
    public static bool BodyGuard { get; private set; }
    public static bool JumpDown { get; private set; }
    public static bool DebugPunchDown { get; private set; }

    /// <summary>True while the punch input for that hand (0 = left, 1 = right) is held.</summary>
    public static bool PunchHeld(int hand) => hand == 0 ? punchHeld[0] : punchHeld[1];

    /// <summary>How deep the trigger for that hand is pressed (0-1; mouse buttons read 1).</summary>
    public static float PunchPressure(int hand) => hand == 0 ? punchPressure[0] : punchPressure[1];

    /// <summary>True if a Rewired player with all the actions is driving input this frame.</summary>
    public static bool UsingRewired { get; private set; }

    private static readonly bool[] punchHeld = new bool[2];
    private static readonly float[] punchPressure = new float[2];
    private static int polledFrame = -1;
    private static Vector2 previousBody;
    private static bool hasPreviousBody;
    private static Vector2 virtualPointer;

    private static float rumbleUntil;
    private static float lowPeak, highPeak;   // peak-hold rumble envelope, decayed every frame
    private static bool rumbling;
    private static Color lightBar = Color.clear;
    private static float nextLightBarTime;

    // ------------------------------------------------------------------ Polling

    /// <summary>Read all inputs once per frame. Safe to call from several components.</summary>
    public static void Poll()
    {
        if (polledFrame == Time.frameCount) return;
        polledFrame = Time.frameCount;

        UsingRewired = false;
#if BOXER_REWIRED
        if (PollRewired()) UsingRewired = true;
        else
#endif
        PollInputSystem();

        float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        Vector2 velocity = hasPreviousBody ? (Body - previousBody) / dt : Vector2.zero;
        BodyVelocity = Vector2.Lerp(BodyVelocity, velocity, 0.6f);
        previousBody = Body;
        hasPreviousBody = true;

        UpdateRumble();
    }

    private static void PollInputSystem()
    {
        Gamepad pad = Gamepad.current;
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;

        // Whichever device was touched most recently owns the body/aim.
        double padTime = pad != null ? pad.lastUpdateTime : -1.0;
        double kbmTime = System.Math.Max(kb != null ? kb.lastUpdateTime : -1.0, mouse != null ? mouse.lastUpdateTime : -1.0);
        if (pad != null && padTime > kbmTime) ActiveDevice = Device.Gamepad;
        else if (kb != null || mouse != null) ActiveDevice = Device.KeyboardMouse;

        UpdateCursor(mouse, kb);

        // --- Footwork ---
        Vector2 move = Vector2.zero;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    move.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  move.y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  move.x -= 1f;
        }
        if (pad != null && move == Vector2.zero)
            move = Deadzone(Layout == StickLayout.Classic ? pad.leftStick.ReadValue() : pad.rightStick.ReadValue());
        Move = Vector2.ClampMagnitude(move, 1f);

        // --- Body / hand ---
        if (GamepadActive && pad != null)
        {
            Body = Deadzone(Layout == StickLayout.Classic ? pad.rightStick.ReadValue() : pad.leftStick.ReadValue());
            PointerScreenPosition = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }
        else
        {
            ReadMousePointer(mouse);
        }

        // --- Punches: L2 / R2 with hysteresis, or the mouse buttons ---
        for (int i = 0; i < 2; i++)
        {
            float trigger = 0f;
            if (pad != null) trigger = i == 0 ? pad.leftTrigger.ReadValue() : pad.rightTrigger.ReadValue();
            bool mouseButton = mouse != null && (i == 0 ? mouse.leftButton.isPressed : mouse.rightButton.isPressed);

            bool wasHeld = punchHeld[i];
            bool triggerHeld = wasHeld ? trigger > TriggerRelease : trigger > TriggerPress;
            punchHeld[i] = triggerHeld || mouseButton;
            punchPressure[i] = mouseButton ? 1f : Mathf.Clamp01(trigger);
        }

        // --- Block / sprint / jump / debug ---
        Block = (pad != null && pad.leftShoulder.isPressed)
             || (mouse != null && mouse.middleButton.isPressed)
             || (kb != null && kb.leftCtrlKey.isPressed);
        BodyGuard = (pad != null && pad.rightShoulder.isPressed)
                 || (kb != null && kb.qKey.isPressed);
        Sprint = (pad != null && pad.leftStickButton.isPressed)
              || (kb != null && kb.leftShiftKey.isPressed);
        JumpDown = (pad != null && pad.buttonSouth.wasPressedThisFrame)
                || (kb != null && kb.spaceKey.wasPressedThisFrame);
        DebugPunchDown = kb != null && kb.jKey.wasPressedThisFrame;
    }

    /// <summary>Mouse → Body: the real cursor while it is free, a delta-driven virtual pointer while it is locked/hidden.</summary>
    private static void ReadMousePointer(Mouse mouse)
    {
        if (mouse == null || Screen.width <= 0 || Screen.height <= 0)
        {
            Body = Vector2.zero;
            PointerScreenPosition = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            return;
        }

        if (CursorLocked)
        {
            Vector2 delta = mouse.delta.ReadValue();
            virtualPointer += delta * (MouseSensitivity * 2f / Screen.height);
            virtualPointer = new Vector2(Mathf.Clamp(virtualPointer.x, -1f, 1f), Mathf.Clamp(virtualPointer.y, -1f, 1f));
            Body = virtualPointer;
            PointerScreenPosition = new Vector2((Body.x + 1f) * 0.5f * Screen.width, (Body.y + 1f) * 0.5f * Screen.height);
        }
        else
        {
            Vector2 p = mouse.position.ReadValue();
            Body = new Vector2(
                Mathf.Clamp((p.x / Screen.width - 0.5f) * 2f, -1f, 1f),
                Mathf.Clamp((p.y / Screen.height - 0.5f) * 2f, -1f, 1f));
            virtualPointer = Body;
            PointerScreenPosition = p;
        }
    }

    private static void UpdateCursor(Mouse mouse, Keyboard kb)
    {
        if (!HideCursorOnClick)
        {
            if (CursorLocked) ReleaseCursor();
            return;
        }

        bool clicked = mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame);
        bool escape = kb != null && kb.escapeKey.wasPressedThisFrame;

        if (!CursorLocked && clicked && Application.isFocused) LockCursor();
        else if (CursorLocked && (escape || !Application.isFocused)) ReleaseCursor();
    }

    /// <summary>Hide and lock the cursor; aiming continues from mouse motion via a virtual pointer.</summary>
    public static void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        CursorLocked = true;
    }

    /// <summary>Show the cursor again (also called on Escape / focus loss).</summary>
    public static void ReleaseCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        CursorLocked = false;
    }

#if BOXER_REWIRED
    private static Rewired.Player rewiredPlayer;
    private static bool rewiredChecked;
    private static bool rewiredActionsOk;
    private static bool rewiredHasBodyGuard;

    private static bool PollRewired()
    {
        if (!Rewired.ReInput.isReady) { rewiredChecked = false; return false; }

        if (!rewiredChecked)
        {
            rewiredChecked = true;
            rewiredPlayer = Rewired.ReInput.players.GetPlayer(0);
            rewiredActionsOk = rewiredPlayer != null;
            rewiredHasBodyGuard = Rewired.ReInput.mapping.GetActionId(RewiredActions.BodyGuard) >= 0;
            foreach (string action in RewiredActions.All)
            {
                if (Rewired.ReInput.mapping.GetActionId(action) >= 0) continue;
                rewiredActionsOk = false;
                Debug.LogWarning($"BoxerInput: Rewired action '{action}' is missing — using the Input System backend until all actions exist. " +
                                 "Create them in the Rewired Editor (Window ▸ Rewired ▸ Input Manager).");
                break;
            }
        }
        if (!rewiredActionsOk) return false;

        Rewired.Player p = rewiredPlayer;
        Rewired.Controller last = p.controllers.GetLastActiveController();
        if (last != null) ActiveDevice = last.type == Rewired.ControllerType.Joystick ? Device.Gamepad : Device.KeyboardMouse;

        UpdateCursor(Mouse.current, Keyboard.current);

        Move = Vector2.ClampMagnitude(Deadzone(new Vector2(p.GetAxis(RewiredActions.MoveHorizontal), p.GetAxis(RewiredActions.MoveVertical))), 1f);

        if (GamepadActive)
        {
            Body = Deadzone(new Vector2(p.GetAxis(RewiredActions.BodyHorizontal), p.GetAxis(RewiredActions.BodyVertical)));
            PointerScreenPosition = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }
        else
        {
            ReadMousePointer(Mouse.current);
        }

        for (int i = 0; i < 2; i++)
        {
            string action = i == 0 ? RewiredActions.PunchLeft : RewiredActions.PunchRight;
            float axis = Mathf.Clamp01(Mathf.Abs(p.GetAxis(action)));
            bool wasHeld = punchHeld[i];
            bool held = p.GetButton(action) || (wasHeld ? axis > TriggerRelease : axis > TriggerPress);
            punchHeld[i] = held;
            punchPressure[i] = held ? Mathf.Max(axis, p.GetButton(action) ? 1f : 0f) : 0f;
        }

        Block = p.GetButton(RewiredActions.Block);
        Sprint = p.GetButton(RewiredActions.Sprint);
        BodyGuard = rewiredHasBodyGuard && p.GetButton(RewiredActions.BodyGuard);
        JumpDown = false;
        Keyboard kb = Keyboard.current;
        DebugPunchDown = kb != null && kb.jKey.wasPressedThisFrame;
        return true;
    }
#endif

    private static Vector2 Deadzone(Vector2 v)
    {
        float m = v.magnitude;
        if (m < StickDeadZone) return Vector2.zero;
        float scaled = Mathf.Clamp01((m - StickDeadZone) / (1f - StickDeadZone));
        return v / m * scaled;
    }

    // ------------------------------------------------------------------ Haptics & light bar

    /// <summary>
    /// Rumble the active gamepad. Peak-hold envelope with exponential decay: a new hit RAISES each motor to at
    /// least its level and the buzz falls away naturally — a jab can never cut a cross's rumble short.
    /// </summary>
    public static void Rumble(float low, float high, float seconds)
    {
        if (seconds <= 0f) return;
        lowPeak = Mathf.Max(lowPeak, Mathf.Clamp01(low));
        highPeak = Mathf.Max(highPeak, Mathf.Clamp01(high));
        rumbleUntil = Mathf.Max(rumbleUntil, Time.unscaledTime + seconds);

#if BOXER_REWIRED
        if (UsingRewired && rewiredPlayer != null)
        {
            rewiredPlayer.SetVibration(0, lowPeak, seconds);
            rewiredPlayer.SetVibration(1, highPeak, seconds);
            return;
        }
#endif
        rumbling = true;
    }

    public static void StopRumble()
    {
        rumbleUntil = 0f;
        lowPeak = 0f;
        highPeak = 0f;
        if (!rumbling) return;
        rumbling = false;
        Gamepad pad = Gamepad.current;
        if (pad != null) pad.ResetHaptics();
    }

    private static void UpdateRumble()
    {
        if (!rumbling) return;
        float dt = Time.unscaledDeltaTime;
        lowPeak *= Mathf.Exp(-dt / 0.16f);     // heavy motor rings longer
        highPeak *= Mathf.Exp(-dt / 0.10f);    // sharp motor snaps off
        if ((lowPeak < 0.02f && highPeak < 0.02f) || Time.unscaledTime >= rumbleUntil + 0.5f)
        {
            StopRumble();
            return;
        }
        Gamepad pad = Gamepad.current;
        if (pad != null) pad.SetMotorSpeeds(lowPeak, highPeak);
    }

    /// <summary>Set the DualShock light bar (ignored on other pads). Throttled, so call it every frame if you like.</summary>
    public static void SetLightBar(Color color)
    {
        if (Time.unscaledTime < nextLightBarTime) return;
        nextLightBarTime = Time.unscaledTime + 0.1f;
        if (((Vector4)color - (Vector4)lightBar).sqrMagnitude < 0.0004f) return;
        lightBar = color;

        if (Gamepad.current is DualShockGamepad dualShock) dualShock.SetLightBarColor(color);
    }
}
