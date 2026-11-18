using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using System.Collections;

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


        [Header("Death / Respawn Cinematic")]

        [Tooltip("Enable for YouTube recording. Disable during RL training.")]
        [SerializeField]
        private bool enableDeathRespawnEffects = false;

        [SerializeField]
        private Transform visualModelRoot;

        [Header("Animation Timing")]

        [SerializeField]
        private float burnDuration = 1.2f;

        [SerializeField]
        private float burntHoldDuration = 0.35f;

        [SerializeField]
        private float spawnDuration = 0.9f;

        [Header("Audio")]

        [SerializeField]
        private AudioSource burnAudioSource;

        [SerializeField]
        private AudioSource respawnAudioSource;

        [SerializeField]
        private AudioClip respawnClip;


        // Cached visual renderers and colliders.
        private Renderer[] _visualRenderers;
        private Collider[] _agentColliders;
        private bool[] _originalColliderStates;

        private bool _originalIsKinematic;

        private bool _isDying;
        private bool _isSpawning;

        private Coroutine _deathRoutine;
        private Coroutine _spawnRoutine;

        private MaterialPropertyBlock _effectBlock;

        private static readonly int BurnProgressId =
            Shader.PropertyToID("_BurnProgress");

        private static readonly int SpawnProgressId =
            Shader.PropertyToID("_SpawnProgress");

        private static readonly int EffectBaseYId =
            Shader.PropertyToID("_EffectBaseY");

        private static readonly int EffectHeightId =
            Shader.PropertyToID("_EffectHeight");


        [Header("Spawn Position")]
        [Tooltip("Small gap between the bottom of the agent collider and the floor.")]
        [SerializeField, Min(0f)]
        private float spawnClearance = 0.02f;

        [Tooltip("Height above the Spawn Point from which the ground check starts.")]
        [SerializeField, Min(0.5f)]
        private float spawnGroundRayStartHeight = 2.0f;

        [Tooltip("Maximum downward distance searched for the floor.")]
        [SerializeField, Min(1f)]
        private float spawnGroundRayDistance = 10.0f;

        [Tooltip("Layers that can be treated as the spawn floor.")]
        [SerializeField]
        private LayerMask spawnGroundMask = ~0;

        private float _rootToColliderBottomOffset;
        private float _initialRootWorldY;
        private readonly RaycastHit[] _spawnGroundHits = new RaycastHit[32];

        private Rigidbody _rb;
        private int _currentRoomIndex;
        private float _initialDistToExit;
        private float _bestDistToExit;

        public override void Initialize()
        {
            _rb = GetComponent<Rigidbody>();

            _rb.constraints =
                RigidbodyConstraints.FreezeRotationX |
                RigidbodyConstraints.FreezeRotationZ;

            _originalIsKinematic = _rb.isKinematic;

            // Find the visual model if it wasn't assigned in the Inspector.
            if (visualModelRoot == null)
                visualModelRoot = transform.Find("VisualModel");

            if (visualModelRoot != null)
            {
                _visualRenderers =
                    visualModelRoot.GetComponentsInChildren<Renderer>(true);
            }
            else
            {
                _visualRenderers = new Renderer[0];

                Debug.LogError(
                    "CubeAgent: VisualModel reference is missing!"
                );
            }

            // Cache the agent's colliders and their initial enabled states.
            _agentColliders = GetComponentsInChildren<Collider>(true);

            _originalColliderStates = new bool[_agentColliders.Length];

            for (int i = 0; i < _agentColliders.Length; i++)
            {
                _originalColliderStates[i] =
                    _agentColliders[i].enabled;
            }

            // Save the authored root height as a safe fallback and measure how far
            // the physical collider extends below the root pivot.
            _initialRootWorldY = transform.position.y;
            CacheRootToColliderBottomOffset();

            // Verify that the effect shader is assigned.
            foreach (Renderer r in _visualRenderers)
            {
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null ||
                        !mat.HasProperty(BurnProgressId) ||
                        !mat.HasProperty(SpawnProgressId))
                    {
                        Debug.LogWarning(
                            $"Effect Shader Graph material is missing on {r.name}"
                        );
                    }
                }
            }
        }

        private void CacheRootToColliderBottomOffset()
        {
            bool foundPhysicalCollider = false;
            Bounds combinedBounds = default;

            foreach (Collider col in _agentColliders)
            {
                if (col == null || !col.enabled || col.isTrigger)
                    continue;

                if (!foundPhysicalCollider)
                {
                    combinedBounds = col.bounds;
                    foundPhysicalCollider = true;
                }
                else
                {
                    combinedBounds.Encapsulate(col.bounds);
                }
            }

            if (foundPhysicalCollider)
            {
                _rootToColliderBottomOffset =
                    transform.position.y - combinedBounds.min.y;
            }
            else
            {
                _rootToColliderBottomOffset = 0f;

                Debug.LogWarning(
                    "CubeAgent: No enabled non-trigger collider was found. " +
                    "Spawn height will be based on the root pivot.",
                    this
                );
            }
        }

        private bool TryGetSpawnGroundHeight(
            Vector3 spawnPosition,
            out float groundY)
        {
            // Start above both the Spawn Point and the root's authored height.
            float referenceY = Mathf.Max(
                spawnPosition.y,
                _initialRootWorldY
            );

            Vector3 rayOrigin = new Vector3(
                spawnPosition.x,
                referenceY + spawnGroundRayStartHeight,
                spawnPosition.z
            );

            int hitCount = Physics.RaycastNonAlloc(
                rayOrigin,
                Vector3.down,
                _spawnGroundHits,
                spawnGroundRayDistance,
                spawnGroundMask,
                QueryTriggerInteraction.Ignore
            );

            float closestDistance = float.PositiveInfinity;
            bool foundGround = false;
            groundY = 0f;

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = _spawnGroundHits[i];

                if (hit.collider == null)
                    continue;

                Transform hitTransform = hit.collider.transform;

                // Never use one of the agent's own colliders as the floor.
                if (hitTransform == transform || hitTransform.IsChildOf(transform))
                    continue;

                // Ignore walls and undersides; we need an upward-facing floor.
                if (hit.normal.y < 0.5f)
                    continue;

                if (hit.distance < closestDistance)
                {
                    closestDistance = hit.distance;
                    groundY = hit.point.y;
                    foundGround = true;
                }
            }

            return foundGround;
        }

        public override void OnEpisodeBegin()
        {
            // Cancel any old sequence if the environment is externally reset.
            if (_deathRoutine != null)
            {
                StopCoroutine(_deathRoutine);
                _deathRoutine = null;
            }

            if (_spawnRoutine != null)
            {
                StopCoroutine(_spawnRoutine);
                _spawnRoutine = null;
            }

            _isDying = false;

            _isSpawning =
                enableDeathRespawnEffects &&
                _visualRenderers.Length > 0;

            // Freeze physics while repositioning.
            SetCinematicPhysics(true);
            SetAgentCollidersEnabled(false);

            // Use the Spawn Point for horizontal placement only. Its Y coordinate
            // may represent either the floor or the agent pivot in the authored
            // scene, so derive the root height from the actual floor collider.
            float randomX = Random.Range(
                -spawnJitterX,
                spawnJitterX
            );

            Vector3 spawnPosition;

            if (spawnPointTransform != null)
            {
                spawnPosition = spawnPointTransform.position;
                spawnPosition += spawnPointTransform.right * randomX;
            }
            else
            {
                // Fallback horizontal location. The floor query below determines Y.
                Vector3 fallbackLocal = new Vector3(
                    randomX,
                    0f,
                    -13.0f
                );

                spawnPosition = transform.parent != null
                    ? transform.parent.TransformPoint(fallbackLocal)
                    : fallbackLocal;
            }

            if (TryGetSpawnGroundHeight(spawnPosition, out float groundY))
            {
                spawnPosition.y =
                    groundY + _rootToColliderBottomOffset + spawnClearance;
            }
            else
            {
                // Keep the prefab-authored height if the floor cannot be detected.
                spawnPosition.y = _initialRootWorldY;

                Debug.LogWarning(
                    $"CubeAgent: No walkable floor found beneath spawn point " +
                    $"at XZ ({spawnPosition.x:F2}, {spawnPosition.z:F2}). " +
                    $"Using the authored root Y ({_initialRootWorldY:F2}). " +
                    "Check Spawn Ground Mask and Spawn Point placement.",
                    this
                );
            }

            // Position and rotate the agent.
            Quaternion spawnRotation = spawnPointTransform != null
                ? spawnPointTransform.rotation
                : (transform.parent != null
                    ? transform.parent.rotation
                    : Quaternion.identity);

            transform.SetPositionAndRotation(
                spawnPosition,
                spawnRotation
            );

            Physics.SyncTransforms();

            // Reset the arena.
            _currentRoomIndex = curriculumManager != null
                ? curriculumManager.ResetArenaForNewEpisode(this)
                : 0;

            if (exitDoorTransform != null)
            {
                _initialDistToExit = Vector3.Distance(
                    transform.localPosition,
                    exitDoorTransform.localPosition
                );

                _bestDistToExit = _initialDistToExit;
            }

            if (_isSpawning)
            {
                // Initially reveal only the very bottom of the model.
                ApplyAgentVisualEffect(0f, 0f);

                _spawnRoutine = StartCoroutine(SpawnSequence());
            }
            else
            {
                // Normal immediate reset during training.
                ApplyAgentVisualEffect(0f, 1f);

                SetAgentCollidersEnabled(true);
                SetCinematicPhysics(false);
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
            if (_isDying || _isSpawning)
            {
                return;
            }
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
            // Lava pit fall check.
            if (transform.localPosition.y < -0.5f)
            {
                BeginFailureSequence(-1.0f);
                return;
            }

            // Episode timeout.
            if (curriculumManager != null &&
                curriculumManager.RemainingTime <= 0f)
            {
                BeginFailureSequence(-0.5f);
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
            BeginFailureSequence(-1.0f);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_isDying || _isSpawning)
                return;
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
            if (_isDying || _isSpawning)
                return;
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

        private void BeginFailureSequence(float rewardPenalty)
        {
            // Prevent multiple collisions from starting multiple death animations.
            if (_isDying || _isSpawning)
                return;

            AddReward(rewardPenalty);

            // Preserve normal RL training behavior.
            if (!enableDeathRespawnEffects || _visualRenderers.Length == 0)
            {
                EndEpisode();
                return;
            }

            _isDying = true;

            SetCinematicPhysics(true);
            SetAgentCollidersEnabled(false);

            // Start the burning sound.
            if (burnAudioSource != null)
            {
                burnAudioSource.Stop();
                burnAudioSource.Play();
            }

            _deathRoutine = StartCoroutine(DeathSequence());
        }

        private IEnumerator DeathSequence()
        {
            float elapsed = 0f;

            while (elapsed < burnDuration)
            {
                elapsed += Time.deltaTime;

                float progress = Mathf.Clamp01(
                    elapsed / burnDuration
                );

                // Gradually turn grey from bottom to top.
                ApplyAgentVisualEffect(progress, 1f);

                yield return null;
            }

            // Make sure the entire character is burnt.
            ApplyAgentVisualEffect(1f, 1f);

            if (burnAudioSource != null)
                burnAudioSource.Stop();

            // Leave the burnt character visible briefly.
            yield return new WaitForSeconds(burntHoldDuration);

            _deathRoutine = null;

            // Only now end the ML-Agents episode.
            EndEpisode();
        }

        private IEnumerator SpawnSequence()
        {
            // Play the respawn sound.
            if (respawnAudioSource != null && respawnClip != null)
            {
                respawnAudioSource.PlayOneShot(respawnClip);
            }

            float elapsed = 0f;

            while (elapsed < spawnDuration)
            {
                elapsed += Time.deltaTime;

                float progress = Mathf.Clamp01(
                    elapsed / spawnDuration
                );

                // Reveal the agent from bottom to top.
                ApplyAgentVisualEffect(0f, progress);

                yield return null;
            }

            // Reveal the complete character.
            ApplyAgentVisualEffect(0f, 1f);

            _spawnRoutine = null;

            // Allow movement and collisions again.
            SetAgentCollidersEnabled(true);
            SetCinematicPhysics(false);

            _isSpawning = false;
        }

        private void ApplyAgentVisualEffect(
    float burnProgress,
    float spawnProgress)
        {
            if (_visualRenderers == null ||
                _visualRenderers.Length == 0)
                return;

            // Calculate the total world-space height of the character.
            Bounds combinedBounds = _visualRenderers[0].bounds;

            foreach (Renderer r in _visualRenderers)
            {
                if (r != null)
                    combinedBounds.Encapsulate(r.bounds);
            }

            float baseY = combinedBounds.min.y;
            float height = Mathf.Max(combinedBounds.size.y, 0.001f);

            foreach (Renderer r in _visualRenderers)
            {
                if (r == null)
                    continue;

                // Preserve any other existing property overrides.
                r.GetPropertyBlock(_effectBlock);

                _effectBlock.SetFloat(
                    BurnProgressId,
                    burnProgress
                );

                _effectBlock.SetFloat(
                    SpawnProgressId,
                    spawnProgress
                );

                _effectBlock.SetFloat(
                    EffectBaseYId,
                    baseY
                );

                _effectBlock.SetFloat(
                    EffectHeightId,
                    height
                );

                r.SetPropertyBlock(_effectBlock);
            }
        }

        private void SetCinematicPhysics(bool freeze)
        {
            if (_rb == null)
                return;

            if (freeze)
            {
                // Only reset velocity when transitioning from
                // a dynamic Rigidbody to a kinematic one.
                if (!_rb.isKinematic)
                {
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;

                    _rb.isKinematic = true;
                }
            }
            else
            {
                _rb.isKinematic = _originalIsKinematic;

                // Reset velocity only after physics is restored.
                if (!_rb.isKinematic)
                {
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                }
            }
        }


        private void SetAgentCollidersEnabled(bool enabled)
        {
            if (_agentColliders == null)
                return;

            for (int i = 0; i < _agentColliders.Length; i++)
            {
                if (_agentColliders[i] == null)
                    continue;

                _agentColliders[i].enabled =
                    enabled && _originalColliderStates[i];
            }

            Physics.SyncTransforms();
        }

        protected override void Awake()
        {
            base.Awake();
            _effectBlock = new MaterialPropertyBlock();
        }
    }
}