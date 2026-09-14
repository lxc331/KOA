using System;
using UnityEngine;

namespace RehabPhotoGame
{
    /// <summary>游戏联调参数，仅影响浅蹲状态判定，不改变人物测量和驱动。</summary>
    [Serializable]
    public sealed class ShallowSquatSettings
    {
        [Header("站姿准备（仍需先站稳）")]
        public float standingThighMinDeg = -20f;
        public float standingThighMaxDeg = 25f;
        public float standingKneeMinDeg = 0f;
        public float standingKneeMaxDeg = 30f;

        [Header("游戏联调：双膝主判定")]
        [Tooltip("每侧膝角相对各自站姿参考至少增加的角度；总进度取较小一侧。")]
        public float minimumKneeFlexionDeg = 20f;
        [Tooltip("大腿相对站姿变化不足此值时仅作辅助提示，不阻止保持或拍摄。")]
        public float minimumThighFlexionDeg = 5f;
        [Tooltip("一侧已达到膝角目标，另一侧变化低于此值时提示明显单腿动作。")]
        public float singleLegMinimumFlexionDeg = 8f;
        [Tooltip("较小/较大膝屈曲量低于此比值时阻止明显单侧主导；普通差异只提示。")]
        public float singleLegMinimumRatio = 0.20f;

        [Header("过深调整（保留站姿参考）")]
        public float maximumShallowThighDeg = 80f;
        public float maximumShallowKneeDeg = 100f;
        [Tooltip("过深后需回到上限减去此角度，才重新开始保持计时，避免边缘反复切换。")]
        public float tooDeepRecoveryMarginDeg = 5f;
        public float returnedToleranceDeg = 15f;
        [Header("辅助提示与站姿对称性")]
        [Tooltip("归一化膝屈曲量差异的提示阈值，仅提示，不取消保持。")]
        public float maxLegProgressDifference = 0.50f;
        [Tooltip("站姿确认的左右角度差上限；下蹲时只用于提示相对膝屈曲量差异。")]
        public float maxAngleAsymmetryDeg = 20f;

        [Header("有效新样本计时")]
        [Tooltip("站姿/回位检查四个角度；浅蹲保持仅检查双膝稳定性。")]
        public float stableRangeDeg = 12f;
        public int minimumConfirmedSamples = 2;
        public float readySeconds = 0.7f;
        public float holdSeconds = 2f;
        public float returnSeconds = 0.6f;

        [Header("传感器健康（沿用现有门限）")]
        public float sensorTimeoutSeconds = 2.2f;
        public float maxSensorSkewSeconds = 2.2f;
        public float signalGraceSeconds = 4f;

        public bool IsValid =>
            Finite(standingThighMinDeg) && Finite(standingThighMaxDeg) &&
            Finite(standingKneeMinDeg) && Finite(standingKneeMaxDeg) &&
            Finite(minimumThighFlexionDeg) && Finite(minimumKneeFlexionDeg) &&
            Finite(singleLegMinimumFlexionDeg) && Finite(singleLegMinimumRatio) &&
            Finite(tooDeepRecoveryMarginDeg) &&
            Finite(maximumShallowThighDeg) && Finite(maximumShallowKneeDeg) &&
            Finite(returnedToleranceDeg) && Finite(maxLegProgressDifference) &&
            Finite(maxAngleAsymmetryDeg) && Finite(stableRangeDeg) &&
            Finite(readySeconds) && Finite(holdSeconds) && Finite(returnSeconds) &&
            Finite(sensorTimeoutSeconds) && Finite(maxSensorSkewSeconds) &&
            Finite(signalGraceSeconds) &&
            standingThighMinDeg >= -45f && standingThighMinDeg < standingThighMaxDeg &&
            standingKneeMinDeg >= 0f && standingKneeMinDeg < standingKneeMaxDeg &&
            minimumThighFlexionDeg >= 0f && minimumKneeFlexionDeg > 0f &&
            singleLegMinimumFlexionDeg >= 0f &&
            singleLegMinimumFlexionDeg < minimumKneeFlexionDeg &&
            singleLegMinimumRatio > 0f && singleLegMinimumRatio < 1f &&
            tooDeepRecoveryMarginDeg >= 0f &&
            maximumShallowThighDeg > standingThighMaxDeg &&
            maximumShallowKneeDeg > standingKneeMaxDeg &&
            maximumShallowThighDeg <= 90f && maximumShallowKneeDeg <= 100f &&
            maximumShallowThighDeg - tooDeepRecoveryMarginDeg > standingThighMaxDeg &&
            maximumShallowKneeDeg - tooDeepRecoveryMarginDeg >=
                standingKneeMaxDeg + minimumKneeFlexionDeg &&
            returnedToleranceDeg > 0f &&
            maxLegProgressDifference > 0f && maxLegProgressDifference <= 1f &&
            maxAngleAsymmetryDeg >= 0f && stableRangeDeg > 0f &&
            minimumConfirmedSamples >= 2 && minimumConfirmedSamples <= 10 &&
            readySeconds > 0f && holdSeconds > 0f && returnSeconds > 0f &&
            sensorTimeoutSeconds > 0f && maxSensorSkewSeconds > 0f &&
            maxSensorSkewSeconds <= sensorTimeoutSeconds &&
            signalGraceSeconds >= sensorTimeoutSeconds && signalGraceSeconds <= 30f;

        internal static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
