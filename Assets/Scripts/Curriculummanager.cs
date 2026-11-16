using UnityEngine;
using Unity.MLAgents;

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
        [Tooltip("TrainingCurriculum reads lesson 0-2 from config.yaml. ForceRoom locks a specific stage.")]
        public ArenaMode arenaMode = ArenaMode.TrainingCurriculum;

        [Header("Room Root GameObjects (Inside EnvironmentAnchor)")]
        public GameObject room1MagmaGrid;
        public GameObject room2GargoyleGauntlet;
        public GameObject room3CrossfireCover;

        [Header("Controllers & HUD")]
        public HazardManager hazardManager;
        public ArenaHUD arenaHUD;

        [Header("Episode Settings")]
        [Tooltip("Maximum episode time in seconds for the larger 20m x 32m rooms.")]
        public float maxEpisodeTimeSeconds = 45f;

        public int CurrentRoomIndex { get; private set; } = 0;
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
        /// Reads the curriculum lesson (0, 1, or 2) and activates the corresponding room layout.
        /// </summary>
        public int ResetArenaForNewEpisode(CubeAgent agent)
        {
            AttemptCount++;
            RemainingTime = maxEpisodeTimeSeconds;

            if (arenaMode == ArenaMode.TrainingCurriculum)
            {
                float lessonValue = Academy.Instance.EnvironmentParameters.GetWithDefault("room_lesson", 0f);
                CurrentRoomIndex = Mathf.Clamp(Mathf.RoundToInt(lessonValue), 0, 2);
            }
            else
            {
                CurrentRoomIndex = (int)arenaMode;
            }

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
    }
}