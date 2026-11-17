using System.Collections.Generic;
using UnityEngine;

namespace FireAndBrimstone
{
    /// <summary>
    /// Master hazard controller for all 3 rooms in the larger (20m x 32m) RoomEscape_Arena:
    /// - Room 1 (The Magma Grid): 5x5 grid of large 3.8m x 3.8m grates over lava. Sweeping wave with a shifting safe pocket.
    /// - Room 2 (Gargoyle Gauntlet - No Pillars): Wall-mounted gargoyles shoot straight forward all the way across the 20m room
    ///   in timed intervals (Cooldown -> Warning -> Shooting). Dynamic Beam BoxColliders let the agent's raycasts see the lanes.
    /// - Room 3 (Crossfire Cover - Pillars + Tracking Gargoyles): Gargoyles stay Idle until the agent enters 'room3ShootingRange'
    ///   (configurable in the Inspector). Once in range, they give a Warning and then Shoot for several seconds while keeping
    ///   their beam 100% locked onto the agent's center. Only hiding behind an obsidian 'Cover' pillar blocks the beam!
    /// </summary>
    public class HazardManager : MonoBehaviour
    {
        public enum GargoyleState
        {
            IdleOrCooldown,
            Warning,
            Shooting
        }

        [System.Serializable]
        public class GridTileSlot
        {
            public int row; // 0 (near start) to 4 (near exit)
            public int col; // 0 (left) to 4 (right)
            public GameObject tileObject;
            public Renderer tileRenderer;
            public GameObject firePillarHitbox; // Child object tagged "FireHitbox"
        }

        [System.Serializable]
        public class GargoyleTurretSlot
        {
            [Tooltip("Optional root object of the gargoyle statue (rotates toward agent in Room 3).")]
            public Transform gargoyleBodyTransform;
            [Tooltip("Point where the laser warning and fire stream originate.")]
            public Transform muzzleTransform;
            [Tooltip("LineRenderer used to draw the warning laser and active fire beam.")]
            public LineRenderer beamLineRenderer;
            [Tooltip("Optional flamethrower ParticleSystem played during the Shooting state.")]
            public ParticleSystem fireBeamVFX;
            [Tooltip("Optional child BoxCollider along the beam so RayPerceptionSensor3D sees the beam. Auto-created if left empty!")]
            public BoxCollider beamSensorCollider;

            [Header("Timing Settings (Seconds)")]
            public float cooldownDuration = 2.2f;
            public float warningDuration = 1.0f;
            public float shootingDuration = 2.0f;
            [Tooltip("Initial time offset used in Room 2 so adjacent gargoyles fire in staggered intervals.")]
            public float initialCycleOffset = 0.0f;

            [HideInInspector] public GargoyleState currentState = GargoyleState.IdleOrCooldown;
            [HideInInspector] public float stateTimer = 0f;

            [System.NonSerialized]
            public bool hasPendingFireHit;

            [System.NonSerialized]
            public float pendingFireHitTimer;

            // Information about the fire stream currently travelling.
            [System.NonSerialized]
            public Vector3 pendingFireOrigin;

            [System.NonSerialized]
            public Vector3 pendingFireDirection;

            [System.NonSerialized]
            public float pendingFireMaxDistance;
        }

        [Header("Shared Materials (Room 1 Grid)")]
        public Material tileSafeMaterial;
        public Material tileWarningMaterial;
        public Material tileEruptingMaterial;

        [Header("Room 1: 5x5 Large Magma Grid Configuration")]
        public List<GridTileSlot> room1GridTiles = new List<GridTileSlot>();
        public float room1WarningDuration = 1.1f;
        public float room1EruptDuration = 1.8f;

        [Header("Room 2: Fixed-Direction Gargoyles (No Pillars, Full-Room Beams)")]
        public List<GargoyleTurretSlot> room2FixedGargoyles = new List<GargoyleTurretSlot>();
        [Tooltip("Maximum distance for Room 2 fixed beams (22m reaches all the way across the 20m wide room).")]
        public float room2FullRoomBeamLength = 22.0f;

        [Header("Room 3: Range-Limited Tracking Gargoyles (With Obsidian Pillars)")]
        public List<GargoyleTurretSlot> room3TrackingGargoyles = new List<GargoyleTurretSlot>();
        [Tooltip("Inspector-adjustable shooting range for all Room 3 gargoyles. Gargoyles stay idle when Agent is farther than this.")]
        public float room3ShootingRange = 12.5f;

        [Header("Gargoyle Beam Visuals & Raycast Mask")]
        public LayerMask beamObstructionMask = ~0;
        public Color warningLaserColor = new Color(1f, 0.75f, 0.0f, 0.9f);
        public Color firingBeamColor = new Color(1f, 0.2f, 0.0f, 1.0f);
        public float warningLaserWidth = 0.12f;
        public float firingBeamWidth = 0.45f;
        [Tooltip("Thickness of the dynamic BoxCollider along each beam so the Agent's raycasts reliably detect it.")]
        public float beamSensorColliderThickness = 0.65f;

        [Tooltip("Approximate fire travel speed in Unity units per second.")]
        [Min(0.1f)]
        public float fireTravelSpeed = 18f;

        [Header("References")]
        public CurriculumManager curriculumManager;
        public CubeAgent arenaAgent;

        // Room 1 wave state
        private int _activeWaveRow = 0;
        private int _safeColumnInWaveRow = 2;
        private float _waveTimer = 0f;
        private bool _isWaveErupting = false;
        private Transform _trackedAgentTransform;

        private void Awake()
        {
            EnsureBeamCollidersInitialized(room2FixedGargoyles);
            EnsureBeamCollidersInitialized(room3TrackingGargoyles);
        }

        /// <summary>
        /// Automatically creates a trigger BoxCollider child for each Gargoyle if one isn't manually assigned,
        /// ensuring RayPerceptionSensor3D can always detect both Warning and Active Fire beams.
        /// </summary>
        private void EnsureBeamCollidersInitialized(List<GargoyleTurretSlot> list)
        {
            foreach (var g in list)
            {
                if (g.muzzleTransform == null) continue;
                if (g.beamSensorCollider == null)
                {
                    GameObject beamColObj = new GameObject("Auto_BeamSensorCollider");
                    beamColObj.transform.SetParent(g.muzzleTransform, false);
                    g.beamSensorCollider = beamColObj.AddComponent<BoxCollider>();
                }
                g.beamSensorCollider.isTrigger = true;
                g.beamSensorCollider.gameObject.SetActive(false);
            }
        }

        // =====================================================================
        // ROOM RESET METHODS
        // =====================================================================

        public void ResetRoom1MagmaGrid()
        {
            _activeWaveRow = Random.Range(0, 5);
            _safeColumnInWaveRow = Random.Range(0, 5);
            _waveTimer = 0f;
            _isWaveErupting = false;
            ApplyRoom1GridStates();
        }

        public void ResetRoom2FixedGargoyles(Transform agentTransform)
        {
            _trackedAgentTransform = agentTransform;
            EnsureBeamCollidersInitialized(room2FixedGargoyles);

            for (int i = 0; i < room2FixedGargoyles.Count; i++)
            {
                var g = room2FixedGargoyles[i];
                g.currentState = GargoyleState.IdleOrCooldown;
                // Apply staggered offset + tiny random jitter so the agent reads the visual warnings
                g.stateTimer = Mathf.Repeat(g.initialCycleOffset + Random.Range(0f, 0.35f), g.cooldownDuration);
                DisableGargoyleBeamVisuals(g);

                g.hasPendingFireHit = false;
                g.pendingFireHitTimer = 0f;
                g.pendingFireOrigin = Vector3.zero;
                g.pendingFireDirection = Vector3.zero;
                g.pendingFireMaxDistance = 0f;
            }
        }

        public void ResetRoom3TrackingGargoyles(Transform agentTransform)
        {
            _trackedAgentTransform = agentTransform;
            EnsureBeamCollidersInitialized(room3TrackingGargoyles);

            for (int i = 0; i < room3TrackingGargoyles.Count; i++)
            {
                var g = room3TrackingGargoyles[i];
                g.currentState = GargoyleState.IdleOrCooldown;
                // Start ready to trigger as soon as the agent steps into range
                g.stateTimer = g.cooldownDuration;
                DisableGargoyleBeamVisuals(g);

                g.hasPendingFireHit = false;
                g.pendingFireHitTimer = 0f;
                g.pendingFireOrigin = Vector3.zero;
                g.pendingFireDirection = Vector3.zero;
                g.pendingFireMaxDistance = 0f;
            }
        }

        private void DisableGargoyleBeamVisuals(GargoyleTurretSlot g)
        {
            if (g.beamLineRenderer != null)
            {
                g.beamLineRenderer.positionCount = 2;
                g.beamLineRenderer.enabled = false;
            }
            if (g.fireBeamVFX != null && g.fireBeamVFX.isPlaying)
            {
                g.fireBeamVFX.Stop();
            }
            if (g.beamSensorCollider != null)
            {
                g.beamSensorCollider.gameObject.SetActive(false);
            }
        }

        // =====================================================================
        // UPDATE LOOP
        // =====================================================================

        private void Update()
        {
            if (curriculumManager == null)
                return;

            switch (curriculumManager.CurrentRoomIndex)
            {
                case 0:
                    UpdateRoom1MagmaGrid();
                    break;

                case 1:
                    if (ProcessPendingFireHits(room2FixedGargoyles))
                        return;

                    UpdateRoom2FixedGargoyles();
                    break;

                case 2:
                    if (ProcessPendingFireHits(room3TrackingGargoyles))
                        return;

                    UpdateRoom3TrackingGargoyles();
                    break;
            }
        }

        // ---------------------------------------------------------------------
        // ROOM 1: 5x5 MAGMA GRID
        // ---------------------------------------------------------------------
        private void UpdateRoom1MagmaGrid()
        {
            _waveTimer += Time.deltaTime;

            if (!_isWaveErupting)
            {
                if (_waveTimer >= room1WarningDuration)
                {
                    _waveTimer = 0f;
                    _isWaveErupting = true;
                    ApplyRoom1GridStates();
                }
            }
            else
            {
                if (_waveTimer >= room1EruptDuration)
                {
                    _waveTimer = 0f;
                    _isWaveErupting = false;

                    _activeWaveRow = (_activeWaveRow + 4) % 5;
                    int colShift = Random.Range(-1, 2);
                    _safeColumnInWaveRow = Mathf.Clamp(_safeColumnInWaveRow + colShift, 0, 4);

                    ApplyRoom1GridStates();
                }
            }
        }

        private void ApplyRoom1GridStates()
        {
            int secondaryRow = (_activeWaveRow + 2) % 5;
            int secondarySafeCol = (4 - _safeColumnInWaveRow);

            foreach (var slot in room1GridTiles)
            {
                if (slot.tileObject == null) continue;

                bool isInPrimaryRow = (slot.row == _activeWaveRow && slot.col != _safeColumnInWaveRow);
                bool isInSecondaryRow = (slot.row == secondaryRow && slot.col != secondarySafeCol);
                bool isHazardTile = isInPrimaryRow || isInSecondaryRow;

                if (!isHazardTile)
                {
                    slot.tileObject.tag = "GridTile_Safe";
                    if (slot.tileRenderer != null && tileSafeMaterial != null)
                        slot.tileRenderer.sharedMaterial = tileSafeMaterial;
                    if (slot.firePillarHitbox != null)
                        slot.firePillarHitbox.SetActive(false);
                }
                else if (!_isWaveErupting)
                {
                    slot.tileObject.tag = "GridTile_Warning";
                    if (slot.tileRenderer != null && tileWarningMaterial != null)
                        slot.tileRenderer.sharedMaterial = tileWarningMaterial;
                    if (slot.firePillarHitbox != null)
                        slot.firePillarHitbox.SetActive(false);
                }
                else
                {
                    slot.tileObject.tag = "GridTile_Warning";
                    if (slot.tileRenderer != null && tileEruptingMaterial != null)
                        slot.tileRenderer.sharedMaterial = tileEruptingMaterial;
                    if (slot.firePillarHitbox != null)
                        slot.firePillarHitbox.SetActive(true);
                }
            }
        }

        // ---------------------------------------------------------------------
        // ROOM 2: FIXED-DIRECTION GARGOYLE GAUNTLET (NO PILLARS, FULL WIDTH)
        // ---------------------------------------------------------------------
        private void UpdateRoom2FixedGargoyles()
        {
            foreach (var g in room2FixedGargoyles)
            {
                if (g.muzzleTransform == null) continue;

                g.stateTimer += Time.deltaTime;

                switch (g.currentState)
                {
                    case GargoyleState.IdleOrCooldown:
                        DisableGargoyleBeamVisuals(g);
                        if (g.stateTimer >= g.cooldownDuration)
                        {
                            g.stateTimer = 0f;
                            g.currentState = GargoyleState.Warning;
                        }
                        break;

                    case GargoyleState.Warning:
                        CastAndRenderBeam(
                            g,
                            g.muzzleTransform.position,
                            -g.muzzleTransform.forward,
                            room2FullRoomBeamLength,
                            isShootingFire: false
                        );
                        if (g.stateTimer >= g.warningDuration)
                        {
                            g.stateTimer = 0f;
                            g.currentState = GargoyleState.Shooting;
                        }
                        break;

                    case GargoyleState.Shooting:

                        if (CastAndRenderBeam(
                            g,
                            g.muzzleTransform.position,
                            -g.muzzleTransform.forward,
                            room2FullRoomBeamLength,
                            isShootingFire: true,
                            elapsedShootingTime: Mathf.Max(0f, g.stateTimer - Time.deltaTime)))
                        {
                            return;
                        }

                        if (g.stateTimer >= g.shootingDuration)
                        {
                            g.stateTimer = 0f;
                            g.currentState = GargoyleState.IdleOrCooldown;
                            DisableGargoyleBeamVisuals(g);
                        }
                        break;
                }
            }
        }

        // ---------------------------------------------------------------------
        // ROOM 3: RANGE-LIMITED TRACKING GARGOYLES (WITH OBSIDIAN PILLARS)
        // ---------------------------------------------------------------------

        private void UpdateRoom3TrackingGargoyles()
        {
            if (_trackedAgentTransform == null && arenaAgent != null)
                _trackedAgentTransform = arenaAgent.transform;

            if (_trackedAgentTransform == null)
                return;

            // Position of the agent's approximate center.
            Vector3 agentCenter =
                _trackedAgentTransform.position + Vector3.up * 0.4f;

            foreach (var g in room3TrackingGargoyles)
            {
                if (g.muzzleTransform == null)
                    continue;

                // Position of this gargoyle's muzzle.
                Vector3 origin = g.muzzleTransform.position;

                float distToAgent = Vector3.Distance(origin, agentCenter);
                bool agentInRange = distToAgent <= room3ShootingRange;

                switch (g.currentState)
                {
                    case GargoyleState.IdleOrCooldown:
                    {
                        DisableGargoyleBeamVisuals(g);
                        g.stateTimer += Time.deltaTime;

                        if (g.stateTimer >= g.cooldownDuration && agentInRange)
                        {
                            g.stateTimer = 0f;
                            g.currentState = GargoyleState.Warning;
                        }

                        break;
                    }

                    case GargoyleState.Warning:
                    {
                        if (!agentInRange)
                        {
                            g.currentState = GargoyleState.IdleOrCooldown;
                            g.stateTimer = g.cooldownDuration;
                            DisableGargoyleBeamVisuals(g);
                            break;
                        }

                        g.stateTimer += Time.deltaTime;

                        RotateGargoyleLockedToAgent(g, origin, agentCenter);

                        Vector3 warnDir = (agentCenter - origin).normalized;

                        CastAndRenderBeam(
                            g,
                            origin,
                            warnDir,
                            room3ShootingRange,
                            isShootingFire: false
                        );

                        if (g.stateTimer >= g.warningDuration)
                        {
                            g.stateTimer = 0f;
                            g.currentState = GargoyleState.Shooting;
                        }

                        break;
                    }

                    case GargoyleState.Shooting:
                    {
                        g.stateTimer += Time.deltaTime;

                        RotateGargoyleLockedToAgent(g, origin, agentCenter);

                        Vector3 fireDir = (agentCenter - origin).normalized;

                        // Preserve the fire's progress throughout this shooting cycle.
                        if (CastAndRenderBeam(
                            g,
                            origin,
                            fireDir,
                            room3ShootingRange,
                            isShootingFire: true,
                            elapsedShootingTime: Mathf.Max(
                                0f,
                                g.stateTimer - Time.deltaTime
                            )))
                        {
                            return;
                        }

                        if (g.stateTimer >= g.shootingDuration)
                        {
                            g.stateTimer = 0f;
                            g.currentState = GargoyleState.IdleOrCooldown;
                            DisableGargoyleBeamVisuals(g);
                        }

                        break;
                    }
                }
            }
        }


        private void RotateGargoyleLockedToAgent(GargoyleTurretSlot g, Vector3 muzzleOrigin, Vector3 agentCenter)
        {
            Vector3 flatDir = agentCenter - muzzleOrigin;
            flatDir.y = 0f;
            if (flatDir.sqrMagnitude > 0.001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(-flatDir.normalized, Vector3.up);
                if (g.gargoyleBodyTransform != null)
                {
                    g.gargoyleBodyTransform.rotation = targetRot;
                }
                g.muzzleTransform.rotation = Quaternion.LookRotation(-(agentCenter - muzzleOrigin).normalized, Vector3.up);
            }
        }

        /// <summary>
        /// Performs the raycast for both Room 2 and Room 3 gargoyles, stops the beam at any Wall or Cover pillar,
        /// updates the LineRenderer + ParticleSystem, and scales the dynamic BoxCollider along the beam so the
        /// Agent's RayPerceptionSensorComponent3D can see the warning and fire lines!
        /// </summary>
        private bool CastAndRenderBeam(
            GargoyleTurretSlot g,
            Vector3 origin,
            Vector3 direction,
            float maxRange,
            bool isShootingFire,
            float elapsedShootingTime = -1f)
        {
            bool killImmediately = false;

            float actualLength = maxRange;
            Vector3 endPoint = origin + direction * maxRange;

            RaycastHit hit = default;
            bool hasHit = false;
            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                direction,
                maxRange,
                beamObstructionMask,
                QueryTriggerInteraction.Ignore
            );
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit candidate in hits)
            {
                // Ignore colliders belonging to this gargoyle.
                if ((g.gargoyleBodyTransform != null &&
                    (candidate.collider.transform == g.gargoyleBodyTransform ||
                     candidate.collider.transform.IsChildOf(g.gargoyleBodyTransform))) ||
                    (g.muzzleTransform != null &&
                    (candidate.collider.transform == g.muzzleTransform ||
                     candidate.collider.transform.IsChildOf(g.muzzleTransform))))
                {
                    continue;
                }

                hit = candidate;
                hasHit = true;
                actualLength = hit.distance;
                endPoint = hit.point;
                break;
            }

            if (hasHit && isShootingFire && IsAgentCollider(hit.collider))
            {
                float safeFireSpeed = Mathf.Max(0.1f, fireTravelSpeed);

                if (elapsedShootingTime >= 0f)
                {
                    // Calculate how much time is left before the fire reaches
                    // this position.
                    float totalTravelTime = hit.distance / safeFireSpeed;

                    float remainingTravelTime =
                        totalTravelTime - elapsedShootingTime;

                    // Update the trajectory to reflect the current hit.
                    g.pendingFireOrigin = origin;
                    g.pendingFireDirection = direction.normalized;
                    g.pendingFireMaxDistance = hit.distance;

                    if (remainingTravelTime <= 0f)
                    {
                        // The fire has already reached this position.
                        ClearPendingFireHit(g);
                        killImmediately = true;
                    }
                    else
                    {
                        // Schedule only the remaining travel time.
                        g.pendingFireHitTimer = remainingTravelTime;
                        g.hasPendingFireHit = true;
                    }
                }
                else if (!g.hasPendingFireHit)
                {
                    // Room 3 retains its existing full-travel-time behavior.
                    float travelTime = hit.distance / safeFireSpeed;

                    g.pendingFireOrigin = origin;
                    g.pendingFireDirection = direction.normalized;
                    g.pendingFireMaxDistance = hit.distance;

                    g.pendingFireHitTimer = Mathf.Max(0.05f, travelTime);
                    g.hasPendingFireHit = true;
                }
            }

            // Update LineRenderer
            // 1. Show the laser ONLY during the warning phase
            if (g.beamLineRenderer != null)
            {
                if (isShootingFire)
                {
                    g.beamLineRenderer.enabled = false;
                }
                else
                {
                    g.beamLineRenderer.enabled = true;

                    g.beamLineRenderer.startWidth = warningLaserWidth;
                    g.beamLineRenderer.endWidth = warningLaserWidth;

                    g.beamLineRenderer.startColor = warningLaserColor;
                    g.beamLineRenderer.endColor = warningLaserColor;

                    g.beamLineRenderer.SetPosition(0, origin);
                    g.beamLineRenderer.SetPosition(1, endPoint);
                }
            }

            // 2. Update Flamethrower ParticleSystem
            if (g.fireBeamVFX != null)
            {
                if (isShootingFire)
                {
                    if (!g.fireBeamVFX.isPlaying)
                        g.fireBeamVFX.Play(true);
                }
                else if (g.fireBeamVFX.isPlaying)
                {
                    g.fireBeamVFX.Stop(
                        true,
                        ParticleSystemStopBehavior.StopEmittingAndClear
                    );
                }
            }

            // 3. Update the invisible beam collider for Agent Ray Perception
            if (g.beamSensorCollider != null)
            {
                g.beamSensorCollider.gameObject.SetActive(true);

                // Keep this collider non-lethal.
                // Actual fire damage is handled by the delayed raycast below.
                g.beamSensorCollider.gameObject.tag = "GridTile_Warning";

                Transform colTransform = g.beamSensorCollider.transform;

                colTransform.position =
                    origin + direction * (actualLength * 0.5f);

                colTransform.rotation =
                    Quaternion.LookRotation(direction, Vector3.up);

                g.beamSensorCollider.center = Vector3.zero;

                g.beamSensorCollider.size = new Vector3(
                    beamSensorColliderThickness,
                    beamSensorColliderThickness,
                    Mathf.Max(0.2f, actualLength)
                );
            }

            if (killImmediately)
            {
                arenaAgent.KillAgentWithFire();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Supplies the 3-float Gargoyle telemetry to CubeAgent.CollectObservations()
        /// so the neural network knows the exact timing state of the closest gargoyle ahead.
        /// </summary>
        public void GetNearestGargoyleTelemetry(
            Vector3 agentWorldPos,
            int roomIndex,
            out float threatLevel,
            out float normalizedPhaseTime,
            out float normalizedDistance)
        {
            threatLevel = 0f;
            normalizedPhaseTime = 0f;
            normalizedDistance = 1f;

            List<GargoyleTurretSlot> activeList = (roomIndex == 1) ? room2FixedGargoyles : room3TrackingGargoyles;
            float maxRefRange = (roomIndex == 1) ? room2FullRoomBeamLength : Mathf.Max(1f, room3ShootingRange);

            GargoyleTurretSlot closest = null;
            float bestDist = float.MaxValue;

            foreach (var g in activeList)
            {
                if (g.muzzleTransform == null) continue;
                float d = Vector3.Distance(agentWorldPos, g.muzzleTransform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    closest = g;
                }
            }

            if (closest == null) return;

            normalizedDistance = Mathf.Clamp01(bestDist / maxRefRange);

            switch (closest.currentState)
            {
                case GargoyleState.IdleOrCooldown:
                    threatLevel = 0.0f;
                    normalizedPhaseTime = Mathf.Clamp01(closest.stateTimer / Mathf.Max(0.1f, closest.cooldownDuration));
                    break;
                case GargoyleState.Warning:
                    threatLevel = 0.5f;
                    normalizedPhaseTime = Mathf.Clamp01(closest.stateTimer / Mathf.Max(0.1f, closest.warningDuration));
                    break;
                case GargoyleState.Shooting:
                    threatLevel = 1.0f;
                    normalizedPhaseTime = Mathf.Clamp01(closest.stateTimer / Mathf.Max(0.1f, closest.shootingDuration));
                    break;
            }
        }

        private bool ProcessPendingFireHits(
            List<GargoyleTurretSlot> turrets)
        {
            if (arenaAgent == null)
                return false;

            foreach (var g in turrets)
            {
                if (!g.hasPendingFireHit)
                    continue;

                // Fire is still travelling.
                g.pendingFireHitTimer -= Time.deltaTime;

                if (g.pendingFireHitTimer > 0f)
                    continue;

                // Fire has reached its expected impact time.
                // Check whether the agent is STILL in its original path.
                bool agentStillInDanger = FireStillHitsAgent(g);

                // Clear the pending hit regardless of the result.
                g.hasPendingFireHit = false;
                g.pendingFireHitTimer = 0f;
                g.pendingFireOrigin = Vector3.zero;
                g.pendingFireDirection = Vector3.zero;
                g.pendingFireMaxDistance = 0f;

                // Only kill the agent if the fire actually reaches it.
                if (agentStillInDanger)
                {
                    arenaAgent.KillAgentWithFire();
                    return true;
                }
            }

            return false;
        }

        private bool IsAgentCollider(Collider col)
        {
            return arenaAgent != null &&
                col != null &&
                (col.transform == arenaAgent.transform ||
                    col.transform.IsChildOf(arenaAgent.transform));
        }

        private bool FireStillHitsAgent(GargoyleTurretSlot g)
        {
            if (arenaAgent == null ||
                g.pendingFireMaxDistance <= 0f ||
                g.pendingFireDirection.sqrMagnitude < 0.001f)
            {
                return false;
            }

            // Find every collider along the original fire trajectory.
            RaycastHit[] hits = Physics.RaycastAll(
                g.pendingFireOrigin,
                g.pendingFireDirection,
                g.pendingFireMaxDistance,
                beamObstructionMask,
                QueryTriggerInteraction.Ignore
            );

            // RaycastAll does not guarantee that hits are sorted.
            System.Array.Sort(
                hits,
                (a, b) => a.distance.CompareTo(b.distance)
            );

            foreach (RaycastHit hit in hits)
            {
                // Ignore the gargoyle itself, which might have rotated
                // since the fire was launched (particularly in Room 3).
                if (g.gargoyleBodyTransform != null &&
                    (hit.collider.transform == g.gargoyleBodyTransform ||
                    hit.collider.transform.IsChildOf(g.gargoyleBodyTransform)))
                {
                    continue;
                }

                // Only the FIRST actual object along the trajectory matters.
                return IsAgentCollider(hit.collider);
            }

            return false;
        }

        private void ClearPendingFireHit(GargoyleTurretSlot g)
        {
            g.hasPendingFireHit = false;
            g.pendingFireHitTimer = 0f;
            g.pendingFireOrigin = Vector3.zero;
            g.pendingFireDirection = Vector3.zero;
            g.pendingFireMaxDistance = 0f;
        }
    }
}