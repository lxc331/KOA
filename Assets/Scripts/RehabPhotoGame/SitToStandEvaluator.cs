using System;

namespace RehabPhotoGame
{
    /// <summary>
    /// 纯逻辑坐站状态机。只有左右大腿和小腿共同伸直才增加取景高度，
    /// 因而单腿前踢、单侧掉线或重复旧样本都不能触发拍摄。
    /// </summary>
    public sealed class SitToStandEvaluator
    {
        private readonly SitToStandSettings settings;
        private int calibrationVersion = -1;
        private float lastSampleTime = float.NegativeInfinity;
        private bool hasStableWindow;
        private float stableWindowStartSampleTime;
        private int stableWindowSamples;
        private LowerBodyMeasurement minimum;
        private LowerBodyMeasurement maximum;
        private bool signalPaused;
        private bool signalPauseExpired;
        private float signalPauseStartedAt;

        private float seatedLeftThigh = float.NaN;
        private float seatedLeftKnee = float.NaN;
        private float seatedRightThigh = float.NaN;
        private float seatedRightKnee = float.NaN;

        public SitToStandStage Stage { get; private set; } = SitToStandStage.Preparing;
        public int CompletedRepetitions { get; private set; }
        public int CaptureSequence { get; private set; }
        public float RaiseProgress01 { get; private set; }
        public float LeftProgress01 { get; private set; }
        public float RightProgress01 { get; private set; }
        public float HoldSeconds { get; private set; }
        public string BlockReason { get; private set; } = "等待有效数据";
        public bool SignalPaused => signalPaused || signalPauseExpired;
        public bool HasSeatedReference =>
            SitToStandSettings.Finite(seatedLeftThigh) &&
            SitToStandSettings.Finite(seatedLeftKnee) &&
            SitToStandSettings.Finite(seatedRightThigh) &&
            SitToStandSettings.Finite(seatedRightKnee);

        public SitToStandEvaluator(SitToStandSettings settings)
        {
            this.settings = settings;
        }

        public void Reset(bool clearCount)
        {
            ResetCycle();
            calibrationVersion = -1;
            signalPaused = false;
            signalPauseExpired = false;
            BlockReason = "等待坐姿准备";
            if (clearCount)
            {
                CompletedRepetitions = 0;
                CaptureSequence = 0;
            }
        }

        public void Update(LowerBodyMeasurement sample, float now)
        {
            if (settings == null || !settings.IsValid)
            {
                ResetCycle();
                BlockReason = "坐站参数无效，请检查配置";
                return;
            }

            if (IsRecoverableSignalFailure(sample))
            {
                PauseForSignal(now);
                return;
            }

            string failure = Validate(sample, now);
            if (failure != "")
            {
                ResetCycle();
                BlockReason = failure;
                return;
            }

            bool resumedFromSignalPause = ResumeAfterSignal();
            if (calibrationVersion != sample.CalibrationVersion)
            {
                ResetCycle();
                calibrationVersion = sample.CalibrationVersion;
            }

            bool newSample = sample.SampleTimeSeconds > lastSampleTime + 0.0001f;
            if (sample.SampleTimeSeconds < lastSampleTime ||
                (!resumedFromSignalPause && newSample &&
                 SitToStandSettings.Finite(lastSampleTime) &&
                 sample.SampleTimeSeconds - lastSampleTime > settings.sensorTimeoutSeconds))
            {
                ResetCycle();
                newSample = true;
            }

            if (resumedFromSignalPause && newSample && hasStableWindow &&
                SitToStandSettings.Finite(lastSampleTime))
            {
                stableWindowStartSampleTime +=
                    Math.Max(0f, sample.SampleTimeSeconds - lastSampleTime);
            }
            if (newSample)
                lastSampleTime = sample.SampleTimeSeconds;

            switch (Stage)
            {
                case SitToStandStage.Preparing:
                    UpdatePreparing(sample, newSample);
                    break;
                case SitToStandStage.Rising:
                    UpdateRising(sample, newSample);
                    break;
                case SitToStandStage.Holding:
                    UpdateHolding(sample, newSample);
                    break;
                case SitToStandStage.Returning:
                    UpdateReturning(sample, newSample);
                    break;
            }
        }

        private void UpdatePreparing(LowerBodyMeasurement sample, bool newSample)
        {
            RaiseProgress01 = LeftProgress01 = RightProgress01 = 0f;
            HoldSeconds = 0f;
            if (!IsSeatedCandidate(sample))
            {
                ResetStableWindow();
                BlockReason = "请坐稳，双脚着地并自然屈膝";
                return;
            }

            BlockReason = "正在确认坐姿准备";
            if (!StableFor(sample, newSample, settings.readySeconds, false))
                return;

            seatedLeftThigh = sample.LeftThighDeg;
            seatedLeftKnee = sample.LeftKneeDeg;
            seatedRightThigh = sample.RightThighDeg;
            seatedRightKnee = sample.RightKneeDeg;
            Enter(SitToStandStage.Rising);
            BlockReason = "准备完成，请双腿同时缓慢站起";
        }

        private void UpdateRising(LowerBodyMeasurement sample, bool newSample)
        {
            UpdateProgress(sample);
            if (!IsCoordinated())
            {
                ResetStableWindow();
                BlockReason = "请让左右腿同步用力站起";
                return;
            }

            BlockReason = "双腿协同伸直，让取景框升高";
            if (RaiseProgress01 < 0.999f || !IsStandingCandidate(sample))
                return;

            Enter(SitToStandStage.Holding);
            StableFor(sample, newSample, settings.holdSeconds, true);
            BlockReason = "已经站稳，请保持等待快门";
        }

        private void UpdateHolding(LowerBodyMeasurement sample, bool newSample)
        {
            UpdateProgress(sample);
            if (!IsCoordinated() || RaiseProgress01 < 0.999f ||
                !IsStandingCandidate(sample))
            {
                Enter(SitToStandStage.Rising);
                BlockReason = "尚未站稳，请双腿继续同步伸直";
                return;
            }

            BlockReason = "已经站稳，请保持等待快门";
            if (!StableFor(sample, newSample, settings.holdSeconds, true))
                return;

            CaptureSequence++;
            Enter(SitToStandStage.Returning);
            RaiseProgress01 = LeftProgress01 = RightProgress01 = 1f;
            BlockReason = "拍摄完成，请缓慢坐回准备姿势";
        }

        private void UpdateReturning(LowerBodyMeasurement sample, bool newSample)
        {
            UpdateProgress(sample);
            HoldSeconds = settings.holdSeconds;
            if (!ReturnedToReference(sample))
            {
                ResetStableWindow();
                BlockReason = "请缓慢坐回，双脚保持着地";
                return;
            }

            BlockReason = "正在确认已经坐稳";
            if (!StableFor(sample, newSample, settings.returnSeconds, false))
                return;

            CompletedRepetitions++;
            Enter(SitToStandStage.Preparing);
            ClearReferences();
            RaiseProgress01 = LeftProgress01 = RightProgress01 = 0f;
            BlockReason = "已完成一次，请坐稳准备下一次";
        }

        private string Validate(LowerBodyMeasurement sample, float now)
        {
            if (!sample.IsValid)
                return string.IsNullOrEmpty(sample.FailureReason)
                    ? "等待 06～09 有效数据"
                    : sample.FailureReason;
            if ((sample.FreshMask & 15) != 15)
                return "坐站需要的四个传感器未持续更新";
            if (!SitToStandSettings.Finite(now) ||
                !SitToStandSettings.Finite(sample.SampleTimeSeconds) ||
                now < sample.SampleTimeSeconds ||
                now - sample.SampleTimeSeconds > settings.sensorTimeoutSeconds)
                return "坐站传感器数据已超时";
            return AnglesValid(sample) ? "" : "坐站角度数据无效";
        }

        private static bool AnglesValid(LowerBodyMeasurement sample)
        {
            return Angle(sample.LeftThighDeg, -45f) &&
                   Angle(sample.RightThighDeg, -45f) &&
                   Angle(sample.LeftKneeDeg, 0f) &&
                   Angle(sample.RightKneeDeg, 0f);
        }

        private bool IsRecoverableSignalFailure(LowerBodyMeasurement sample)
        {
            if (sample.CalibrationVersion <= 0) return false;
            string reason = sample.FailureReason ?? "";
            if (reason.Contains("等待串口") || reason.Contains("站姿标定") ||
                reason.Contains("开始驱动"))
                return false;
            bool missingRequired = (sample.FreshMask & 15) != 15;
            bool signalReason = reason.Contains("超时") || reason.Contains("时间差") ||
                                reason.Contains("未持续更新") || reason.Contains("传感器");
            return signalReason || (!sample.IsValid && missingRequired && reason.Contains("数据"));
        }

        private void PauseForSignal(float now)
        {
            if (!SitToStandSettings.Finite(now))
            {
                ResetCycle();
                BlockReason = "训练时钟无效，请重新准备";
                return;
            }
            if (signalPauseExpired)
            {
                BlockReason = "信号中断较久，本次坐站已取消，请坐回后重新准备";
                return;
            }
            if (!signalPaused)
            {
                signalPaused = true;
                signalPauseStartedAt = now;
            }
            float pausedFor = Math.Max(0f, now - signalPauseStartedAt);
            if (pausedFor > settings.signalGraceSeconds)
            {
                ResetCycle();
                signalPaused = false;
                signalPauseExpired = true;
                BlockReason = "信号中断较久，本次坐站已取消，请坐回后重新准备";
                return;
            }
            BlockReason =
                $"信号短暂波动，取景高度和计时已冻结（{pausedFor:F1}/{settings.signalGraceSeconds:F0} 秒）";
        }

        private bool ResumeAfterSignal()
        {
            if (signalPauseExpired)
            {
                signalPauseExpired = false;
                return false;
            }
            if (!signalPaused) return false;
            signalPaused = false;
            return true;
        }

        private bool IsSeatedCandidate(LowerBodyMeasurement sample)
        {
            return InRange(sample.LeftThighDeg, settings.seatedThighMinDeg, settings.seatedThighMaxDeg) &&
                   InRange(sample.RightThighDeg, settings.seatedThighMinDeg, settings.seatedThighMaxDeg) &&
                   InRange(sample.LeftKneeDeg, settings.seatedKneeMinDeg, settings.seatedKneeMaxDeg) &&
                   InRange(sample.RightKneeDeg, settings.seatedKneeMinDeg, settings.seatedKneeMaxDeg);
        }

        private bool IsStandingCandidate(LowerBodyMeasurement sample)
        {
            return sample.LeftThighDeg <= settings.standingThighMaxDeg &&
                   sample.RightThighDeg <= settings.standingThighMaxDeg &&
                   sample.LeftKneeDeg <= settings.standingKneeMaxDeg &&
                   sample.RightKneeDeg <= settings.standingKneeMaxDeg &&
                   Math.Abs(sample.LeftThighDeg - sample.RightThighDeg) <= settings.maxStandingAsymmetryDeg &&
                   Math.Abs(sample.LeftKneeDeg - sample.RightKneeDeg) <= settings.maxStandingAsymmetryDeg;
        }

        private bool ReturnedToReference(LowerBodyMeasurement sample)
        {
            return HasSeatedReference && IsSeatedCandidate(sample) &&
                   Math.Abs(sample.LeftThighDeg - seatedLeftThigh) <= settings.returnedToleranceDeg &&
                   Math.Abs(sample.RightThighDeg - seatedRightThigh) <= settings.returnedToleranceDeg &&
                   Math.Abs(sample.LeftKneeDeg - seatedLeftKnee) <= settings.returnedToleranceDeg &&
                   Math.Abs(sample.RightKneeDeg - seatedRightKnee) <= settings.returnedToleranceDeg;
        }

        private void UpdateProgress(LowerBodyMeasurement sample)
        {
            if (!HasSeatedReference)
            {
                RaiseProgress01 = LeftProgress01 = RightProgress01 = 0f;
                return;
            }
            LeftProgress01 = LegProgress(
                sample.LeftThighDeg, sample.LeftKneeDeg,
                seatedLeftThigh, seatedLeftKnee);
            RightProgress01 = LegProgress(
                sample.RightThighDeg, sample.RightKneeDeg,
                seatedRightThigh, seatedRightKnee);
            // 取较慢一侧：任何单腿动作都不能把双腿坐站进度推到完成。
            RaiseProgress01 = Math.Min(LeftProgress01, RightProgress01);
        }

        private float LegProgress(float thigh, float knee, float referenceThigh, float referenceKnee)
        {
            float thighProgress = AxisProgress(
                thigh, referenceThigh, settings.standingThighMaxDeg);
            float kneeProgress = AxisProgress(
                knee, referenceKnee, settings.standingKneeMaxDeg);
            return Math.Min(thighProgress, kneeProgress);
        }

        private float AxisProgress(float value, float reference, float target)
        {
            float span = reference - target;
            if (!SitToStandSettings.Finite(span) || span < settings.minimumReferenceSpanDeg)
                return 0f;
            return Clamp01((reference - value) / span);
        }

        private bool IsCoordinated() =>
            Math.Abs(LeftProgress01 - RightProgress01) <= settings.maxLegProgressDifference;

        private bool StableFor(
            LowerBodyMeasurement sample,
            bool newSample,
            float duration,
            bool exposeHoldTime)
        {
            if (!hasStableWindow)
            {
                if (!newSample) return false;
                minimum = maximum = sample;
                stableWindowStartSampleTime = sample.SampleTimeSeconds;
                stableWindowSamples = 1;
                hasStableWindow = true;
            }
            else if (newSample)
            {
                IncludeInWindow(sample);
                stableWindowSamples++;
            }

            if (!WindowIsStable())
            {
                minimum = maximum = sample;
                stableWindowStartSampleTime = sample.SampleTimeSeconds;
                stableWindowSamples = newSample ? 1 : 0;
                hasStableWindow = newSample;
                if (exposeHoldTime) HoldSeconds = 0f;
                return false;
            }

            float elapsed = Math.Max(0f,
                sample.SampleTimeSeconds - stableWindowStartSampleTime);
            if (exposeHoldTime)
                HoldSeconds = Math.Min(duration, elapsed);
            return elapsed >= duration &&
                   stableWindowSamples >= settings.minimumConfirmedSamples;
        }

        private void IncludeInWindow(LowerBodyMeasurement sample)
        {
            minimum.LeftThighDeg = Math.Min(minimum.LeftThighDeg, sample.LeftThighDeg);
            minimum.LeftKneeDeg = Math.Min(minimum.LeftKneeDeg, sample.LeftKneeDeg);
            minimum.RightThighDeg = Math.Min(minimum.RightThighDeg, sample.RightThighDeg);
            minimum.RightKneeDeg = Math.Min(minimum.RightKneeDeg, sample.RightKneeDeg);
            maximum.LeftThighDeg = Math.Max(maximum.LeftThighDeg, sample.LeftThighDeg);
            maximum.LeftKneeDeg = Math.Max(maximum.LeftKneeDeg, sample.LeftKneeDeg);
            maximum.RightThighDeg = Math.Max(maximum.RightThighDeg, sample.RightThighDeg);
            maximum.RightKneeDeg = Math.Max(maximum.RightKneeDeg, sample.RightKneeDeg);
        }

        private bool WindowIsStable()
        {
            return maximum.LeftThighDeg - minimum.LeftThighDeg <= settings.stableRangeDeg &&
                   maximum.LeftKneeDeg - minimum.LeftKneeDeg <= settings.stableRangeDeg &&
                   maximum.RightThighDeg - minimum.RightThighDeg <= settings.stableRangeDeg &&
                   maximum.RightKneeDeg - minimum.RightKneeDeg <= settings.stableRangeDeg;
        }

        private void Enter(SitToStandStage stage)
        {
            Stage = stage;
            ResetStableWindow();
            HoldSeconds = stage == SitToStandStage.Returning
                ? settings.holdSeconds
                : 0f;
        }

        private void ResetCycle()
        {
            Stage = SitToStandStage.Preparing;
            RaiseProgress01 = LeftProgress01 = RightProgress01 = 0f;
            HoldSeconds = 0f;
            lastSampleTime = float.NegativeInfinity;
            ClearReferences();
            ResetStableWindow();
        }

        private void ClearReferences()
        {
            seatedLeftThigh = seatedLeftKnee =
                seatedRightThigh = seatedRightKnee = float.NaN;
        }

        private void ResetStableWindow()
        {
            hasStableWindow = false;
            stableWindowSamples = 0;
        }

        private static bool Angle(float value, float minimum) =>
            SitToStandSettings.Finite(value) && value >= minimum && value <= 180f;
        private static bool InRange(float value, float minimum, float maximum) =>
            value >= minimum && value <= maximum;
        private static float Clamp01(float value) =>
            value <= 0f ? 0f : value >= 1f ? 1f : value;
    }
}
