using UnityEngine;

namespace AntTrails
{
    /// <summary>
    /// Single per-frame driver. Sampling and simulation both need a heartbeat, and one
    /// component is cheaper and easier to reason about than patching Update on game types.
    /// </summary>
    internal class AntTrailsRunner : MonoBehaviour
    {
        private void Update()
        {
            if (ZNet.instance == null)
            {
                return;
            }

            float dt = Time.deltaTime;

            // A dedicated server has no local player; Sample() no-ops there.
            StepReporter.Tick(dt);

            if (TrailEngine.Active)
            {
                TrailEngine.Tick(dt);
            }
        }
    }
}
