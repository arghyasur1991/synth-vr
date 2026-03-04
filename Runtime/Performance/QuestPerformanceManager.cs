using UnityEngine;

namespace Genesis.Sentience.VR
{
    /// <summary>
    /// Requests CPU/GPU performance levels from the Quest OS.
    /// Place on a persistent GameObject (e.g. "Global Settings") so it runs
    /// before physics-heavy components like SceneMeshManager and MjScene.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class QuestPerformanceManager : MonoBehaviour
    {
        [Tooltip("Request high CPU/GPU clocks for heavy physics scenes")]
        public bool boostPerformance = true;

        public OVRManager.ProcessorPerformanceLevel cpuLevel =
            OVRManager.ProcessorPerformanceLevel.SustainedHigh;

        public OVRManager.ProcessorPerformanceLevel gpuLevel =
            OVRManager.ProcessorPerformanceLevel.SustainedHigh;

        void Awake()
        {
            if (!boostPerformance) return;

            OVRManager.suggestedCpuPerfLevel = cpuLevel;
            OVRManager.suggestedGpuPerfLevel = gpuLevel;
            Debug.Log($"[QuestPerf] Performance: CPU={cpuLevel}, GPU={gpuLevel}");
        }
    }
}
