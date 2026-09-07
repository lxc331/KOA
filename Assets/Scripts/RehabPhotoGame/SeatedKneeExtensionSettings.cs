using System;

namespace RehabPhotoGame
{
    public enum TrainingLeg { Left, Right, Both }
    public enum KneeExtensionStage { Preparing, Extending, Holding, Returning }

    /// <summary>第1段技术验证参数，不是训练处方。</summary>
    [Serializable]
    public sealed class SeatedKneeExtensionSettings
    {
        public float readyKneeMinDeg = 35f;
        public float readyKneeMaxDeg = 135f;
        public float seatedThighMinDeg = 45f;
        public float seatedThighMaxDeg = 130f;
        public float minimumExtensionDeg = 25f;
        public float targetExitMarginDeg = 8f;
        public float returnToleranceDeg = 15f;
        public float stableRangeDeg = 12f;
        public int minimumConfirmedSamples = 2;
        public float readySeconds = 0.5f;
        public float holdSeconds = 3f;

        // 相比 d967ffa 略放宽到 2.2 秒，吸收 07～09 的正常无线抖动；
        // 不再采用 V1.6 的角度补偿逻辑。
        public float sensorTimeoutSeconds = 2.2f;
        public float maxSensorSkewSeconds = 2.2f;
        public float signalGraceSeconds = 4f;

        public bool IsValid =>
            Finite(readyKneeMinDeg) && Finite(readyKneeMaxDeg) &&
            Finite(seatedThighMinDeg) && Finite(seatedThighMaxDeg) &&
            Finite(minimumExtensionDeg) && Finite(targetExitMarginDeg) &&
            Finite(returnToleranceDeg) &&
            Finite(stableRangeDeg) && Finite(readySeconds) && Finite(holdSeconds) &&
            Finite(sensorTimeoutSeconds) && Finite(maxSensorSkewSeconds) &&
            Finite(signalGraceSeconds) &&
            minimumExtensionDeg > 0f && targetExitMarginDeg >= 0f &&
            returnToleranceDeg > 0f &&
            readyKneeMinDeg < readyKneeMaxDeg && readyKneeMaxDeg <= 180f &&
            seatedThighMinDeg > 0f && seatedThighMinDeg < seatedThighMaxDeg &&
            seatedThighMaxDeg <= 180f && stableRangeDeg > 0f &&
            minimumConfirmedSamples >= 2 && minimumConfirmedSamples <= 10 &&
            readySeconds > 0f && holdSeconds > 0f &&
            sensorTimeoutSeconds > 0f && maxSensorSkewSeconds > 0f &&
            maxSensorSkewSeconds <= sensorTimeoutSeconds &&
            signalGraceSeconds >= sensorTimeoutSeconds && signalGraceSeconds <= 30f;

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
