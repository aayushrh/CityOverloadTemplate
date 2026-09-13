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

**The target object itself must be on the Interactable layer** — not just its parent. The cast
takes the nearest hit and checks *that collider's* layer against Interactable Mask; a door
collider left on Default is rejected before anything looks for an `IInteractable`, and it fails
completely silently. Turn on `PlayerInteractor`'s **Log Hits** when interaction does nothing: it
names the offending layer, and logs only when the outcome changes so it won't flood the console.

**Don't put the whole vehicle on Interactable** — the body collider has to stay a blocker, or
you can interact through it from the far side. Only the door children move layer.

**The player excludes itself by hierarchy, not by layer.** The ray starts at the eye, inside the
player's own capsule, so hits belonging to the player's transform hierarchy are skipped. An
earlier version stripped the player's layer from Raycast Mask instead, which meant a player left
on Default silently blinded the ray to every other Default object — gone now. Putting the Player
on the `Player` layer is still worth doing for `PlayerMovement`'s **Ground Mask**, which should
exclude it.

### HUD

```csharp
interactor.FocusChanged += target =>
    promptLabel.text = target != null ? target.Prompt : "";
```

Or poll `interactor.FocusPrompt`, which is empty when nothing is focused. `Interacted` fires
after a successful interaction, for a click sound or a camera nudge.

## Riding the van

Three scripts in `Assets/Scripts/Vehicle/`, split by responsibility:

| Script | Lives on | Job |
|---|---|---|
| `Van` | van root | Owns the seat list, decides who drives |
| `VanSeat` | one per door, each with its own box collider | Entry point: snap, suspend the player, swap camera |
| `VanController` | van root, beside the Rigidbody | Movement. Disabled until the driver seat fills |

`VanInteractable.cs` is superseded by these — **delete it**, it's dead code now.

### Hierarchy

```
Main Camera             Camera + CinemachineBrain     <- scene root, NOT under the player
Player                  CharacterController + PlayerMovement + PlayerInteractor
└── CameraPivot         pitch goes here
    └── PlayerCam       CinemachineCamera

Van                     Rigidbody + Van + VanController + body collider
├── Door_Driver         BoxCollider (Interactable layer) + VanSeat  [Role: Driver]
│   ├── Seat_Driver
│   └── Exit_Driver
├── Door_Passenger      BoxCollider (Interactable layer) + VanSeat  [Role: Passenger]
│   ├── Seat_Passenger
│   └── Exit_Passenger
├── Door_RearLeft       BoxCollider (Interactable layer) + VanSeat  [Role: Passenger]
│   └── ...
└── DriverCam           CinemachineCamera
```

One `VanSeat` per door, each on its own GameObject with its own box collider sized to the door.
That's what makes them separately aimable — `PlayerInteractor` resolves whichever collider the
ray hit up to its own seat, so three doors are three distinct prompts.

### Who gets control

`Role` on each seat is `Driver` or `Passenger`. Taking the Driver seat calls
`VanController.TakeControl`; vacating it calls `ReleaseControl`. Passengers never drive, no
matter how many are aboard — so a full van still has exactly one driver. `Van` warns at
`Awake` if no seat is set to Driver, or if more than one is (the first wins).

Passenger seats are otherwise identical: they snap, suspend the player, and swap camera. Give
each one its own **Seat Camera** for a distinct view, or leave it empty to use the Van's
**Fallback Camera**.

### Van inspector

| Field | Value |
|---|---|
| Controller | auto-found if empty |
| Seats | leave empty to collect every child `VanSeat`, or list them for explicit order |
| Fallback Camera | `DriverCam` — used by any seat without its own |

### VanController tuning

| | |
|---|---|
| Max Speed / Reverse | 14 / 5 m/s (~50 km/h forward) |
| Acceleration | 8 m/s per second |
| Brake / Engine braking | 18 / 3 |
| Max Turn Rate | 75 deg/s at full lock |
| Full Steer Speed | 5 m/s — below this, steering tapers off |
| Lateral Grip | 12 — lower slides, higher rails |
| Handbrake | Jump (Space) |

It drives the Rigidbody's velocity directly rather than using WheelColliders: stable, tunable,
and indifferent to suspension setup. Right for a city van, wrong for a racing game.

**Set the Rigidbody's Interpolation to `Interpolate`.** Physics runs at the fixed timestep and
the camera renders between steps, so without it the view judders while driving — and since the
player is parented into the van, the judder is very visible.

Also: give the van a mass in the 1200–2000 range, lower the Center of Mass if it feels tippy,
and leave Is Kinematic off (`VanController` warns if it's on, since velocity writes do nothing
on a kinematic body).

### Three ordering details that matter

**Steering scales with speed.** `speedFactor` ramps in up to Full Steer Speed, and the sign
flips below zero so reversing steers the way a car does. Without the ramp a stationary van
spins on the spot like a turret.

**Throttle against the direction of travel brakes rather than instantly reversing.** Pressing S
at 14 m/s forward decelerates; reverse engages only once roughly stopped.

**Exit releases van control before re-enabling the player.** `VanSeat.Exit` calls
`Van.SeatVacated` first, so there's no frame where both the van and the player are reading
`MoveInput` and the player walks while the van is still rolling.

### Cinemachine rearrangement this requires

`CinemachineBrain` **owns the transform of the Camera it sits on**, so the real Camera cannot be
a child of `CameraPivot` — the brain and the pivot would fight over it every frame. Hence the
`Main Camera` at scene root above.

On `PlayerCam`, set **Tracking Target** to `CameraPivot` with both Position and Rotation Control
following it: the pivot stays what mouselook rotates, and Cinemachine mirrors it.

Because there's no plain `Camera` under the player any more, **assign `PlayerMovement`'s Camera
Pivot by hand** — auto-find has nothing to find. `PlayerInteractor` falls back to
`PlayerMovement.CameraPivot`, so it's covered either way.

Blending between views is Cinemachine's job, not these scripts': set it on the brain's
**Default Blend**. The swap just toggles `enabled` on two cameras and lets the brain pick the
highest-priority active one — no priority bookkeeping to drift out of sync.

### Two player-side details

**The CharacterController is disabled before reparenting.** While enabled it overwrites
transform writes during its own update, so a player parented to a moving seat with it still on
gets dragged back out.

**Exit goes through `PlayerMovement.SnapTo`, never `transform.position`.** `PlayerMovement`
caches yaw internally; writing the transform directly leaves that stale and mouselook whips back
to the pre-van heading on the first frame. `SnapTo(position, yaw)` moves, resyncs yaw, and
clears momentum. Use it for teleports too.

### Looking around while seated

`PlayerMovement` is disabled while riding, so the player's own look is off. Put
`CinemachinePanTilt` + `CinemachineInputAxisController` on the seat camera and point the axis
controller at the `Look` action — Cinemachine drives the view directly and the seated player
keeps head movement without `PlayerMovement` running. For the driver, this is also how you get
a look-around-while-driving camera, since `Move` goes to the van and `Look` stays free.

### Seated input: exit and seat switching

While seated the interact ray starts at the player's eye — inside the van — so it would focus
the van's own geometry or nothing. `PlayerInteractor` is switched off on entry, and the occupied
`VanSeat` subscribes to the raw input instead:

| Input | Effect |
|---|---|
| `InteractPressed` | Exit to this seat's Exit Point |
| `SwitchSeatPressed` | Cycle to the next free seat |

Both carry a frame-number guard, so the keypress that got the player in can't immediately get
them out again, and one switch press can't cascade through every seat.

### Cycling seats

`Van.NextFreeSeatAfter(seat)` walks the **Seats list** in order and wraps: 0 → 1 → 2 → 0.
Occupied and locked seats are skipped rather than stopping the cycle, and the walk stops before
returning to the starting seat, so it always terminates and returns null when nothing else is
free. List order is the cycle order — reorder the list to change it.

`Van.TransferOccupant(from, to)` does the move. This is deliberately **not** exit-then-enter:
the player stays suspended and parented the whole way, never touching the ground, so you can
switch seats in a moving van. The player's component state travels between seats as a
`SeatedPlayer` struct rather than being torn down and rebuilt.

Switching out of the driver seat calls `ReleaseControl`, so the van brakes to a stop while
you're in the back. Switching into it calls `TakeControl` again.

Seats commonly share one camera (both of yours point at the same `CinemachineCamera`). The
transfer checks for that and skips the toggle when the two seats resolve to the same camera, so
the brain never sees a frame with nothing active.

### Binding the seat-switch button

`InputManager.SwitchSeatPressed` exists but **nothing raises it yet** — that's the part left
for you. Two ways in:

```csharp
InputManager.Instance.RaiseSwitchSeat();   // from a UI button, or your own binding
```

Or wire it to the Input System properly: add a `SwitchSeat` button action to the Player map,
let Unity regenerate the wrapper, then forward the new callback:

```csharp
public void OnSwitchSeat(InputAction.CallbackContext context)
{
    if (context.performed) SwitchSeatPressed?.Invoke();
}
```

### New on InputManager

- `JumpHeld` — held bool alongside the `JumpPressed`/`JumpReleased` events. `VanController`
  uses it as the handbrake.
- `SwitchSeatPressed` + `RaiseSwitchSeat()` — see above.

### Known limit: multiplayer

Every occupied seat subscribes to the same local input events, so in a networked session with
two players aboard, one player's Interact press would exit both. Gating this on PurrNet
ownership — the seat only binds input for the locally-owned player — is the fix when you get
there.

