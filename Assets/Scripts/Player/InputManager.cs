using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

/// <summary>
/// Single entry point for player input. Owns the generated InputSystem_Actions
/// wrapper, keeps the continuous axes as polled state, and surfaces the buttons
/// as events so gameplay code never touches the Input System directly.
/// </summary>
public class InputManager : MonoBehaviour, InputSystem_Actions.IPlayerActions
{
    public static InputManager Instance { get; private set; }

    private InputSystem_Actions inputActions;
    private InputSystem_Actions.PlayerActions playerInput;

    public InputSystem_Actions Actions => inputActions;

    // ---- Continuous axes: poll these from Update/FixedUpdate ----------------
    /// <summary>WASD / left stick, already normalised to the -1..1 range.</summary>
    public Vector2 MoveInput { get; private set; }

    /// <summary>Mouse delta (pixels this frame) or right-stick deflection.</summary>
    public Vector2 LookInput { get; private set; }

    /// <summary>
    /// True when LookInput came from a mouse/pointer, i.e. it is already a
    /// per-frame delta and must NOT be multiplied by Time.deltaTime. False for a
    /// gamepad stick, which is a sustained -1..1 deflection and must be.
    /// </summary>
    public bool LookIsPointerDelta { get; private set; } = true;

    // ---- Held states: true for as long as the button is down ---------------
    public bool SprintHeld { get; private set; }
    public bool CrouchHeld { get; private set; }

    /// <summary>Jump held down. Vehicles reuse this as the handbrake.</summary>
    public bool JumpHeld { get; private set; }

    // ---- Button events -----------------------------------------------------
    // Declared as events so subscribers can only use += / -=; a stray '='
    // assignment would otherwise wipe every other listener with no compiler error.
    public event UnityAction JumpPressed;
    public event UnityAction JumpReleased;

    public event UnityAction SprintPressed;
    public event UnityAction SprintReleased;

    public event UnityAction CrouchPressed;
    public event UnityAction CrouchReleased;

    public event UnityAction InteractPressed;
    public event UnityAction InteractReleased;

    /// <summary>
    /// Cycle to the next vehicle seat. No input action is bound to this yet — raise it with
    /// <see cref="RaiseSwitchSeat"/> from a UI button or your own binding.
    ///
    /// To drive it from the Input System later: add a <c>SwitchSeat</c> button action to the
    /// Player map, let Unity regenerate the wrapper, then forward the new
    /// <c>OnSwitchSeat</c> callback to <see cref="RaiseSwitchSeat"/>.
    /// </summary>
    public event UnityAction SwitchSeatPressed;

    /// <summary>Raise <see cref="SwitchSeatPressed"/>. Safe to call when nothing is listening.</summary>
    public void RaiseSwitchSeat() => SwitchSeatPressed?.Invoke();

    private void Awake()
    {
        // Destroy the duplicate, not the manager that is already live and subscribed to.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        SetCursorLocked(true);

        inputActions = new InputSystem_Actions();
        playerInput = inputActions.Player;
        playerInput.AddCallbacks(this);
        playerInput.Enable();
    }

    private void OnDestroy()
    {
        if (inputActions != null)
        {
            playerInput.RemoveCallbacks(this);
            playerInput.Disable();
            inputActions.Dispose();
            inputActions = null;
        }
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Lock/hide the cursor for mouselook. Call with false when opening a menu,
    /// and disable the Player map too if you don't want movement to register.
    /// </summary>
    public void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    /// <summary>Turn gameplay input on/off wholesale (menus, cutscenes, death).</summary>
    public void SetPlayerInputEnabled(bool enabled)
    {
        if (inputActions == null) return;

        if (enabled)
        {
            playerInput.Enable();
        }
        else
        {
            playerInput.Disable();
            // Don't leave a stale axis pushing the player into a wall.
            MoveInput = Vector2.zero;
            LookInput = Vector2.zero;
            SprintHeld = false;
            CrouchHeld = false;
            JumpHeld = false;
        }
    }

    // ---- InputSystem_Actions.IPlayerActions --------------------------------

    public void OnMove(InputAction.CallbackContext context)
    {
        MoveInput = context.ReadValue<Vector2>();
    }

    public void OnLook(InputAction.CallbackContext context)
    {
        LookInput = context.ReadValue<Vector2>();
        if (context.control != null)
            LookIsPointerDelta = context.control.device is Pointer;
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            JumpHeld = true;
            JumpPressed?.Invoke();
        }
        else if (context.canceled)
        {
            JumpHeld = false;
            JumpReleased?.Invoke();
        }
    }

    public void OnSprint(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            SprintHeld = true;
            SprintPressed?.Invoke();
        }
        else if (context.canceled)
        {
            SprintHeld = false;
            SprintReleased?.Invoke();
        }
    }

    public void OnCrouch(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            CrouchHeld = true;
            CrouchPressed?.Invoke();
        }
        else if (context.canceled)
        {
            CrouchHeld = false;
            CrouchReleased?.Invoke();
        }
    }

    public void OnInteract(InputAction.CallbackContext context)
    {
        if (context.performed) InteractPressed?.Invoke();
        else if (context.canceled) InteractReleased?.Invoke();
    }
}
