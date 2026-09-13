using UnityEngine;

/// <summary>
/// Arcade vehicle movement. Disabled until <see cref="Van"/> hands it control, which happens
/// when the driver seat is taken; re-disabled when that seat is vacated.
///
/// Deliberately not a WheelCollider sim — it drives the Rigidbody's velocity directly, which
/// is stable, tunable, and doesn't care about suspension setup. Good for a city van; not for
/// a racing game.
///
/// Goes on the van root, alongside the Rigidbody.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class VanController : MonoBehaviour
{
    [Header("Drive")]
    [Tooltip("Top forward speed in m/s. 14 is about 50 km/h.")]
    [SerializeField] private float maxSpeed = 14f;
    [SerializeField] private float maxReverseSpeed = 5f;
    [Tooltip("m/s gained per second on the throttle.")]
    [SerializeField] private float acceleration = 8f;
    [Tooltip("m/s lost per second when braking or reversing into forward motion.")]
    [SerializeField] private float brakeDeceleration = 18f;
    [Tooltip("m/s lost per second when coasting with no throttle.")]
    [SerializeField] private float engineBraking = 3f;

    [Header("Steering")]
    [Tooltip("Degrees per second of yaw at full lock, once up to speed.")]
    [SerializeField] private float maxTurnRate = 75f;
    [Tooltip("Speed at which steering reaches full strength. Below this it tapers off, so " +
             "the van can't pivot on the spot.")]
    [SerializeField] private float fullSteerSpeed = 5f;
    [SerializeField] private bool steerWhileHandbraking = true;

    [Header("Grip")]
    [Tooltip("m/s of sideways velocity killed per second. Low values slide, high values rail.")]
    [SerializeField] private float lateralGrip = 12f;

    [Header("Ground")]
    [Tooltip("How far below the body collider to look for ground. No throttle or steering " +
             "while airborne.")]
    [SerializeField] private float groundCheckDistance = 0.4f;
    [SerializeField] private LayerMask groundMask = ~0;

    [Header("Contact")]
    [Tooltip("Give the van's colliders a frictionless material at Awake. This controller sets " +
             "velocity directly and models grip itself via Lateral Grip, so PhysX friction is " +
             "a second, competing brake. Leave on unless you know you want physical friction.")]
    [SerializeField] private bool frictionlessContact = true;

    [Header("Release")]
    [Tooltip("On losing its driver, brake to a stop before switching off rather than " +
             "coasting away on whatever momentum it had.")]
    [SerializeField] private bool brakeToStopOnRelease = true;

    [Header("Debug")]
    [Tooltip("Log driver / grounded / throttle whenever they change. Turn this on first if " +
             "the van won't move.")]
    [SerializeField] private bool logDrive = false;

    /// <summary>The player driving, or null when running out the brake-to-stop.</summary>
    public GameObject Driver { get; private set; }

    /// <summary>Signed speed along the van's forward axis, m/s. Negative is reverse.</summary>
    public float ForwardSpeed { get; private set; }

    /// <summary>For a speedometer.</summary>
    public float SpeedKmh => Mathf.Abs(ForwardSpeed) * 3.6f;

    public bool IsGrounded { get; private set; }

    private Rigidbody body;
    private Collider bodyCollider;
    private InputManager input;

    private readonly RaycastHit[] groundHits = new RaycastHit[8];
    private string lastLogged;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        bodyCollider = GetComponent<Collider>();

        if (body.isKinematic)
            Debug.LogWarning($"{nameof(VanController)} on '{name}' has a kinematic Rigidbody — " +
                             $"velocity-driven movement won't do anything. Uncheck Is Kinematic.", this);

        if (body.mass < 100f)
            Debug.LogWarning($"{nameof(VanController)} on '{name}': Rigidbody mass is {body.mass}kg. " +
                             $"A van wants something like 1500 — at this mass anything it touches " +
                             $"will throw it around.", this);

        if (frictionlessContact) ApplyFrictionlessContact();

        // Off until a driver sits down. Van.Awake also enforces this.
        enabled = false;
    }

    /// <summary>
    /// Strip friction from the van's solid colliders.
    ///
    /// This controller writes <c>linearVelocity</c> straight onto the Rigidbody and models
    /// grip itself (Lateral Grip, Engine Braking). Leaving PhysX friction on top means two
    /// systems braking the same body: Unity's default material is mu 0.6, which is 5.9 m/s^2
    /// of deceleration against 8 m/s^2 of acceleration. What is left barely creeps, and a
    /// light body resting on small contact patches just rocks in place instead.
    /// </summary>
    private void ApplyFrictionlessContact()
    {
        var slick = new PhysicsMaterial($"{name}_Contact")
        {
            dynamicFriction = 0f,
            staticFriction = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounciness = 0f,
            bounceCombine = PhysicsMaterialCombine.Minimum,
        };

        foreach (Collider c in GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger) continue;      // interaction volumes have no contacts to fix
            c.sharedMaterial = slick;
        }
    }

    /// <summary>Called by <see cref="Van"/> when the driver seat is occupied.</summary>
    public void TakeControl(GameObject driver)
    {
        Driver = driver;
        input = InputManager.Instance;
        enabled = true;

        // A van parked since scene load is asleep, and PhysX ignores velocity written to a
        // sleeping body. Without this the first press of W does nothing at all — forever.
        if (body != null) body.WakeUp();
    }

    /// <summary>
    /// Called by <see cref="Van"/> when the driver seat is vacated. With
    /// <see cref="brakeToStopOnRelease"/> the component stays enabled until the van has
    /// stopped, then switches itself off.
    /// </summary>
    public void ReleaseControl()
    {
        Driver = null;
        if (!brakeToStopOnRelease) enabled = false;
    }

    private void FixedUpdate()
    {
        if (input == null) input = InputManager.Instance;

        float throttle = 0f;
        float steer = 0f;
        bool handbrake;

        if (Driver != null && input != null)
        {
            Vector2 move = input.MoveInput;
            throttle = Mathf.Clamp(move.y, -1f, 1f);
            steer = Mathf.Clamp(move.x, -1f, 1f);
            handbrake = input.JumpHeld;
        }
        else
        {
            // Unmanned and braking to a stop.
            handbrake = true;
        }

        float dt = Time.fixedDeltaTime;

        // Any driver input has to wake the body before the velocity write, not after: a write
        // to a sleeping Rigidbody is silently discarded.
        if (body.IsSleeping() && (Mathf.Abs(throttle) > 0.01f || Mathf.Abs(steer) > 0.01f))
            body.WakeUp();

        Vector3 velocity = body.linearVelocity;

        ForwardSpeed = Vector3.Dot(velocity, transform.forward);
        IsGrounded = CheckGrounded();

        if (logDrive)
        {
            string state = $"driver={(Driver != null ? Driver.name : "none")} grounded={IsGrounded} " +
                           $"throttle={throttle:0.0} fwd={ForwardSpeed:0.00}m/s " +
                           $"vel={velocity.magnitude:0.00} sleeping={body.IsSleeping()}";
            if (state != lastLogged)
            {
                lastLogged = state;
                Debug.Log($"[{name}] {state}", this);
            }
        }

        if (IsGrounded)
        {
            ApplyDrive(throttle, handbrake, velocity, dt);
            ApplySteering(steer, handbrake, dt);
        }

        // Switch off once the unmanned van has actually stopped, so FixedUpdate isn't
        // running forever on a parked vehicle.
        if (Driver == null && Mathf.Abs(ForwardSpeed) < 0.2f)
        {
            enabled = false;
        }
    }

    /// <summary>
    /// Looks for ground just under the body collider. Derived from the collider's bounds
    /// rather than the transform origin, because a vehicle model's pivot is as often at the
    /// centre of the body as at the wheels — a fixed offset silently fails on half of them.
    /// </summary>
    private bool CheckGrounded()
    {
        Vector3 origin;
        float distance;

        if (bodyCollider != null)
        {
            Bounds bounds = bodyCollider.bounds;
            // Start just inside the bottom face and look a little further down.
            origin = new Vector3(bounds.center.x, bounds.min.y + 0.1f, bounds.center.z);
            distance = 0.1f + groundCheckDistance;
        }
        else
        {
            origin = transform.position + Vector3.up * 0.2f;
            distance = 0.2f + groundCheckDistance;
        }

        int count = Physics.RaycastNonAlloc(origin, Vector3.down, groundHits, distance,
                                            groundMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            Collider c = groundHits[i].collider;
            if (c == null) continue;

            // Skip the van's own colliders and anything riding in it — the seated player is
            // parented into this hierarchy, and its layer is in the mask.
            if (c.transform.IsChildOf(transform)) continue;

            return true;
        }
        return false;
    }

    private void ApplyDrive(float throttle, bool handbrake, Vector3 velocity, float dt)
    {
        float target = throttle >= 0f ? throttle * maxSpeed : throttle * maxReverseSpeed;

        float rate;
        if (handbrake)
        {
            target = 0f;
            rate = brakeDeceleration;
        }
        else if (Mathf.Abs(throttle) < 0.01f)
        {
            target = 0f;
            rate = engineBraking;
        }
        else if (Mathf.Abs(ForwardSpeed) > 0.5f && Mathf.Sign(target) != Mathf.Sign(ForwardSpeed))
        {
            // Throttle against the direction of travel is the brake pedal, not instant reverse.
            target = 0f;
            rate = brakeDeceleration;
        }
        else
        {
            rate = acceleration;
        }

        float newForward = Mathf.MoveTowards(ForwardSpeed, target, rate * dt);

        // Bleed off sideways velocity — this is what stops the van skating through corners.
        float lateral = Vector3.Dot(velocity, transform.right);
        float newLateral = Mathf.MoveTowards(lateral, 0f, lateralGrip * dt);

        // Keep the world-vertical component untouched so gravity still does its job.
        float vertical = velocity.y;

        Vector3 planar = transform.forward * newForward + transform.right * newLateral;
        body.linearVelocity = new Vector3(planar.x, vertical, planar.z);
    }

    private void ApplySteering(float steer, bool handbrake, float dt)
    {
        if (handbrake && !steerWhileHandbraking) return;
        if (Mathf.Abs(steer) < 0.01f) return;

        // Steering authority scales with speed: a stationary van shouldn't spin in place,
        // and the sign flips in reverse so backing up steers the way a car does.
        float speedFactor = Mathf.Clamp01(Mathf.Abs(ForwardSpeed) / Mathf.Max(fullSteerSpeed, 0.01f));
        float direction = ForwardSpeed < 0f ? -1f : 1f;

        float yawDelta = steer * maxTurnRate * speedFactor * direction * dt;
        body.MoveRotation(body.rotation * Quaternion.Euler(0f, yawDelta, 0f));
    }

    private void OnDrawGizmosSelected()
    {
        Collider col = bodyCollider != null ? bodyCollider : GetComponent<Collider>();

        Vector3 origin = col != null
            ? new Vector3(col.bounds.center.x, col.bounds.min.y + 0.1f, col.bounds.center.z)
            : transform.position + Vector3.up * 0.2f;

        Gizmos.color = IsGrounded ? Color.green : Color.red;
        Gizmos.DrawLine(origin, origin + Vector3.down * (0.1f + groundCheckDistance));
    }
}
