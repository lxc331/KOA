using System;

namespace RehabPhotoGame
{
    /// <summary>
    /// 与 Unity 绘制解耦的摄影反馈状态机，便于自动回归测试。
    /// </summary>
    public sealed class PhotoFocusSession
    {
        private bool hasPrevious;
        private int previousSessionVersion;
        private TrainingLeg previousLeg;
        private KneeExtensionStage previousStage;

        public float Focus01 { get; private set; }
        public int CapturedPhotos { get; private set; }

        /// <returns>本帧是否应触发一次快门。</returns>
        public bool Update(KneeExtensionTrainingSnapshot snapshot)
        {
            bool changedSession = !hasPrevious ||
                                  snapshot.SessionVersion != previousSessionVersion ||
                                  snapshot.Leg != previousLeg;
            if (changedSession)
            {
                Focus01 = FocusFor(snapshot, 0f);
                CapturedPhotos = 0;
                Remember(snapshot);
                return false;
            }

            bool capture = !snapshot.IsSwitchingLeg && snapshot.IsDataValid &&
                           previousStage == KneeExtensionStage.Holding &&
                           snapshot.Stage == KneeExtensionStage.Returning;

            // 短时断流时保持已有清晰度，不让旧帧继续对焦，也不触发快门。
            Focus01 = FocusFor(snapshot, Focus01);
            if (capture)
                CapturedPhotos++;

            Remember(snapshot);
            return capture;
        }

        public void Reset()
        {
            hasPrevious = false;
            Focus01 = 0f;
            CapturedPhotos = 0;
        }

        public static float CalculateFocus(float kneeAngleDeg, float readyDeg, float targetDeg)
        {
            if (!Finite(kneeAngleDeg) || !Finite(readyDeg) || !Finite(targetDeg) ||
                readyDeg <= targetDeg)
                return 0f;

            float value = (readyDeg - kneeAngleDeg) / (readyDeg - targetDeg);
            if (value <= 0f) return 0f;
            if (value >= 1f) return 1f;
            return value;
        }

        private static float FocusFor(KneeExtensionTrainingSnapshot snapshot, float frozenValue)
        {
            if (snapshot.IsSwitchingLeg)
                return 0f;
            if (!snapshot.IsDataValid)
                return frozenValue;
            if (!snapshot.HasReadyReference || snapshot.Stage == KneeExtensionStage.Preparing)
                return 0f;
            if (snapshot.Stage == KneeExtensionStage.Returning)
                return 1f;
            return CalculateFocus(
                snapshot.KneeAngleDeg,
                snapshot.ReadyReferenceKneeDeg,
                snapshot.TargetKneeDeg);
        }

        private void Remember(KneeExtensionTrainingSnapshot snapshot)
        {
            hasPrevious = true;
            previousSessionVersion = snapshot.SessionVersion;
            previousLeg = snapshot.Leg;
            previousStage = snapshot.Stage;
        }

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
