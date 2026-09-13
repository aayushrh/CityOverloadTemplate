using System;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Interact to climb into the van: the player is snapped to a seat anchor, its movement
/// is suspended, and the view blends to the van's CinemachineCamera. Interact again to
/// get out.
///
/// Put this on the van root (or a child carrying the interact collider), with the whole
/// thing on the <c>Interactable</c> layer.
///
/// The van is a ride-along seat — it does not drive. <see cref="Occupant"/> and the
/// <see cref="Entered"/>/<see cref="Exited"/> events are the hooks a driving script
/// would use to start consuming <c>InputManager.Instance.MoveInput</c>.
/// </summary>
public class VanInteractable : InteractableBase
{
    [Header("Seat")]
    [Tooltip("Where the player is placed while riding. A child of the van so it moves with it.")]
    [SerializeField] private Transform seatAnchor;

    [Tooltip("Where the player is put down on exit. Defaults to just left of the seat.")]
    [SerializeField] private Transform exitPoint;

    [Tooltip("Seconds to slide into the seat. 0 snaps instantly.")]
    [SerializeField, Min(0f)] private float seatBlendDuration = 0.25f;

    [Header("Camera")]
    [Tooltip("The van's CinemachineCamera. Enabled on entry so the brain blends to it.")]
    [SerializeField] private CinemachineCamera vanCamera;

    [Tooltip("The player's CinemachineCamera. Disabled while riding. Found via the player if empty.")]
    [SerializeField] private CinemachineCamera playerCamera;

    [Header("Prompts")]
    [SerializeField] private string enterPrompt = "Enter van";
    [SerializeField] private string occupiedPrompt = "Occupied";

    [Header("Events")]
    [SerializeField] private UnityEvent<GameObject> onEntered;
    [SerializeField] private UnityEvent<GameObject> onExited;

    /// <summary>Fires with the player GameObject when someone gets in.</summary>
    public event Action<GameObject> Entered;

    /// <summary>Fires with the player GameObject when someone gets out.</summary>
    public event Action<GameObject> Exited;

    /// <summary>The riding player, or null.</summary>
    public GameObject Occupant { get; private set; }

    public bool IsOccupied => Occupant != null;

    // Cached state belonging to the occupant, restored on exit.
    private PlayerMovement occupantMovement;
    private PlayerInteractor occupantInteractor;
    private CharacterController occupantController;
    private Transform occupantOriginalParent;
    private CinemachineCamera occupantCamera;

    private InputManager input;
    private int enteredOnFrame = -1;
    private Coroutine seatRoutine;

    public override string Prompt => IsOccupied ? occupiedPrompt : enterPrompt;

    // Occupied by someone else: still focusable so the prompt can say so, but refuses.
    public override bool CanInteract => base.CanInteract && !IsOccupied;

    private void Awake()
    {
        if (seatAnchor == null)
        {
            Debug.LogError($"{nameof(VanInteractable)} on '{name}' has no seat anchor — " +
                           $"add an empty child where the player should sit.", this);
        }

        // Start with the van's view off so the player camera wins until someone gets in.
        if (vanCamera != null) vanCamera.enabled = false;
    }

    protected override void OnInteract(GameObject interactor)
    {
        if (IsOccupied) return;
        Enter(interactor);
    }

    // ------------------------------------------------------------------------

    private void Enter(GameObject player)
    {
        occupantMovement = player.GetComponent<PlayerMovement>();
        occupantController = player.GetComponent<CharacterController>();
        occupantInteractor = player.GetComponent<PlayerInteractor>();

        if (seatAnchor == null) return;

        Occupant = player;
        enteredOnFrame = Time.frameCount;

        // Suspend the controller before reparenting: an enabled CharacterController
        // overwrites transform writes and will drag the player back out of the seat.
        if (occupantMovement != null) occupantMovement.enabled = false;
        if (occupantController != null) occupantController.enabled = false;

        // The interactor's ray starts at the player's eye, which is now inside the van —
        // it would focus the van's own geometry. Exit is handled off the raw input instead.
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

        OnFocusExit();          // clear the highlight; nothing is aiming at it now
        onEntered?.Invoke(player);
        Entered?.Invoke(player);
    }

    /// <summary>Get the current occupant out. Safe to call from a cutscene or a UI button.</summary>
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
        // Enabling/disabling is enough: CinemachineBrain picks the highest-priority
        // ACTIVE camera and blends to it using its Default Blend. No priority bookkeeping.
        if (occupantCamera == null)
        {
            occupantCamera = playerCamera != null
                ? playerCamera
                : player.GetComponentInChildren<CinemachineCamera>(includeInactive: true);
        }

        // If there is no van camera, leave the player's alone — disabling the only active
        // camera hands the brain nothing to blend to and the screen goes black.
        if (vanCamera == null) return;

        vanCamera.enabled = toVan;
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

    private void OnDestroy()
    {
        // The player is parented to the seat. Destroying the van without letting it out
        // would take the player down with it.
        if (IsOccupied) Exit();
        UnbindExitInput();
    }

    private void OnDrawGizmosSelected()
    {
        if (seatAnchor != null)
        {
            Gizmos.color = Color.cyan;
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
