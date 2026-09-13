using System;
using UnityEngine;

/// <summary>
/// Casts forward from the camera each frame, tracks the focused <see cref="IInteractable"/>,
/// and forwards the Interact button to it.
///
/// Put this on the Player root alongside <see cref="PlayerMovement"/>.
/// </summary>
public class PlayerInteractor : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Where the ray starts and which way it points. Auto-found from the child camera.")]
    [SerializeField] private Transform rayOrigin;

    [Header("Reach")]
    [SerializeField] private float range = 3f;

    [Tooltip("0 = a thin raycast. A small radius (0.05-0.15) makes aiming at small props forgiving.")]
    [SerializeField] private float castRadius = 0.1f;

    [Header("Layers")]
    [Tooltip("Everything the ray can hit: the Interactable layer PLUS anything that " +
             "should block line of sight. Without the blockers you can interact through walls.")]
    [SerializeField] private LayerMask raycastMask = ~0;

    [Tooltip("Which of those layers actually count as interactable. Set this to Interactable.")]
    [SerializeField] private LayerMask interactableMask = 1 << 8;

    [Tooltip("Treat triggers as interactable surfaces. Off means trigger colliders are ignored.")]
    [SerializeField] private bool hitTriggers = false;

    [Header("Debug")]
    [Tooltip("Log what the ray hit and why it was or wasn't accepted. Turn this on first when " +
             "interaction silently does nothing — it usually names a layer.")]
    [SerializeField] private bool logHits = false;

    /// <summary>
    /// Fires when the focused target changes, with null when focus is lost.
    /// Subscribe from the HUD to show and hide the prompt.
    /// </summary>
    public event Action<IInteractable> FocusChanged;

    /// <summary>Fires after a successful interaction — good hook for a sound or a nudge.</summary>
    public event Action<IInteractable> Interacted;

    /// <summary>Currently focused target, or null.</summary>
    public IInteractable Focused { get; private set; }

    /// <summary>The focused target's component, so you can read its transform or position.</summary>
    public Component FocusedComponent { get; private set; }

    /// <summary>Convenience for a HUD that just polls: the prompt text, or empty.</summary>
    public string FocusPrompt => Focused != null ? Focused.Prompt : string.Empty;

    private InputManager input;

    // 16 is generous for a 3m cast. If it ever fills, the nearest among what came back is
    // still used — the cast just can't promise it saw everything.
    private readonly RaycastHit[] hits = new RaycastHit[16];

    private string lastLogged;

    // FindTarget runs every frame, so log only when the outcome actually changes —
    // otherwise turning this on buries the console.
    private void LogOnce(string message, UnityEngine.Object context = null)
    {
        if (!logHits || message == lastLogged) return;
        lastLogged = message;
        Debug.Log($"[{name}] {message}", context != null ? context : this);
    }

    private QueryTriggerInteraction TriggerMode =>
        hitTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;

    private void Awake()
    {
        if (rayOrigin == null)
        {
            Camera cam = GetComponentInChildren<Camera>();
            if (cam != null) rayOrigin = cam.transform;
        }
        if (rayOrigin == null)
        {
            // With Cinemachine the real Camera lives outside the player hierarchy (the brain
            // owns its transform), so fall back to the movement script's eye transform.
            var movement = GetComponent<PlayerMovement>();
            if (movement != null) rayOrigin = movement.CameraPivot;
        }
        if (rayOrigin == null)
            Debug.LogError($"{nameof(PlayerInteractor)} on '{name}' has no ray origin: assign one, " +
                           $"or give the player a child camera or a PlayerMovement with a camera pivot.", this);
    }

    private void OnEnable() => TryBind();

    private void OnDisable()
    {
        if (input != null)
        {
            input.InteractPressed -= OnInteractPressed;
            input = null;
        }
        ClearFocus();
    }

    private void TryBind()
    {
        if (input != null || InputManager.Instance == null) return;

        input = InputManager.Instance;
        input.InteractPressed += OnInteractPressed;
    }

    private void Update()
    {
        TryBind();
        if (rayOrigin == null) return;

        SetFocus(FindTarget(out Component component), component);
    }

    // ------------------------------------------------------------------------

    private IInteractable FindTarget(out Component component)
    {
        component = null;

        // One cast against interactables AND blockers, so the nearest thing wins and a wall
        // in between blocks the interaction instead of being cast straight through.
        //
        // Gather all hits rather than using the single-hit overloads: the ray starts at the
        // player's own eye, inside its own colliders, and those have to be skipped BY
        // HIERARCHY. Filtering the player out by layer instead would mean a player left on
        // Default silently blinds the ray to every other Default object in the scene.
        int count = castRadius > 0f
            ? Physics.SphereCastNonAlloc(rayOrigin.position, castRadius, rayOrigin.forward,
                                         hits, range, raycastMask, TriggerMode)
            : Physics.RaycastNonAlloc(rayOrigin.position, rayOrigin.forward,
                                      hits, range, raycastMask, TriggerMode);
        if (count == 0)
        {
            LogOnce($"interact ray hit nothing within {range}m.");
            return null;
        }

        Collider nearest = null;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Collider c = hits[i].collider;
            if (c == null) continue;

            // Our own body, including the capsule the eye sits inside.
            if (c.transform.IsChildOf(transform)) continue;

            if (hits[i].distance >= nearestDistance) continue;
            nearestDistance = hits[i].distance;
            nearest = c;
        }

        if (nearest == null)
        {
            LogOnce("interact ray hit only the player's own colliders.");
            return null;
        }

        // The nearest surface isn't interactable: either it's a wall in the way, or the
        // target is sitting on the wrong layer.
        if ((interactableMask.value & (1 << nearest.gameObject.layer)) == 0)
        {
            LogOnce($"nearest hit '{nearest.name}' is on layer {nearest.gameObject.layer} " +
                    $"({LayerMask.LayerToName(nearest.gameObject.layer)}), which is not in " +
                    $"Interactable Mask. Either it's blocking the view, or it needs moving to " +
                    $"an interactable layer.", nearest);
            return null;
        }

        // Search upward so a child collider on a composite prop still resolves to the
        // component that owns the behaviour.
        var interactable = nearest.GetComponentInParent<IInteractable>();

        // Require a Component so focus can be validated against Unity's lifetime checks.
        component = interactable as Component;

        if (component == null)
        {
            LogOnce($"'{nearest.name}' is on an interactable layer but has no IInteractable " +
                    $"on it or any parent.", nearest);
            return null;
        }

        LogOnce($"focused '{component.name}' at {nearestDistance:0.00}m.", component);
        return interactable;
    }

    private void SetFocus(IInteractable next, Component nextComponent)
    {
        // A destroyed MonoBehaviour still satisfies `interface != null`, so validate through
        // the Component and its Unity lifetime check. Drop it without calling OnFocusExit —
        // touching a destroyed object would throw.
        if (Focused != null && FocusedComponent == null)
        {
            Focused = null;
            FocusedComponent = null;
            FocusChanged?.Invoke(null);
        }

        if (ReferenceEquals(next, Focused)) return;

        Focused?.OnFocusExit();

        Focused = next;
        FocusedComponent = nextComponent;
        Focused?.OnFocusEnter();

        FocusChanged?.Invoke(Focused);
    }

    private void ClearFocus() => SetFocus(null, null);

    private void OnInteractPressed()
    {
        if (Focused == null || FocusedComponent == null) return;
        if (!Focused.CanInteract) return;

        Focused.Interact(gameObject);
        Interacted?.Invoke(Focused);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform origin = rayOrigin != null ? rayOrigin : transform;
        Gizmos.color = Focused != null ? Color.green : Color.gray;

        Vector3 end = origin.position + origin.forward * range;
        Gizmos.DrawLine(origin.position, end);
        if (castRadius > 0f) Gizmos.DrawWireSphere(end, castRadius);
    }
#endif
}
