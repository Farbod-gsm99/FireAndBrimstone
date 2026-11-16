using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

namespace FireAndBrimstone
{
    /// <summary>
    /// Core ML-Agents script for the redesigned 3-room "Fire and Brimstone" environment (20m x 32m Arena).
    /// - Discrete Actions (3 branches: Forward/Back, Strafe Left/Right, Rotate Left/Right)
    /// - 3D RayPerceptionSensors (Stacked = 4) + dynamic Beam BoxColliders so raycasts see gargoyle beams
    /// - 10 Normalized Vector Observations (Room One-Hot [3], Velocity [2], Exit Dir [2], Gargoyle Threat [3])
    /// - Potential-Based Progress Rewards + 0 step penalty in Rooms 1 & 2 (to encourage patient waiting)
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CubeAgent : Agent
    {
        [Header("Arena References")]
        public CurriculumManager curriculumManager;
        public HazardManager hazardManager;
        public Transform exitDoorTransform;
        public Transform spawnPointTransform;

        [Header("Movement Settings (Scaled for 20m x 32m Arena)")]
        public float moveSpeed = 8.0f;
        public float strafeSpeed = 6.5f;
        public float rotationSpeed = 165f;
        [Tooltip("Random X-axis offset at episode start so the agent learns general navigation rather than memorizing keys.")]
        public float spawnJitterX = 4.5f;

        [Header("Reward Shaping Settings")]
        [Tooltip("Total potential-based reward earned for advancing from start to the Exit Door.")]
        public float maxProgressReward = 0.6f;
        [Tooltip("Applied ONLY in Room 3 so the agent doesn't hide behind the first pillar forever. Rooms 1 & 2 have 0 step penalty.")]
        public float room3StepPenalty = -0.0003f;

        private Rigidbody _rb;
        private int _currentRoomIndex;
        private float _initialDistToExit;
        private float _bestDistToExit;

        public override void Initialize()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }

        public override void OnEpisodeBegin()
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;

            Vector3 baseSpawn = spawnPointTransform != null
                ? spawnPointTransform.localPosition
                : new Vector3(0f, 0.6f, -13.0f);

            float randomX = Random.Range(-spawnJitterX, spawnJitterX);
            transform.localPosition = new Vector3(baseSpawn.x + randomX, baseSpawn.y, baseSpawn.z);
            transform.localRotation = Quaternion.identity;

            _currentRoomIndex = curriculumManager != null
                ? curriculumManager.ResetArenaForNewEpisode(this)
                : 0;

            if (exitDoorTransform != null)
            {
                _initialDistToExit = Vector3.Distance(transform.localPosition, exitDoorTransform.localPosition);
                _bestDistToExit = _initialDistToExit;
            }
        }

        /// <summary>
        /// Collects 10 normalized floats:
        /// 1-3: One-hot encoding of the current room (Room 1, Room 2, Room 3) -> 3 floats
        /// 4-5: Normalized local velocity (X, Z) -> 2 floats
        /// 6-7: Local direction vector toward the Exit Door (X, Z) -> 2 floats
        /// 8-10: Nearest Gargoyle Threat telemetry (ThreatState [0=Idle/Safe, 0.5=Warning, 1=Shooting],
        ///       NormalizedPhaseTimer [0..1], NormalizedDistance [0..1]) -> 3 floats
        /// Total Vector Observation Space Size = 10.
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            // 1-3: Room One-Hot (3 floats)
            for (int i = 0; i < 3; i++)
            {
                sensor.AddObservation(_currentRoomIndex == i ? 1.0f : 0.0f);
            }

            // 4-5: Normalized Local Velocity (2 floats)
            Vector3 localVel = transform.InverseTransformDirection(_rb.linearVelocity);
            sensor.AddObservation(Mathf.Clamp(localVel.x / moveSpeed, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(localVel.z / moveSpeed, -1f, 1f));

            // 6-7: Direction to Exit Door in Agent's Local Space (2 floats)
            if (exitDoorTransform != null)
            {
                Vector3 dirToExit = (exitDoorTransform.localPosition - transform.localPosition).normalized;
                Vector3 localDirExit = transform.InverseTransformDirection(dirToExit);
                sensor.AddObservation(localDirExit.x);
                sensor.AddObservation(localDirExit.z);
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(1f);
            }

            // 8-10: Nearest Gargoyle Threat Info (3 floats)
            if (hazardManager != null && (_currentRoomIndex == 1 || _currentRoomIndex == 2))
            {
                hazardManager.GetNearestGargoyleTelemetry(
                    transform.position,
                    _currentRoomIndex,
                    out float threatLevel,
                    out float normalizedPhaseTime,
                    out float normalizedDistance
                );
                sensor.AddObservation(threatLevel);
                sensor.AddObservation(normalizedPhaseTime);
                sensor.AddObservation(normalizedDistance);
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(1f);
            }
        }

        /// <summary>
        /// Discrete Action Space (3 Branches):
        /// Branch 0 (Forward/Back): 0 = None, 1 = Forward (+Z), 2 = Backward (-Z)
        /// Branch 1 (Strafe):       0 = None, 1 = Left (-X),    2 = Right (+X)
        /// Branch 2 (Rotate):       0 = None, 1 = Turn Left,    2 = Turn Right
        /// </summary>
        public override void OnActionReceived(ActionBuffers actionBuffers)
        {
            int moveAction = actionBuffers.DiscreteActions[0];
            int strafeAction = actionBuffers.DiscreteActions[1];
            int rotateAction = actionBuffers.DiscreteActions[2];

            float forwardInput = 0f;
            if (moveAction == 1) forwardInput = 1f;
            else if (moveAction == 2) forwardInput = -0.75f;

            float strafeInput = 0f;
            if (strafeAction == 1) strafeInput = -1f;
            else if (strafeAction == 2) strafeInput = 1f;

            float rotateInput = 0f;
            if (rotateAction == 1) rotateInput = -1f;
            else if (rotateAction == 2) rotateInput = 1f;

            transform.Rotate(Vector3.up, rotateInput * rotationSpeed * Time.fixedDeltaTime);

            Vector3 desiredMove = (transform.forward * (forwardInput * moveSpeed)) +
                                  (transform.right * (strafeInput * strafeSpeed));
            _rb.linearVelocity = new Vector3(desiredMove.x, _rb.linearVelocity.y, desiredMove.z);

            EvaluateStepRewards();
        }

        private void EvaluateStepRewards()
        {
            // Lava pit fall check
            if (transform.localPosition.y < -0.5f)
            {
                AddReward(-1.0f);
                EndEpisode();
                return;
            }

            // Episode timeout check
            if (curriculumManager != null && curriculumManager.RemainingTime <= 0f)
            {
                AddReward(-0.5f);
                EndEpisode();
                return;
            }

            // Rooms 1 and 2 have 0 step penalty so the agent can wait patiently for fire waves/lanes to clear.
            // Room 3 applies a tiny step penalty so the agent advances from pillar to pillar.
            if (_currentRoomIndex == 2)
            {
                AddReward(room3StepPenalty);
            }

            // Potential-based progress reward toward the Exit Door (rewards only NEW closest distance)
            if (exitDoorTransform != null && _initialDistToExit > 0.1f)
            {
                float currentDist = Vector3.Distance(transform.localPosition, exitDoorTransform.localPosition);
                if (currentDist < _bestDistToExit)
                {
                    float delta = _bestDistToExit - currentDist;
                    float progressReward = (delta / _initialDistToExit) * maxProgressReward;
                    AddReward(progressReward);
                    _bestDistToExit = currentDist;
                }
            }
        }

        public void KillAgentWithFire()
        {
            AddReward(-1.0f);
            EndEpisode();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("FireHitbox"))
            {
                KillAgentWithFire();
            }
            else if (other.CompareTag("ExitDoor"))
            {
                AddReward(1.0f);
                EndEpisode();
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.collider.CompareTag("FireHitbox"))
            {
                KillAgentWithFire();
            }
            else if (collision.collider.CompareTag("ExitDoor"))
            {
                AddReward(1.0f);
                EndEpisode();
            }
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var discreteActions = actionsOut.DiscreteActions;

            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) discreteActions[0] = 1;
            else if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) discreteActions[0] = 2;
            else discreteActions[0] = 0;

            if (Input.GetKey(KeyCode.A)) discreteActions[1] = 1;
            else if (Input.GetKey(KeyCode.D)) discreteActions[1] = 2;
            else discreteActions[1] = 0;

            if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftArrow)) discreteActions[2] = 1;
            else if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.RightArrow)) discreteActions[2] = 2;
            else discreteActions[2] = 0;
        }
    }
}