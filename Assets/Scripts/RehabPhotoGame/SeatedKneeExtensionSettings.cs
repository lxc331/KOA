using System;

namespace RehabPhotoGame
{
    public enum TrainingLeg { Left, Right, Both }
    public enum KneeExtensionStage { Preparing, Extending, Holding, Returning }

    /// <summary>第1段技术验证参数，不是训练处方。</summary>
    [Serializable]
    public sealed class SeatedKneeExtensionSettings
    {
        public float readyKneeMinDeg = 70f;
        public float readyKneeMaxDeg = 110f;
        public float seatedThighMinDeg = 55f;
        public float seatedThighMaxDeg = 120f;
        public float targetKneeMaxDeg = 20f;
        public float targetExitMarginDeg = 5f;
        public float stableRangeDeg = 5f;
        public float readySeconds = 0.6f;
        public float holdSeconds = 3f;
        public float sensorTimeoutSeconds = 1.5f;
        public float maxSensorSkewSeconds = 1f;

        public bool IsValid =>
            Finite(readyKneeMinDeg) && Finite(readyKneeMaxDeg) &&
            Finite(seatedThighMinDeg) && Finite(seatedThighMaxDeg) &&
            Finite(targetKneeMaxDeg) && Finite(targetExitMarginDeg) &&
            Finite(stableRangeDeg) && Finite(readySeconds) && Finite(holdSeconds) &&
            Finite(sensorTimeoutSeconds) && Finite(maxSensorSkewSeconds) &&
            targetKneeMaxDeg >= 0f && targetExitMarginDeg >= 0f &&
            targetKneeMaxDeg + targetExitMarginDeg < readyKneeMinDeg &&
            readyKneeMinDeg < readyKneeMaxDeg && readyKneeMaxDeg <= 180f &&
            seatedThighMinDeg > 0f && seatedThighMinDeg < seatedThighMaxDeg &&
            seatedThighMaxDeg <= 180f && stableRangeDeg > 0f &&
            readySeconds > 0f && holdSeconds > 0f &&
            sensorTimeoutSeconds > 0f && maxSensorSkewSeconds > 0f &&
            maxSensorSkewSeconds <= sensorTimeoutSeconds;

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
