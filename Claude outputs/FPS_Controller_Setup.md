# First Person Controller — setup

## What changed

**`Assets/Settings/InputSystem_Actions.inputactions`** — the Player map is trimmed to the six
actions an FPS controller needs: `Move`, `Look`, `Jump`, `Sprint`, `Crouch`, `Interact`.
Removed: `Attack`, `Previous`, `Next`. The whole `UI` map is untouched, and every surviving
action keeps its original GUID so anything already referencing them (PurrUI, a `PlayerInput`
component) still resolves.

`Interact` also had a **Hold** interaction on it in the default template, which meant
`performed` only fired after ~0.4s. That's cleared, so it now fires on press.

**`Assets/Settings/InputSystem_Actions.inputactions.meta`** — `generateWrapperCode` was `0`.
That's the reason `InputSystem_Actions` didn't exist as a class and your `InputManager`
wouldn't compile. It's now `1`, so Unity generates `InputSystem_Actions.cs` next to the asset
on import. The GUID is unchanged, so no references break.

**`Assets/Scripts/Player/InputManager.cs`** — same shape as your original, renamed to the new
actions. `Move`/`Look` stay as polled properties; `Jump`/`Interact` are events;
`Sprint`/`Crouch` are both (held bool *and* events, since a movement controller wants the bool).

**`Assets/Scripts/Player/PlayerMovement.cs`** — the controller.

## Scene setup

1. Empty GameObject `InputManager` → add the `InputManager` component. One per scene
   (it self-destructs duplicates).
2. Build the player:

```
Player              CharacterController + PlayerMovement
└── CameraPivot     empty, local position (0, 1.62, 0)
    └── Main Camera local position (0,0,0), local rotation (0,0,0)
```

3. On the `CharacterController`: Height `1.8`, Radius `0.3`, Center `(0, 0.9, 0)`,
   Skin Width `0.02`, Step Offset `0.3`, Slope Limit `45`.
   `PlayerMovement` rewrites height/center at `Awake` from its own `Standing Height`, so those
   two just need to agree.
4. On `PlayerMovement`: leave **Camera Pivot** empty and it finds the child camera itself, or
   assign `CameraPivot`. Set **Ground Mask** to exclude the player's own layer — with the
   default `Everything` the probe can hit the player's own collider on some setups.

## Defaults

| | |
|---|---|
| Walk / Sprint / Crouch | 4.5 / 7.5 / 2.0 m/s |
| Jump height | 1.1 m, gravity −22 |
| Coyote time / jump buffer | 0.12 s each |
| Mouse sensitivity | 0.12 (degrees per pixel of mouse delta) |
| Crouch | hold by default; uncheck **Hold To Crouch** for toggle |

Mouse and gamepad look are scaled separately — mouse delta is already per-frame, stick
deflection is per-second — so the two sensitivities are in different units and tune
independently. `InputManager.LookIsPointerDelta` is what picks between them.

## Notes

- Movement is in `Update` with `CharacterController.Move`, which is the normal place for a
  `CharacterController`. Don't also drive it from `FixedUpdate`.
- Other systems can read `HorizontalSpeed`, `IsCrouching`, `IsSprinting`, `IsOnGround`,
  `Velocity` off `PlayerMovement` for footsteps/animation/HUD.
- Opening a menu: `InputManager.Instance.SetCursorLocked(false)` and
  `SetPlayerInputEnabled(false)` — the second one also zeroes the axes so the player doesn't
  keep walking into a wall while the menu is up.

## Interaction

Two new layers in `ProjectSettings/TagManager.asset`: **`Interactable`** (user layer 8) and
**`Player`** (user layer 9). Layers 0–7 are Unity's reserved slots, which is why 8 is the
first one available.

**`Assets/Scripts/Interaction/IInteractable.cs`** — the contract:

```csharp
string Prompt { get; }          // HUD text, re-read every frame while focused
bool CanInteract { get; }       // false = focusable but refuses, e.g. "Locked"
void OnFocusEnter();            // highlight on
void OnFocusExit();             // highlight off
void Interact(GameObject interactor);
```

`Interact` takes the interactor's GameObject rather than the concrete component, so NPCs or a
remote PurrNet player can drive the same interactables later.

**`Assets/Scripts/Interaction/InteractableBase.cs`** — implements all of it with a serialized
prompt, `UnityEvent`s, and a single-use flag. Drop it on a prop and wire the event in the
inspector; override `OnInteract(GameObject)` in a subclass when it needs real code. Implement
`IInteractable` directly only when the object already has a base class.

**`Assets/Scripts/Player/PlayerInteractor.cs`** — goes on the Player root. Casts from the
camera each frame, tracks focus, and forwards the Interact button.

### Setting an interactable up

1. Put the object on the **Interactable** layer, give it a collider.
2. Add `InteractableBase` (or your subclass). Set **Prompt**, wire **On Interacted**.
3. Make sure `PlayerInteractor`'s **Raycast Mask** includes the Interactable layer *and* the
   layers that should block line of sight. **Interactable Mask** is just Interactable.

### Two things that are easy to get wrong

**Raycast Mask needs the blockers, not only interactables.** It's one cast, and the nearest hit
wins — a wall between you and a switch only blocks the interaction if the wall's layer is in
the mask. A mask of Interactable alone lets you press buttons through walls.

**Put the Player on the `Player` layer.** The camera sits inside the CharacterController
capsule, so a cast that can hit the player's own collider hits it at distance ~0 and nothing
is ever interactable. `PlayerInteractor` strips its own GameObject's layer from the mask at
`Awake` to cover this, but it only works if the player isn't sharing a layer with your
geometry. This is the same layer to exclude from `PlayerMovement`'s **Ground Mask**.

### HUD

```csharp
interactor.FocusChanged += target =>
    promptLabel.text = target != null ? target.Prompt : "";
```

Or poll `interactor.FocusPrompt`, which is empty when nothing is focused. `Interacted` fires
after a successful interaction, for a click sound or a camera nudge.

## Riding the van

**`Assets/Scripts/Interaction/VanInteractable.cs`** — derives from `InteractableBase`. Interact
to snap into a seat, interact again to get out, and the view blends to the van's Cinemachine
camera in between. It's a ride-along seat; it does not drive. `Occupant`, `Entered` and `Exited`
are the hooks a driving script would use to start consuming `InputManager.Instance.MoveInput`.

### Cinemachine rearrangement this requires

This is the part that isn't obvious. `CinemachineBrain` **owns the transform of the Camera it
sits on**, so the real Camera can no longer be a child of `CameraPivot` — the brain and the
pivot would fight over it every frame. Restructure to:

```
Main Camera            Camera + CinemachineBrain     ← scene root, NOT under the player
Player                 CharacterController + PlayerMovement + PlayerInteractor
└── CameraPivot        pitch goes here
    └── PlayerCam      CinemachineCamera
Van                    VanInteractable + collider, on the Interactable layer
├── SeatAnchor         empty, where the player sits
├── ExitPoint          empty, where the player is put down
└── VanCam             CinemachineCamera
```

On `PlayerCam`, set **Tracking Target** to `CameraPivot` with both Position and Rotation
Control set to follow it — the pivot stays the thing mouselook rotates, and Cinemachine just
mirrors it.

Because there's no longer a plain `Camera` under the player, auto-find can't work:
`PlayerMovement` needs **Camera Pivot** assigned explicitly. `PlayerInteractor` falls back to
`PlayerMovement.CameraPivot`, so it's fine either way.

### Van inspector

| Field | Value |
|---|---|
| Seat Anchor | `SeatAnchor` (required) |
| Exit Point | `ExitPoint` — where the player lands, facing its forward |
| Van Camera | `VanCam` |
| Player Camera | leave empty; found from the player on first entry |
| Seat Blend Duration | `0.25` — slides into the seat; `0` snaps |

Blend *between* cameras is Cinemachine's, not this script's: set it on the brain's **Default
Blend**. The swap just toggles `enabled` on the two cameras, so the brain picks the highest
priority active one — no priority bookkeeping to get out of sync.

### Two ordering details that matter

**The CharacterController is disabled before reparenting.** An enabled `CharacterController`
overwrites transform writes during its own update, so a player parented to a moving seat with
it still on gets dragged back out.

**Exit goes through `PlayerMovement.SnapTo`, not `transform.position`.** `PlayerMovement` caches
yaw internally; writing the transform directly leaves that cache stale and mouselook whips back
to the pre-van heading on the first frame. `SnapTo(position, yaw)` moves the player, resyncs the
yaw and clears momentum. Use it for teleports too.

### Looking around while seated

`PlayerMovement` is disabled while riding, so the player's own look is off. To look around from
the seat, put `CinemachinePanTilt` + `CinemachineInputAxisController` on `VanCam` and point the
axis controller at the `Look` action — Cinemachine then drives the view directly and the seated
player keeps head movement without `PlayerMovement` running.

### Exit input is bound directly, not through the interactor

While seated, the interact ray starts at the player's eye — which is inside the van — so it
would focus the van's own geometry or nothing at all. `PlayerInteractor` is switched off on
entry and `VanInteractable` subscribes to `InputManager.InteractPressed` itself, with a
frame-number guard so the keypress that got the player in can't also get it out.
