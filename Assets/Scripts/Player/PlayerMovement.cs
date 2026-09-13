using UnityEngine;

/// <summary>
/// First person character controller driven entirely by <see cref="InputManager"/>.
///
/// Expected hierarchy:
///   Player            -> CharacterController + this script
///     CameraPivot     -> empty transform at eye height (pitch rotates here)
///       Main Camera   -> local position zero, local rotation identity
///
/// The body yaws, the pivot pitches. Never put pitch on the CharacterController
/// itself or the capsule tips over and the collision sweep goes sideways.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Transform that holds the camera. Pitch is applied here. Auto-found if left empty.")]
    [SerializeField] private Transform cameraPivot;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 0.12f;
    [Tooltip("Degrees per second at full stick deflection.")]
    [SerializeField] private float gamepadSensitivity = 180f;
    [SerializeField] private bool invertY = false;
    [SerializeField] private float minPitch = -89f;
    [SerializeField] private float maxPitch = 89f;

    [Header("Speed")]
    [SerializeField] private float walkSpeed = 4.5f;
    [SerializeField] private float sprintSpeed = 7.5f;
    [SerializeField] private float crouchSpeed = 2.0f;
    [Tooltip("How fast horizontal velocity reaches the target speed while grounded.")]
    [SerializeField] private float groundAcceleration = 14f;
    [Tooltip("How fast it bleeds off when there is no input.")]
    [SerializeField] private float groundDeceleration = 18f;
    [Tooltip("Air control. Low values make jumps feel committed.")]
    [SerializeField] private float airAcceleration = 3.5f;

    [Header("Jump & Gravity")]
    [Tooltip("Apex height in metres. Jump force is derived from this and gravity.")]
    [SerializeField] private float jumpHeight = 1.1f;
    [SerializeField] private float gravity = -22f;
    [SerializeField] private float terminalVelocity = -45f;
    [Tooltip("Grace period after walking off a ledge during which a jump still works.")]
    [SerializeField] private float coyoteTime = 0.12f;
    [Tooltip("A jump pressed this long before landing fires on touchdown.")]
    [SerializeField] private float jumpBuffer = 0.12f;

    [Header("Crouch")]
    [SerializeField] private bool holdToCrouch = true;
    [SerializeField] private float standingHeight = 1.8f;
    [SerializeField] private float crouchingHeight = 1.1f;
    [SerializeField] private float crouchTransitionSpeed = 10f;

    [Header("Ground Check")]
    [Tooltip("Layers treated as ground. Exclude the player's own layer.")]
    [SerializeField] private LayerMask groundMask = ~0;

    private CharacterController controller;
    private InputManager input;

    private Vector3 horizontalVelocity;
    private float verticalVelocity;

    private float yaw;
    private float pitch;

    private float coyoteTimer;
    private float jumpBufferTimer;

    private bool isCrouching;
    private bool crouchToggleState;
    private float targetHeight;

    private float eyeHeightRatio = 0.9f;

    private bool grounded;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();

        if (cameraPivot == null)
        {
            Camera cam = GetComponentInChildren<Camera>();
            if (cam != null) cameraPivot = cam.transform;
        }
        if (cameraPivot == null)
            Debug.LogError($"{nameof(PlayerMovement)} on '{name}' has no camera pivot and found no child camera.", this);

        controller.height = standingHeight;
        controller.center = new Vector3(0f, standingHeight * 0.5f, 0f);
        targetHeight = standingHeight;

        // Remember where the eyes sit relative to the capsule so the pivot can
        // ride up and down with the crouch instead of clipping into the floor.
        if (cameraPivot != null && standingHeight > 0f)
            eyeHeightRatio = Mathf.Clamp01(cameraPivot.localPosition.y / standingHeight);

        yaw = transform.eulerAngles.y;
    }

    private void OnEnable()
    {
        // InputManager may come alive in the same frame, so bind lazily in Update
        // if it isn't ready yet rather than silently missing the subscription.
        TryBind();
    }

    private void OnDisable()
    {
        if (input != null)
        {
            input.JumpPressed -= OnJumpPressed;
            input.CrouchPressed -= OnCrouchPressed;
            input = null;
        }
    }

    private void TryBind()
    {
        if (input != null || InputManager.Instance == null) return;

        input = InputManager.Instance;
        input.JumpPressed += OnJumpPressed;
        input.CrouchPressed += OnCrouchPressed;
    }

    private void Update()
    {
        TryBind();
        if (input == null) return;

        float dt = Time.deltaTime;

        grounded = controller.isGrounded || GroundProbe();

        HandleLook(dt);
        HandleCrouch(dt);
        HandleGroundAndTimers(dt);
        HandleJump();
        HandleHorizontal(dt);

        Vector3 motion = horizontalVelocity;
        motion.y = verticalVelocity;
        controller.Move(motion * dt);
    }

    // ------------------------------------------------------------------------

    private void HandleLook(float dt)
    {
        Vector2 look = input.LookInput;
        if (look.sqrMagnitude > 0f)
        {
            // Mouse delta is already per-frame; stick deflection is per-second.
            float scale = input.LookIsPointerDelta ? mouseSensitivity : gamepadSensitivity * dt;
            yaw += look.x * scale;
            pitch += (invertY ? look.y : -look.y) * scale;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (cameraPivot != null)
            cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    private void HandleCrouch(float dt)
    {
        bool wantsCrouch = holdToCrouch ? input.CrouchHeld : crouchToggleState;

        if (wantsCrouch)
        {
            isCrouching = true;
        }
        else if (isCrouching && CanStandUp())
        {
            isCrouching = false;
        }

        targetHeight = isCrouching ? crouchingHeight : standingHeight;

        if (!Mathf.Approximately(controller.height, targetHeight))
        {
            float h = Mathf.Lerp(controller.height, targetHeight, crouchTransitionSpeed * dt);
            if (Mathf.Abs(h - targetHeight) < 0.005f) h = targetHeight;

            // Keep the feet planted: centre is always half the height up.
            controller.height = h;
            controller.center = new Vector3(0f, h * 0.5f, 0f);

            if (cameraPivot != null)
            {
                Vector3 p = cameraPivot.localPosition;
                p.y = h * eyeHeightRatio;
                cameraPivot.localPosition = p;
            }
        }
    }

    private bool CanStandUp()
    {
        float radius = controller.radius * 0.95f;
        // Sweep from the top of the crouched capsule up to standing height.
        Vector3 start = transform.position + Vector3.up * (controller.height - radius);
        float distance = standingHeight - controller.height;
        if (distance <= 0f) return true;

        return !Physics.SphereCast(start, radius, Vector3.up, out _, distance,
                                   groundMask, QueryTriggerInteraction.Ignore);
    }

    private bool GroundProbe()
    {
        // CharacterController.isGrounded flickers on slopes and step edges, so
        // back it up with an explicit check just under the capsule.
        float radius = controller.radius * 0.95f;
        Vector3 origin = transform.position + Vector3.up * (radius + controller.skinWidth);
        return Physics.SphereCast(origin, radius, Vector3.down, out _,
                                  controller.skinWidth + 0.1f,
                                  groundMask, QueryTriggerInteraction.Ignore);
    }

    private void HandleGroundAndTimers(float dt)
    {
        if (grounded)
        {
            coyoteTimer = coyoteTime;

            // Small constant push keeps the controller glued to slopes and stops
            // isGrounded from dropping out on the way down stairs.
            if (verticalVelocity < 0f)
                verticalVelocity = -2f;
        }
        else
        {
            coyoteTimer -= dt;
            verticalVelocity += gravity * dt;
            verticalVelocity = Mathf.Max(verticalVelocity, terminalVelocity);
        }

        jumpBufferTimer -= dt;
    }

    private void HandleJump()
    {
        if (jumpBufferTimer <= 0f || coyoteTimer <= 0f) return;
        if (isCrouching && !CanStandUp()) return;

        // v = sqrt(2 * g * h)
        verticalVelocity = Mathf.Sqrt(2f * Mathf.Abs(gravity) * jumpHeight);

        jumpBufferTimer = 0f;
        coyoteTimer = 0f;
    }

    private void HandleHorizontal(float dt)
    {
        Vector2 move = Vector2.ClampMagnitude(input.MoveInput, 1f);
        Vector3 wish = transform.right * move.x + transform.forward * move.y;

        float targetSpeed = CurrentSpeed();
        Vector3 target = wish * targetSpeed;

        float rate = grounded
            ? (wish.sqrMagnitude > 0.0001f ? groundAcceleration : groundDeceleration)
            : airAcceleration;

        // Exponential smoothing: frame-rate independent, and a higher rate simply
        // means "snappier". 14 settles in roughly a fifth of a second.
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, target, 1f - Mathf.Exp(-rate * dt));

        if (horizontalVelocity.sqrMagnitude < 0.0004f) horizontalVelocity = Vector3.zero;
    }

    private float CurrentSpeed()
    {
        if (isCrouching) return crouchSpeed;
        if (input.SprintHeld) return sprintSpeed;
        return walkSpeed;
    }

    // ---- Input callbacks ---------------------------------------------------

    private void OnJumpPressed() => jumpBufferTimer = jumpBuffer;

    private void OnCrouchPressed()
    {
        if (!holdToCrouch) crouchToggleState = !crouchToggleState;
    }

    // ---- External repositioning (teleports, vehicles) -----------------------

    /// <summary>
    /// The transform pitch is applied to. Other systems (interaction raycasts, a
    /// CinemachineCamera parented here) use this as the player's eye transform.
    /// </summary>
    public Transform CameraPivot => cameraPivot;

    /// <summary>
    /// Move the player without the CharacterController fighting it, and resync the
    /// controller's cached yaw so mouselook doesn't snap back on the next frame.
    /// Use this for teleports and for getting out of a vehicle — writing
    /// <c>transform.position</c> directly leaves the internal state stale.
    /// </summary>
    public void SnapTo(Vector3 position, float yawDegrees)
    {
        if (controller == null) controller = GetComponent<CharacterController>();

        // An enabled CharacterController overwrites transform writes during its own
        // update, so take it out of the loop for the move.
        bool wasEnabled = controller.enabled;
        controller.enabled = false;
        transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f));
        controller.enabled = wasEnabled;

        yaw = yawDegrees;
        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        jumpBufferTimer = 0f;
        coyoteTimer = 0f;
    }

    /// <summary>Overload that takes yaw from a transform's facing.</summary>
    public void SnapTo(Transform target) => SnapTo(target.position, target.eulerAngles.y);

    // ---- Read-only state for other systems (footsteps, animation, HUD) -----

    public Vector3 Velocity => horizontalVelocity + Vector3.up * verticalVelocity;
    public float HorizontalSpeed => horizontalVelocity.magnitude;
    public bool IsCrouching => isCrouching;
    public bool IsSprinting => !isCrouching && input != null && input.SprintHeld && HorizontalSpeed > 0.1f;
    public bool IsOnGround => grounded;
}
