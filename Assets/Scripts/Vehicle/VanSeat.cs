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
/// One entry point on a vehicle. Put this on a child of the <see cref="Van"/> with its own
/// box collider on the <c>Interactable</c> layer — one per door. Interact to climb in,
/// interact again to get out.
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

    // Occupant state, restored on exit.
    private PlayerMovement occupantMovement;
    private PlayerInteractor occupantInteractor;
    private CharacterController occupantController;
    private Transform occupantOriginalParent;
    private CinemachineCamera occupantCamera;

    private CinemachineCamera ActiveCamera =>
        seatCamera != null ? seatCamera : (van != null ? van.FallbackCamera : null);

    private InputManager input;
    private int enteredOnFrame = -1;
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

    // ------------------------------------------------------------------------

    private void Enter(GameObject player)
    {
        if (seatAnchor == null) return;

        occupantMovement = player.GetComponent<PlayerMovement>();
        occupantController = player.GetComponent<CharacterController>();
        occupantInteractor = player.GetComponent<PlayerInteractor>();

        Occupant = player;
        enteredOnFrame = Time.frameCount;

        // Suspend the controller before reparenting: an enabled CharacterController
        // overwrites transform writes and will drag the player back out of the seat.
        if (occupantMovement != null) occupantMovement.enabled = false;
        if (occupantController != null) occupantController.enabled = false;

        // The interact ray starts at the player's eye, now inside the van, so it would
        // focus the van's own geometry. Exit runs off the raw input instead.
        if (occupantInteractor != null) occupantInteractor.enabled = false;

        occupantOriginalParent = player.transform.parent;
        player.transform.SetParent(seatAnchor, worldPositionStays: true);

        if (seatBlendDuration > 0f)
        {
            if (seatRoutine != null) StopCoroutine(seatRoutine);
            seatRoutine = StartCoroutine(BlendIntoSeat(player.transform));
        }
        else
        {
            player.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        SwapCamera(player, toVan: true);
        BindExitInput();

        OnFocusExit();                       // nothing is aiming at this any more
        // `?.` uses real null; a destroyed Van passes that check and throws, so use the
        // Unity null operator instead.
        if (van != null) van.SeatOccupied(this, player);   // the Van decides if this grants control
        onEntered?.Invoke(player);
        Entered?.Invoke(player);
    }

    /// <summary>Get the occupant out. Safe to call from a cutscene, a UI button, or Van.EjectAll.</summary>
    public void Exit()
    {
        if (!IsOccupied) return;

        GameObject player = Occupant;
        Occupant = null;

        if (seatRoutine != null)
        {
            StopCoroutine(seatRoutine);
            seatRoutine = null;
        }

        UnbindExitInput();

        // Release control BEFORE re-enabling the player, so the van stops reading input
        // in the same frame the player starts reading it again.
        if (van != null) van.SeatVacated(this, player);

        SwapCamera(player, toVan: false);

        player.transform.SetParent(occupantOriginalParent, worldPositionStays: true);

        Transform target = exitPoint != null ? exitPoint : seatAnchor;

        if (occupantController != null) occupantController.enabled = true;
        if (occupantMovement != null)
        {
            occupantMovement.enabled = true;
            // SnapTo resyncs the cached yaw and clears momentum, so mouselook doesn't
            // whip back to whatever the player was facing before it got in.
            occupantMovement.SnapTo(target.position, target.eulerAngles.y);
        }
        else
        {
            player.transform.SetPositionAndRotation(target.position,
                                                    Quaternion.Euler(0f, target.eulerAngles.y, 0f));
        }

        if (occupantInteractor != null) occupantInteractor.enabled = true;

        occupantMovement = null;
        occupantController = null;
        occupantInteractor = null;
        occupantCamera = null;
        occupantOriginalParent = null;

        onExited?.Invoke(player);
        Exited?.Invoke(player);
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

    // ---- Camera ------------------------------------------------------------

    private void SwapCamera(GameObject player, bool toVan)
    {
        CinemachineCamera target = ActiveCamera;

        // With no van-side camera, leave the player's alone — disabling the only active
        // camera hands the brain nothing to blend to and the screen goes black.
        if (target == null) return;

        if (occupantCamera == null)
        {
            occupantCamera = playerCamera != null
                ? playerCamera
                : player.GetComponentInChildren<CinemachineCamera>(includeInactive: true);
        }

        // Enable/disable is enough: CinemachineBrain picks the highest-priority ACTIVE
        // camera and blends using its Default Blend. No priority bookkeeping to drift.
        target.enabled = toVan;
        if (occupantCamera != null) occupantCamera.enabled = !toVan;
    }

    // ---- Exit input --------------------------------------------------------

    private void BindExitInput()
    {
        if (InputManager.Instance == null) return;
        input = InputManager.Instance;
        input.InteractPressed += OnExitPressed;
    }

    private void UnbindExitInput()
    {
        if (input == null) return;
        input.InteractPressed -= OnExitPressed;
        input = null;
    }

    private void OnExitPressed()
    {
        // The press that got the player in must not also get it out. C# events invoke a
        // snapshot of the list so this shouldn't fire on the entry frame anyway — but
        // relying on that is the kind of thing that breaks quietly.
        if (Time.frameCount == enteredOnFrame) return;
        Exit();
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
        UnbindExitInput();
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
