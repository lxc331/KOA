using System;

namespace RehabPhotoGame
{
    /// <summary>第3段坐站摄影联调参数，不是医学训练处方。</summary>
    [Serializable]
    public sealed class SitToStandSettings
    {
        public float seatedThighMinDeg = 60f;
        public float seatedThighMaxDeg = 135f;
        public float seatedKneeMinDeg = 55f;
        public float seatedKneeMaxDeg = 145f;

        public float standingThighMaxDeg = 35f;
        public float standingKneeMaxDeg = 30f;
        public float minimumReferenceSpanDeg = 25f;
        public float returnedToleranceDeg = 18f;
        public float maxLegProgressDifference = 0.30f;
        public float maxStandingAsymmetryDeg = 20f;

        public float stableRangeDeg = 12f;
        public int minimumConfirmedSamples = 2;
        public float readySeconds = 0.7f;
        public float holdSeconds = 2f;
        public float returnSeconds = 0.6f;

        public float sensorTimeoutSeconds = 2.2f;
        public float maxSensorSkewSeconds = 2.2f;
        public float signalGraceSeconds = 4f;

        public bool IsValid =>
            Finite(seatedThighMinDeg) && Finite(seatedThighMaxDeg) &&
            Finite(seatedKneeMinDeg) && Finite(seatedKneeMaxDeg) &&
            Finite(standingThighMaxDeg) && Finite(standingKneeMaxDeg) &&
            Finite(minimumReferenceSpanDeg) && Finite(returnedToleranceDeg) &&
            Finite(maxLegProgressDifference) && Finite(maxStandingAsymmetryDeg) &&
            Finite(stableRangeDeg) && Finite(readySeconds) &&
            Finite(holdSeconds) && Finite(returnSeconds) &&
            Finite(sensorTimeoutSeconds) && Finite(maxSensorSkewSeconds) &&
            Finite(signalGraceSeconds) &&
            seatedThighMinDeg >= 0f && seatedThighMinDeg < seatedThighMaxDeg &&
            seatedThighMaxDeg <= 180f &&
            seatedKneeMinDeg >= 0f && seatedKneeMinDeg < seatedKneeMaxDeg &&
            seatedKneeMaxDeg <= 180f &&
            standingThighMaxDeg >= 0f && standingThighMaxDeg < seatedThighMinDeg &&
            standingKneeMaxDeg >= 0f && standingKneeMaxDeg < seatedKneeMinDeg &&
            minimumReferenceSpanDeg > 0f && returnedToleranceDeg > 0f &&
            seatedThighMinDeg - standingThighMaxDeg >= minimumReferenceSpanDeg &&
            seatedKneeMinDeg - standingKneeMaxDeg >= minimumReferenceSpanDeg &&
            maxLegProgressDifference > 0f && maxLegProgressDifference <= 1f &&
            maxStandingAsymmetryDeg >= 0f && stableRangeDeg > 0f &&
            minimumConfirmedSamples >= 2 && minimumConfirmedSamples <= 10 &&
            readySeconds > 0f && holdSeconds > 0f && returnSeconds > 0f &&
            sensorTimeoutSeconds > 0f && maxSensorSkewSeconds > 0f &&
            maxSensorSkewSeconds <= sensorTimeoutSeconds &&
            signalGraceSeconds >= sensorTimeoutSeconds && signalGraceSeconds <= 30f;

        internal static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
