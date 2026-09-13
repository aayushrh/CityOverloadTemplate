using System;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Events;

public enum VanSeatRole
{
    /// <summary>Taking this seat hands movement control to the van's <see cref="VanController"/>.</summary>
    Driver,

    /// <summary>Rides along. Never drives, no matter how many are occupied.</summary>
    Passenger
}

/// <summary>
/// A seated player and the component state that has to be handed back when they get out.
/// Passed between seats on a switch so the player is never un-suspended mid-transfer.
/// </summary>
public readonly struct SeatedPlayer
{
    public readonly GameObject Root;
    public readonly PlayerMovement Movement;
    public readonly CharacterController Controller;
    public readonly PlayerInteractor Interactor;
    public readonly Transform OriginalParent;
    public readonly CinemachineCamera Camera;

    public SeatedPlayer(GameObject root, PlayerMovement movement, CharacterController controller,
                        PlayerInteractor interactor, Transform originalParent, CinemachineCamera camera)
    {
        Root = root;
        Movement = movement;
        Controller = controller;
        Interactor = interactor;
        OriginalParent = originalParent;
        Camera = camera;
    }

    public bool IsValid => Root != null;
}

/// <summary>
/// One entry point on a vehicle. Put this on a child of the <see cref="Van"/> with its own
/// box collider on the <c>Interactable</c> layer — one per door.
///
/// Interact to climb in. While seated, Interact gets out and
/// <see cref="InputManager.SwitchSeatPressed"/> cycles to the next free seat.
///
/// The seat handles the player side (snap, suspend movement, swap camera). Whether it
/// <em>drives</em> is the <see cref="Van"/>'s decision, based on <see cref="Role"/>.
/// </summary>
[RequireComponent(typeof(Collider))]
public class VanSeat : InteractableBase
{
    [Header("Seat")]
    [Tooltip("Driver hands control to the VanController. Passenger just rides.")]
    [SerializeField] private VanSeatRole role = VanSeatRole.Passenger;

    [Tooltip("Where the player is placed while riding. A child of the van so it moves with it.")]
    [SerializeField] private Transform seatAnchor;

    [Tooltip("Where the player is put down on exit — put it outside the van's collider.")]
    [SerializeField] private Transform exitPoint;

    [Tooltip("Seconds to slide into the seat. 0 snaps instantly.")]
    [SerializeField, Min(0f)] private float seatBlendDuration = 0.25f;

    [Header("Camera")]
    [Tooltip("View for this seat. Falls back to the Van's camera when empty.")]
    [SerializeField] private CinemachineCamera seatCamera;

    [Tooltip("The player's CinemachineCamera. Found on the player if empty.")]
    [SerializeField] private CinemachineCamera playerCamera;

    [Header("Prompts")]
    [Tooltip("Leave empty for a default based on the role.")]
    [SerializeField] private string enterPrompt = "";
    [SerializeField] private string occupiedPrompt = "Occupied";

    [Header("Events")]
    [SerializeField] private UnityEvent<GameObject> onEntered;
    [SerializeField] private UnityEvent<GameObject> onExited;

    public event Action<GameObject> Entered;
    public event Action<GameObject> Exited;

    public VanSeatRole Role => role;

    /// <summary>The riding player, or null.</summary>
    public GameObject Occupant { get; private set; }

    public bool IsOccupied => Occupant != null;

    private Van van;
    private SeatedPlayer seated;

    /// <summary>This seat's view: its own camera, or the Van's fallback.</summary>
    internal CinemachineCamera ActiveCamera =>
        seatCamera != null ? seatCamera : (van != null ? van.FallbackCamera : null);

    private InputManager input;
    private int occupiedOnFrame = -1;
    private Coroutine seatRoutine;

    public override string Prompt
    {
        get
        {
            if (IsOccupied) return occupiedPrompt;
            if (!string.IsNullOrEmpty(enterPrompt)) return enterPrompt;
            return role == VanSeatRole.Driver ? "Drive" : "Ride";
        }
    }

    // Occupied: still focusable so the prompt can say so, but refuses the interaction.
    public override bool CanInteract => base.CanInteract && !IsOccupied;

    private void Awake()
    {
        // Works whichever of Van.Awake / VanSeat.Awake runs first.
        if (van == null) van = GetComponentInParent<Van>();

        if (seatAnchor == null)
            Debug.LogError($"{nameof(VanSeat)} '{name}' has no seat anchor — " +
                           $"add an empty child where the player should sit.", this);

        if (exitPoint == null)
            Debug.LogWarning($"{nameof(VanSeat)} '{name}' has no exit point, so the player will be " +
                             $"dropped at the seat — likely inside the van's collider.", this);
    }

    /// <summary>Called by <see cref="Van"/> during its Awake.</summary>
    internal void Bind(Van owner) => van = owner;

    protected override void OnInteract(GameObject interactor)
    {
        if (IsOccupied) return;
        Enter(interactor);
    }

    // ---- Entering from the world -------------------------------------------

    private void Enter(GameObject player)
    {
        if (seatAnchor == null) return;

        PlayerMovement movement = player.GetComponent<PlayerMovement>();
        CharacterController controller = player.GetComponent<CharacterController>();
        PlayerInteractor interactor = player.GetComponent<PlayerInteractor>();

        CinemachineCamera cam = playerCamera != null
            ? playerCamera
            : player.GetComponentInChildren<CinemachineCamera>(includeInactive: true);

        // Suspend the controller before reparenting: an enabled CharacterController
        // overwrites transform writes and will drag the player back out of the seat.
        if (movement != null) movement.enabled = false;
        if (controller != null) controller.enabled = false;

        // The interact ray starts at the player's eye, now inside the van, so it would focus
        // the van's own geometry. Exit and seat switching run off the raw input instead.
        if (interactor != null) interactor.enabled = false;

        Occupy(new SeatedPlayer(player, movement, controller, interactor,
                                player.transform.parent, cam));

        // Hand the view to this seat. Only now that the seat camera is confirmed: disabling
        // the only active camera would leave the brain nothing to blend to.
        CinemachineCamera mine = ActiveCamera;
        if (mine != null)
        {
            mine.enabled = true;
            if (seated.Camera != null) seated.Camera.enabled = false;
        }

        OnFocusExit();                       // nothing is aiming at this any more
        onEntered?.Invoke(player);
        Entered?.Invoke(player);
    }

    /// <summary>Get the occupant out. Safe from a cutscene, a UI button, or Van.EjectAll.</summary>
    public void Exit()
    {
        if (!IsOccupied) return;

        SeatedPlayer leaving = seated;
        Vacate();

        // Hand the view back before the player starts steering it again.
        CinemachineCamera mine = ActiveCamera;
        if (mine != null)
        {
            mine.enabled = false;
            if (leaving.Camera != null) leaving.Camera.enabled = true;
        }

        GameObject player = leaving.Root;
        player.transform.SetParent(leaving.OriginalParent, worldPositionStays: true);

        Transform target = exitPoint != null ? exitPoint : seatAnchor;

        if (leaving.Controller != null) leaving.Controller.enabled = true;
        if (leaving.Movement != null)
        {
            leaving.Movement.enabled = true;
            // SnapTo resyncs the cached yaw and clears momentum, so mouselook doesn't whip
            // back to whatever the player was facing before it got in.
            leaving.Movement.SnapTo(target.position, target.eulerAngles.y);
        }
        else
        {
            player.transform.SetPositionAndRotation(target.position,
                                                    Quaternion.Euler(0f, target.eulerAngles.y, 0f));
        }

        if (leaving.Interactor != null) leaving.Interactor.enabled = true;

        onExited?.Invoke(player);
        Exited?.Invoke(player);
    }

    // ---- Switching seats ---------------------------------------------------

    /// <summary>
    /// Hand the occupant to another seat. Called by <see cref="Van.TransferOccupant"/> —
    /// the player stays suspended throughout, never touching the ground.
    /// </summary>
    internal SeatedPlayer VacateForTransfer()
    {
        SeatedPlayer moving = seated;
        Vacate();
        return moving;
    }

    /// <summary>Receive an already-suspended occupant from <paramref name="from"/>.</summary>
    internal void AcceptTransfer(in SeatedPlayer moving, VanSeat from)
    {
        CinemachineCamera previous = from != null ? from.ActiveCamera : null;

        Occupy(moving);

        // Seats often share one camera. Switch in that order, and skip the toggle entirely
        // when it's the same object, so the brain never sees a frame with no active camera.
        CinemachineCamera mine = ActiveCamera;
        if (mine != null) mine.enabled = true;
        if (previous != null && previous != mine) previous.enabled = false;
    }

    // ---- Shared occupancy core ---------------------------------------------

    private void Occupy(in SeatedPlayer player)
    {
        seated = player;
        Occupant = player.Root;
        occupiedOnFrame = Time.frameCount;

        player.Root.transform.SetParent(seatAnchor, worldPositionStays: true);

        if (seatBlendDuration > 0f)
        {
            if (seatRoutine != null) StopCoroutine(seatRoutine);
            seatRoutine = StartCoroutine(BlendIntoSeat(player.Root.transform));
        }
        else
        {
            player.Root.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        BindSeatInput();

        // `?.` uses real null; a destroyed Van passes that check and throws, so use the
        // Unity null operator instead. The Van decides whether this grants control.
        if (van != null) van.SeatOccupied(this, player.Root);
    }

    private void Vacate()
    {
        if (seatRoutine != null)
        {
            StopCoroutine(seatRoutine);
            seatRoutine = null;
        }

        UnbindSeatInput();

        GameObject player = Occupant;
        Occupant = null;
        seated = default;

        // Release control BEFORE the player is given back its own input, so there is no
        // frame where both the van and the player are reading MoveInput.
        if (van != null) van.SeatVacated(this, player);
    }

    private System.Collections.IEnumerator BlendIntoSeat(Transform player)
    {
        // Lerp in the seat's local space, so a van that is already moving doesn't
        // leave the player trailing behind during the blend.
        Vector3 fromPos = player.localPosition;
        Quaternion fromRot = player.localRotation;

        for (float t = 0f; t < seatBlendDuration; t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / seatBlendDuration);
            player.SetLocalPositionAndRotation(Vector3.Lerp(fromPos, Vector3.zero, k),
                                               Quaternion.Slerp(fromRot, Quaternion.identity, k));
            yield return null;
        }

        player.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        seatRoutine = null;
    }

    // ---- Input while seated ------------------------------------------------

    private void BindSeatInput()
    {
        if (input != null) return;

        if (InputManager.Instance == null)
        {
            Debug.LogWarning($"{nameof(VanSeat)} '{name}': no InputManager in the scene, so the " +
                             $"player can't interact to get out. Add one.", this);
            return;
        }

        input = InputManager.Instance;
        input.InteractPressed += OnInteractPressed;
        input.SwitchSeatPressed += OnSwitchSeatPressed;
    }

    private void UnbindSeatInput()
    {
        if (input == null) return;
        input.InteractPressed -= OnInteractPressed;
        input.SwitchSeatPressed -= OnSwitchSeatPressed;
        input = null;
    }

    private void OnInteractPressed()
    {
        // The press that got the player in must not also get it out. C# events invoke a
        // snapshot of the list so a handler added mid-invoke shouldn't fire this round —
        // but relying on that is the kind of thing that breaks quietly.
        if (Time.frameCount == occupiedOnFrame) return;
        Exit();
    }

    private void OnSwitchSeatPressed()
    {
        if (Time.frameCount == occupiedOnFrame) return;
        if (van == null) return;

        VanSeat next = van.NextFreeSeatAfter(this);
        if (next == null) return;            // solo seat, or every other one is taken

        van.TransferOccupant(this, next);
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        // A seat switched off mid-ride must not leave the player parented to it.
        if (IsOccupied) Exit();
    }

    private void OnDestroy()
    {
        if (IsOccupied) Exit();
        UnbindSeatInput();
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = role == VanSeatRole.Driver ? Color.green : Color.cyan;
        if (seatAnchor != null)
        {
            Gizmos.DrawWireSphere(seatAnchor.position, 0.25f);
            Gizmos.DrawRay(seatAnchor.position, seatAnchor.forward * 0.6f);
        }
        if (exitPoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(exitPoint.position, 0.25f);
            Gizmos.DrawRay(exitPoint.position, exitPoint.forward * 0.6f);
        }
    }
}
