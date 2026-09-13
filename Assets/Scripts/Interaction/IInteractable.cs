using UnityEngine;

/// <summary>
/// Anything the player can interact with. Implement this on a component whose
/// GameObject (or a parent of its collider) sits on the <c>Interactable</c> layer —
/// <see cref="PlayerInteractor"/> walks up the hierarchy from the collider it hit,
/// so the collider itself doesn't have to carry the implementation.
///
/// Most things should just derive from <see cref="InteractableBase"/>; implement
/// this directly when the object already has a base class of its own.
/// </summary>
public interface IInteractable
{
    /// <summary>
    /// Short verb phrase for the HUD — "Open", "Pick up", "Locked". Can change at
    /// runtime; the interactor re-reads it every frame while focused.
    /// </summary>
    string Prompt { get; }

    /// <summary>
    /// False rejects <see cref="Interact"/> while still allowing focus, so a locked
    /// door can sit there showing "Locked" instead of going silent.
    /// </summary>
    bool CanInteract { get; }

    /// <summary>The player's aim moved onto this object. Highlight here.</summary>
    void OnFocusEnter();

    /// <summary>The player's aim moved off this object, or it went out of range.</summary>
    void OnFocusExit();

    /// <summary>
    /// Interact key pressed while focused and <see cref="CanInteract"/> was true.
    /// </summary>
    /// <param name="interactor">The GameObject that initiated it — the player root.</param>
    void Interact(GameObject interactor);
}
