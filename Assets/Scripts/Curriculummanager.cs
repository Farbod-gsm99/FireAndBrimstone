using UnityEngine;

namespace FireAndBrimstone
{
    public enum ArenaMode
    {
        TrainingCurriculum = -1,
        ForceRoom1_MagmaGrid = 0,
        ForceRoom2_GargoyleGauntlet = 1,
        ForceRoom3_CrossfireCover = 2
    }

    /// <summary>
    /// Manages the 3-stage room progression inside the larger (20m x 32m) RoomEscape_Arena prefab:
    /// - Room 1 (Lesson 0): The Magma Grid (5x5 large tiles, sweeping fire wave with safe pocket).
    /// - Room 2 (Lesson 1): The Gargoyle Gauntlet (No pillars; wall gargoyles shoot straight across the whole room).
    /// - Room 3 (Lesson 2): Crossfire Cover (Obsidian pillars + range-limited tracking gargoyles).
    /// </summary>
    public class CurriculumManager : MonoBehaviour
    {
        [Header("Mode Selection (Use ForceRoom for YouTube Recording)")]
        [Tooltip("TrainingCurriculum advances after each successful room. ForceRoom locks a specific stage for testing or recording.")]
        public ArenaMode arenaMode = ArenaMode.TrainingCurriculum;

        [Header("Room Root GameObjects (Inside EnvironmentAnchor)")]
        public GameObject room1MagmaGrid;
        public GameObject room2GargoyleGauntlet;
        public GameObject room3CrossfireCover;

        [Header("Controllers & HUD")]
        public HazardManager hazardManager;
        public ArenaHUD arenaHUD;

        [Header("Episode Settings")]
        [Tooltip("Maximum episode time in seconds for Rooms 2 and 3.")]
        public float maxEpisodeTimeSeconds = 45f;
        [Tooltip("Room 1 gets a longer training budget so the agent can wait for the simplified wave and then cross.")]
        [Min(1f)]
        public float room1MaxEpisodeTimeSeconds = 60f;

        public int CurrentRoomIndex { get; private set; } = 1;
        public int AttemptCount { get; private set; } = 0;
        public float RemainingTime { get; private set; } = 45f;

        private GameObject[] _rooms;
        private readonly string[] _stageNames = new string[]
        {
            "STAGE 1: THE MAGMA GRID",
            "STAGE 2: GARGOYLE GAUNTLET",
            "STAGE 3: CROSSFIRE COVER"
        };

        private readonly string[] _stageSubtitles = new string[]
        {
            "WAIT FOR SAFE TILES & CROSS THE LAVA",
            "TIME THE FIRE LANES TO REACH THE EXIT",
            "HIDE BEHIND OBSIDIAN PILLARS TO SURVIVE"
        };

        private void Awake()
        {
            _rooms = new GameObject[]
            {
                room1MagmaGrid,
                room2GargoyleGauntlet,
                room3CrossfireCover
            };
        }

        private void Update()
        {
            if (RemainingTime > 0f)
            {
                RemainingTime -= Time.deltaTime;
                if (RemainingTime < 0f) RemainingTime = 0f;
            }

            if (arenaHUD != null)
            {
                arenaHUD.UpdateTimerDisplay(RemainingTime);
            }
        }

        /// <summary>
        /// Called at the start of every episode by CubeAgent.OnEpisodeBegin().
        /// In training mode, CurrentRoomIndex starts at Room 2, is retained after failures,
        /// and advances only when the agent successfully reaches the current room's exit.
        /// </summary>
        public int ResetArenaForNewEpisode(CubeAgent agent)
        {
            AttemptCount++;

            // Forced-room modes remain available for testing and recording.
            // TrainingCurriculum deliberately does not read an external lesson
            // parameter: progression is stored locally in this arena instance.
            if (arenaMode != ArenaMode.TrainingCurriculum)
            {
                CurrentRoomIndex = Mathf.Clamp(
                    (int)arenaMode,
                    0,
                    _rooms.Length - 1
                );
            }
            else
            {
                CurrentRoomIndex = Mathf.Clamp(
                    CurrentRoomIndex,
                    0,
                    _rooms.Length - 1
                );
            }

            // Room 1 gets a larger time budget; Mathf.Max also protects against
            // older prefab instances that still carry the previous 45-second value.
            RemainingTime = CurrentRoomIndex == 0
                ? Mathf.Max(maxEpisodeTimeSeconds, room1MaxEpisodeTimeSeconds)
                : maxEpisodeTimeSeconds;

            for (int i = 0; i < _rooms.Length; i++)
            {
                if (_rooms[i] != null)
                {
                    _rooms[i].SetActive(i == CurrentRoomIndex);
                }
            }

            if (hazardManager != null)
            {
                if (CurrentRoomIndex == 0)
                {
                    hazardManager.ResetRoom1MagmaGrid();
                }
                else if (CurrentRoomIndex == 1)
                {
                    hazardManager.ResetRoom2FixedGargoyles(agent.transform);
                }
                else if (CurrentRoomIndex == 2)
                {
                    hazardManager.ResetRoom3TrackingGargoyles(agent.transform);
                }
            }

            if (arenaHUD != null)
            {
                arenaHUD.UpdateStageAndAttempt(
                    _stageNames[CurrentRoomIndex],
                    AttemptCount,
                    _stageSubtitles[CurrentRoomIndex]
                );
            }

            return CurrentRoomIndex;
        }

        /// <summary>
        /// Advances this arena's training agent after a successful room exit.
        /// A failed or timed-out episode never calls this method, so the agent
        /// remains in the same room until it completes it.
        /// After Room 3, the next episode starts a new training sequence at Room 2.
        /// </summary>
        public void AdvanceAfterSuccessfulRoom()
        {
            // Preserve the fixed-room behavior used for testing/recording.
            if (arenaMode != ArenaMode.TrainingCurriculum)
                return;

            if (_rooms == null || _rooms.Length == 0)
                return;

            if (CurrentRoomIndex >= _rooms.Length - 1)
            {
                // Begin the next training cycle at Room 2, not Room 1.
                CurrentRoomIndex = 1;
            }
            else
            {
                CurrentRoomIndex++;
            }
        }
    }
}