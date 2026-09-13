using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Owns the van's seats and decides who is driving. Goes on the van root.
///
/// Each entry point is a separate <see cref="VanSeat"/> on its own child GameObject with
/// its own box collider, listed in <see cref="seats"/>. Exactly one of them is the driver
/// seat; filling it hands control to the <see cref="VanController"/>, and vacating it takes
/// control away. Passenger seats never drive, however many are occupied.
/// </summary>
public class Van : MonoBehaviour
{
    [Header("Parts")]
    [Tooltip("Movement script. Kept disabled until the driver seat is taken.")]
    [SerializeField] private VanController controller;

    [Tooltip("Every entry point, in any order. One must be the Driver.")]
    [SerializeField] private List<VanSeat> seats = new List<VanSeat>();

    [Header("Camera")]
    [Tooltip("Used by any seat that doesn't specify its own camera.")]
    [SerializeField] private CinemachineCamera fallbackCamera;

    [Header("Events")]
    [SerializeField] private UnityEvent<GameObject> onDriverTookControl;
    [SerializeField] private UnityEvent onDriverReleasedControl;

    /// <summary>The seat flagged as Driver, or null if none is configured.</summary>
    public VanSeat DriverSeat { get; private set; }

    /// <summary>The player currently driving, or null.</summary>
    public GameObject Driver => DriverSeat != null ? DriverSeat.Occupant : null;

    public bool IsBeingDriven => Driver != null;

    /// <summary>Movement script, so a HUD can read speed without caching a reference.</summary>
    public VanController Controller => controller;

    /// <summary>Seat camera fallback, read by <see cref="VanSeat"/>.</summary>
    public CinemachineCamera FallbackCamera => fallbackCamera;

    private void Awake()
    {
        if (controller == null) controller = GetComponentInChildren<VanController>(true);

        // Picking seats up automatically means adding a seat to the prefab can't be
        // forgotten in the list — the list stays there for explicit ordering.
        if (seats.Count == 0) GetComponentsInChildren(true, seats);

        int driverCount = 0;
        foreach (VanSeat seat in seats)
        {
            if (seat == null) continue;
            seat.Bind(this);

            if (seat.Role != VanSeatRole.Driver) continue;
            driverCount++;
            DriverSeat ??= seat;
        }

        if (driverCount == 0)
            Debug.LogWarning($"Van '{name}' has no seat set to Driver, so it can never be driven.", this);
        else if (driverCount > 1)
            Debug.LogWarning($"Van '{name}' has {driverCount} seats set to Driver. " +
                             $"Only '{DriverSeat.name}' will get control.", this);

        // Nothing drives until someone sits down.
        if (controller != null) controller.enabled = false;
        if (fallbackCamera != null) fallbackCamera.enabled = false;
    }

    // ---- Called by VanSeat -------------------------------------------------

    internal void SeatOccupied(VanSeat seat, GameObject player)
    {
        if (seat != DriverSeat) return;      // passengers are just cargo
        if (controller == null) return;

        controller.TakeControl(player);
        onDriverTookControl?.Invoke(player);
    }

    internal void SeatVacated(VanSeat seat, GameObject player)
    {
        if (seat != DriverSeat) return;
        if (controller == null) return;

        controller.ReleaseControl();
        onDriverReleasedControl?.Invoke();
    }

    /// <summary>Turn everyone out — handy for a cutscene or a despawn.</summary>
    public void EjectAll()
    {
        // Reverse order so a seat removing itself from play can't disturb the walk.
        for (int i = seats.Count - 1; i >= 0; i--)
            if (seats[i] != null && seats[i].IsOccupied) seats[i].Exit();
    }

    /// <summary>First unoccupied seat of that role, or null. For AI or a "nearest door" helper.</summary>
    public VanSeat FindFreeSeat(VanSeatRole role)
    {
        foreach (VanSeat seat in seats)
            if (seat != null && seat.Role == role && !seat.IsOccupied && seat.CanInteract)
                return seat;
        return null;
    }

    // ---- Seat cycling ------------------------------------------------------

    /// <summary>
    /// The next free seat after <paramref name="from"/> in list order, wrapping around:
    /// 0 -> 1 -> 2 -> 0. Null when <paramref name="from"/> is the only usable seat.
    /// Occupied and non-interactable seats are skipped rather than blocking the cycle.
    /// </summary>
    public VanSeat NextFreeSeatAfter(VanSeat from)
    {
        int start = seats.IndexOf(from);
        if (start < 0) return null;

        // Walk the whole ring once, starting one past `from`, and stop before coming back
        // to it — so the wrap is free but the search always terminates.
        for (int step = 1; step < seats.Count; step++)
        {
            VanSeat candidate = seats[(start + step) % seats.Count];
            if (candidate == null || candidate == from) continue;
            if (candidate.IsOccupied) continue;
            if (!candidate.CanInteract) continue;    // respects a locked or disabled seat
            return candidate;
        }
        return null;
    }

    /// <summary>
    /// Move a seated player between two seats without putting them on the ground. The player
    /// stays suspended throughout, so this is not exit-then-enter.
    /// </summary>
    public bool TransferOccupant(VanSeat from, VanSeat to)
    {
        if (from == null || to == null || from == to) return false;
        if (!from.IsOccupied || to.IsOccupied) return false;

        SeatedPlayer moving = from.VacateForTransfer();
        if (!moving.IsValid) return false;

        to.AcceptTransfer(moving, from);
        return true;
    }
}
