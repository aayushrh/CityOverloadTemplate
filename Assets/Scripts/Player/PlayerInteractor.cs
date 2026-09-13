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

        // The camera sits inside the CharacterController capsule, so a cast that includes
        // the player's own layer hits the player first and blocks every interaction.
        raycastMask &= ~(1 << gameObject.layer);
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

        // One cast against interactables AND blockers. These overloads return the NEAREST
        // hit (the NonAlloc ones do not), which is what makes a wall in between block the
        // interaction instead of being cast straight through.
        RaycastHit info;
        bool hit = castRadius > 0f
            ? Physics.SphereCast(rayOrigin.position, castRadius, rayOrigin.forward,
                                 out info, range, raycastMask, TriggerMode)
            : Physics.Raycast(rayOrigin.position, rayOrigin.forward,
                              out info, range, raycastMask, TriggerMode);
        if (!hit) return null;

        Collider collider = info.collider;
        if (collider == null) return null;

        // The nearest surface isn't on the interactable layer: something is in the way.
        if ((interactableMask.value & (1 << collider.gameObject.layer)) == 0) return null;

        // Search upward so a child collider on a composite prop still resolves to the
        // component that owns the behaviour.
        var interactable = collider.GetComponentInParent<IInteractable>();

        // Require a Component so focus can be validated against Unity's lifetime checks.
        component = interactable as Component;
        return component != null ? interactable : null;
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
