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
    [Tooltip("No throttle or steering while airborne.")]
    [SerializeField] private float groundCheckDistance = 0.6f;
    [SerializeField] private LayerMask groundMask = ~0;

    [Header("Release")]
    [Tooltip("On losing its driver, brake to a stop before switching off rather than " +
             "coasting away on whatever momentum it had.")]
    [SerializeField] private bool brakeToStopOnRelease = true;

    /// <summary>The player driving, or null when running out the brake-to-stop.</summary>
    public GameObject Driver { get; private set; }

    /// <summary>Signed speed along the van's forward axis, m/s. Negative is reverse.</summary>
    public float ForwardSpeed { get; private set; }

    /// <summary>For a speedometer.</summary>
    public float SpeedKmh => Mathf.Abs(ForwardSpeed) * 3.6f;

    public bool IsGrounded { get; private set; }

    private Rigidbody body;
    private InputManager input;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();

        if (body.isKinematic)
            Debug.LogWarning($"{nameof(VanController)} on '{name}' has a kinematic Rigidbody — " +
                             $"velocity-driven movement won't do anything. Uncheck Is Kinematic.", this);

        // Off until a driver sits down. Van.Awake also enforces this.
        enabled = false;
    }

    /// <summary>Called by <see cref="Van"/> when the driver seat is occupied.</summary>
    public void TakeControl(GameObject driver)
    {
        Driver = driver;
        input = InputManager.Instance;
        enabled = true;
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
        bool handbrake = false;

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
        Vector3 velocity = body.linearVelocity;

        ForwardSpeed = Vector3.Dot(velocity, transform.forward);
        IsGrounded = Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down,
                                     groundCheckDistance + 0.2f, groundMask,
                                     QueryTriggerInteraction.Ignore);

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
        Gizmos.color = IsGrounded ? Color.green : Color.red;
        Vector3 start = transform.position + Vector3.up * 0.2f;
        Gizmos.DrawLine(start, start + Vector3.down * (groundCheckDistance + 0.2f));
    }
}
