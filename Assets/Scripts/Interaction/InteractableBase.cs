using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Drop-in <see cref="IInteractable"/> for the common case: a prompt, a UnityEvent,
/// and optional single use. Wire the event in the inspector for simple things; override
/// <see cref="OnInteract"/> in a subclass when the behaviour needs real code.
/// </summary>
public class InteractableBase : MonoBehaviour, IInteractable
{
    [Header("Interaction")]
    [Tooltip("Shown on the HUD while focused. Keep it to a verb phrase.")]
    [SerializeField] private string prompt = "Interact";

    [Tooltip("Uncheck to keep the object focusable but refuse the interaction.")]
    [SerializeField] private bool interactable = true;

    [Tooltip("Disable this component after the first successful interaction.")]
    [SerializeField] private bool singleUse = false;

    [Header("Events")]
    [SerializeField] private UnityEvent onInteracted;
    [SerializeField] private UnityEvent onFocusEntered;
    [SerializeField] private UnityEvent onFocusExited;

    public virtual string Prompt => prompt;

    public virtual bool CanInteract => interactable && enabled;

    /// <summary>True while the player is aiming at this object.</summary>
    public bool IsFocused { get; private set; }

    /// <summary>Change the HUD text at runtime ("Open" -> "Close").</summary>
    public void SetPrompt(string value) => prompt = value;

    /// <summary>Lock or unlock without disabling the component, so focus still works.</summary>
    public void SetInteractable(bool value) => interactable = value;

    public virtual void OnFocusEnter()
    {
        if (IsFocused) return;
        IsFocused = true;
        onFocusEntered?.Invoke();
    }

    public virtual void OnFocusExit()
    {
        if (!IsFocused) return;
        IsFocused = false;
        onFocusExited?.Invoke();
    }

    public void Interact(GameObject interactor)
    {
        // Guard here as well as in the interactor: Interact may be called by other
        // systems (a networked RPC, a cutscene) that never ran the focus check.
        if (!CanInteract) return;

        OnInteract(interactor);
        onInteracted?.Invoke();

        if (singleUse)
        {
            OnFocusExit();
            enabled = false;
        }
    }

    /// <summary>Override for interaction logic in code. The UnityEvent fires after this.</summary>
    protected virtual void OnInteract(GameObject interactor) { }

    // Virtual so subclasses can extend it. Unity dispatches the message to the most
    // derived declaration only, so a subclass that re-declares it privately would
    // silently stop the focus cleanup below from running.
    protected virtual void OnDisable()
    {
        // Don't leave a highlight on when the object is switched off mid-focus.
        OnFocusExit();
    }
}
