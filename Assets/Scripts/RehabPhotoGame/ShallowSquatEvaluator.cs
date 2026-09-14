using System;

namespace RehabPhotoGame
{
    /// <summary>
    /// 游戏联调浅蹲状态机。先确认站姿，以双膝相对屈曲量推进；
    /// 大腿为辅助提示，过深时保留参考等待回浅。不能据此认定真人是否坐下。
    /// </summary>
    public sealed class ShallowSquatEvaluator
    {
        private readonly ShallowSquatSettings settings;
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

        private float standingLeftThigh = float.NaN;
        private float standingLeftKnee = float.NaN;
        private float standingRightThigh = float.NaN;
        private float standingRightKnee = float.NaN;

        public ShallowSquatStage Stage { get; private set; } = ShallowSquatStage.Preparing;
        public int CompletedRepetitions { get; private set; }
        public int CaptureSequence { get; private set; }
        public float DropProgress01 { get; private set; }
        public float LeftProgress01 { get; private set; }
        public float RightProgress01 { get; private set; }
        public float HoldSeconds { get; private set; }
        public float LeftKneeFlexionDeg { get; private set; }
        public float RightKneeFlexionDeg { get; private set; }
        public bool CoordinationWarning { get; private set; }
        public string BlockReason { get; private set; } = "等待有效数据";
        public bool SignalPaused => signalPaused || signalPauseExpired;
        public bool HasStandingReference =>
            ShallowSquatSettings.Finite(standingLeftThigh) &&
            ShallowSquatSettings.Finite(standingLeftKnee) &&
            ShallowSquatSettings.Finite(standingRightThigh) &&
            ShallowSquatSettings.Finite(standingRightKnee);

        public ShallowSquatEvaluator(ShallowSquatSettings settings)
        {
            this.settings = settings;
        }

        public void Reset(bool clearCount)
        {
            ResetCycle();
            calibrationVersion = -1;
            signalPaused = false;
            signalPauseExpired = false;
            BlockReason = "等待站姿准备";
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
                BlockReason = "浅蹲参数无效，请检查配置";
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
                 ShallowSquatSettings.Finite(lastSampleTime) &&
                 sample.SampleTimeSeconds - lastSampleTime > settings.sensorTimeoutSeconds))
            {
                ResetCycle();
                newSample = true;
            }

            if (resumedFromSignalPause && newSample && hasStableWindow &&
                ShallowSquatSettings.Finite(lastSampleTime))
            {
                stableWindowStartSampleTime +=
                    Math.Max(0f, sample.SampleTimeSeconds - lastSampleTime);
            }
            if (newSample)
                lastSampleTime = sample.SampleTimeSeconds;

            switch (Stage)
            {
                case ShallowSquatStage.Preparing:
                    UpdatePreparing(sample, newSample);
                    break;
                case ShallowSquatStage.Lowering:
                    UpdateLowering(sample, newSample);
                    break;
                case ShallowSquatStage.Holding:
                    UpdateHolding(sample, newSample);
                    break;
                case ShallowSquatStage.Returning:
                    UpdateReturning(sample, newSample);
                    break;
                case ShallowSquatStage.TooDeep:
                    UpdateTooDeep(sample, newSample);
                    break;
            }
        }

        private void UpdatePreparing(LowerBodyMeasurement sample, bool newSample)
        {
            DropProgress01 = LeftProgress01 = RightProgress01 = 0f;
            HoldSeconds = 0f;
            if (!IsStandingCandidate(sample))
            {
                ResetStableWindow();
                BlockReason = "请先站直并站稳，再开始浅蹲";
                return;
            }

            BlockReason = "正在确认站姿准备";
            if (!StableFor(sample, newSample, settings.readySeconds, false))
                return;

            standingLeftThigh = sample.LeftThighDeg;
            standingLeftKnee = sample.LeftKneeDeg;
            standingRightThigh = sample.RightThighDeg;
            standingRightKnee = sample.RightKneeDeg;
            Enter(ShallowSquatStage.Lowering);
            BlockReason = "准备完成，请双腿同步缓慢浅蹲";
        }

        private void UpdateLowering(LowerBodyMeasurement sample, bool newSample)
        {
            UpdateProgress(sample);
            if (IsTooDeep(sample))
            {
                EnterTooDeep();
                return;
            }
            if (IsClearlySingleLeg())
            {
                ResetStableWindow();
                BlockReason = "请让左右腿同步屈曲，避免单腿下蹲";
                return;
            }

            BlockReason = LoweringInstruction();
            if (DropProgress01 < 0.999f || !IsShallowCandidate(sample))
                return;

            Enter(ShallowSquatStage.Holding);
            StableFor(sample, newSample, settings.holdSeconds, true);
            BlockReason = HoldingInstruction(sample);
        }

        private void UpdateHolding(LowerBodyMeasurement sample, bool newSample)
        {
            UpdateProgress(sample);
            if (IsTooDeep(sample))
            {
                EnterTooDeep();
                return;
            }
            if (IsClearlySingleLeg() ||
                DropProgress01 < 0.999f || !IsShallowCandidate(sample))
            {
                Enter(ShallowSquatStage.Lowering);
                BlockReason = IsClearlySingleLeg()
                    ? "请让左右腿同步屈曲，避免单腿下蹲"
                    : LoweringInstruction();
                return;
            }

            BlockReason = HoldingInstruction(sample);
            if (!StableFor(sample, newSample, settings.holdSeconds, true))
                return;

            CaptureSequence++;
            Enter(ShallowSquatStage.Returning);
            DropProgress01 = LeftProgress01 = RightProgress01 = 1f;
            BlockReason = "拍摄完成，请双腿同步缓慢站直";
        }

        private void UpdateTooDeep(LowerBodyMeasurement sample, bool newSample)
        {
            UpdateProgress(sample);
            HoldSeconds = 0f;
            // 过深期间不清除参考，也不累计保持。回浅后必须重新保持完整时长。
            if (!IsWithinDepthLimit(sample, settings.tooDeepRecoveryMarginDeg))
            {
                BlockReason = "已超出浅蹲范围，请回浅一点；回到范围后重新保持";
                return;
            }

            Enter(ShallowSquatStage.Lowering);
            UpdateLowering(sample, newSample);
        }

        private void UpdateReturning(LowerBodyMeasurement sample, bool newSample)
        {
            UpdateProgress(sample);
            HoldSeconds = settings.holdSeconds;
            if (!ReturnedToReference(sample))
            {
                ResetStableWindow();
                BlockReason = "请双腿同步缓慢站回准备姿势";
                return;
            }

            BlockReason = "正在确认已经站稳";
            if (!StableFor(sample, newSample, settings.returnSeconds, false))
                return;

            CompletedRepetitions++;
            Enter(ShallowSquatStage.Preparing);
            ClearReferences();
            DropProgress01 = LeftProgress01 = RightProgress01 = 0f;
            BlockReason = "已完成一次，请站稳准备下一次";
        }

        private string Validate(LowerBodyMeasurement sample, float now)
        {
            if (!sample.IsValid)
                return string.IsNullOrEmpty(sample.FailureReason)
                    ? "等待 06～09 有效数据"
                    : sample.FailureReason;
            if ((sample.FreshMask & 15) != 15)
                return "浅蹲需要的四个传感器未持续更新";
            if (!ShallowSquatSettings.Finite(now) ||
                !ShallowSquatSettings.Finite(sample.SampleTimeSeconds) ||
                now < sample.SampleTimeSeconds ||
                now - sample.SampleTimeSeconds > settings.sensorTimeoutSeconds)
                return "浅蹲传感器数据已超时";
            return AnglesValid(sample) ? "" : "浅蹲角度数据无效";
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
            if (!ShallowSquatSettings.Finite(now))
            {
                ResetCycle();
                BlockReason = "训练时钟无效，请重新准备";
                return;
            }
            if (signalPauseExpired)
            {
                BlockReason = "信号中断较久，本次浅蹲已取消，请站直后重新准备";
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
                BlockReason = "信号中断较久，本次浅蹲已取消，请站直后重新准备";
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

        private bool IsStandingCandidate(LowerBodyMeasurement sample)
        {
            return InRange(sample.LeftThighDeg, settings.standingThighMinDeg, settings.standingThighMaxDeg) &&
                   InRange(sample.RightThighDeg, settings.standingThighMinDeg, settings.standingThighMaxDeg) &&
                   InRange(sample.LeftKneeDeg, settings.standingKneeMinDeg, settings.standingKneeMaxDeg) &&
                   InRange(sample.RightKneeDeg, settings.standingKneeMinDeg, settings.standingKneeMaxDeg) &&
                   AnglesAreSymmetric(sample);
        }

        private bool IsShallowCandidate(LowerBodyMeasurement sample)
        {
            return HasStandingReference && !IsTooDeep(sample) &&
                   sample.LeftKneeDeg - standingLeftKnee >= settings.minimumKneeFlexionDeg &&
                   sample.RightKneeDeg - standingRightKnee >= settings.minimumKneeFlexionDeg;
        }

        private bool IsTooDeep(LowerBodyMeasurement sample)
        {
            return !IsWithinDepthLimit(sample, 0f);
        }

        private bool IsWithinDepthLimit(LowerBodyMeasurement sample, float margin)
        {
            return sample.LeftThighDeg <= settings.maximumShallowThighDeg - margin &&
                   sample.RightThighDeg <= settings.maximumShallowThighDeg - margin &&
                   sample.LeftKneeDeg <= settings.maximumShallowKneeDeg - margin &&
                   sample.RightKneeDeg <= settings.maximumShallowKneeDeg - margin;
        }

        private bool AnglesAreSymmetric(LowerBodyMeasurement sample)
        {
            return Math.Abs(sample.LeftThighDeg - sample.RightThighDeg) <= settings.maxAngleAsymmetryDeg &&
                   Math.Abs(sample.LeftKneeDeg - sample.RightKneeDeg) <= settings.maxAngleAsymmetryDeg;
        }

        private bool ReturnedToReference(LowerBodyMeasurement sample)
        {
            return HasStandingReference && IsStandingCandidate(sample) &&
                   Math.Abs(sample.LeftThighDeg - standingLeftThigh) <= settings.returnedToleranceDeg &&
                   Math.Abs(sample.RightThighDeg - standingRightThigh) <= settings.returnedToleranceDeg &&
                   Math.Abs(sample.LeftKneeDeg - standingLeftKnee) <= settings.returnedToleranceDeg &&
                   Math.Abs(sample.RightKneeDeg - standingRightKnee) <= settings.returnedToleranceDeg;
        }

        private void UpdateProgress(LowerBodyMeasurement sample)
        {
            if (!HasStandingReference)
            {
                DropProgress01 = LeftProgress01 = RightProgress01 = 0f;
                return;
            }
            LeftKneeFlexionDeg = Math.Max(0f, sample.LeftKneeDeg - standingLeftKnee);
            RightKneeFlexionDeg = Math.Max(0f, sample.RightKneeDeg - standingRightKnee);
            LeftProgress01 = Clamp01(LeftKneeFlexionDeg / settings.minimumKneeFlexionDeg);
            RightProgress01 = Clamp01(RightKneeFlexionDeg / settings.minimumKneeFlexionDeg);
            float difference = Math.Abs(LeftKneeFlexionDeg - RightKneeFlexionDeg);
            CoordinationWarning = difference > settings.maxAngleAsymmetryDeg ||
                difference / settings.minimumKneeFlexionDeg > settings.maxLegProgressDifference;
            // 取较慢一侧，确保单腿动作不能推动双腿浅蹲进度。
            DropProgress01 = Math.Min(LeftProgress01, RightProgress01);
        }

        private bool IsClearlySingleLeg()
        {
            float larger = Math.Max(LeftKneeFlexionDeg, RightKneeFlexionDeg);
            float smaller = Math.Min(LeftKneeFlexionDeg, RightKneeFlexionDeg);
            return larger >= settings.minimumKneeFlexionDeg &&
                   (smaller < settings.singleLegMinimumFlexionDeg ||
                    smaller / larger < settings.singleLegMinimumRatio);
        }

        private string LoweringInstruction()
        {
            if (LeftProgress01 < 0.999f && RightProgress01 >= 0.999f)
                return "左膝尚未到目标，请双腿一起缓慢调整";
            if (RightProgress01 < 0.999f && LeftProgress01 >= 0.999f)
                return "右膝尚未到目标，请双腿一起缓慢调整";
            return "双膝缓慢屈曲，让低处目标进入取景框";
        }

        private string HoldingInstruction(LowerBodyMeasurement sample)
        {
            if (CoordinationWarning)
                return "双膝已到目标，请保持；左右幅度有差异，尽量同步";
            if (sample.LeftThighDeg - standingLeftThigh < settings.minimumThighFlexionDeg ||
                sample.RightThighDeg - standingRightThigh < settings.minimumThighFlexionDeg)
                return "双膝已到目标，请保持；大腿变化较小，请留意姿势";
            return "已到浅蹲目标，请保持等待快门";
        }

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
            bool kneesStable =
                maximum.LeftKneeDeg - minimum.LeftKneeDeg <= settings.stableRangeDeg &&
                maximum.RightKneeDeg - minimum.RightKneeDeg <= settings.stableRangeDeg;
            return kneesStable && (Stage == ShallowSquatStage.Holding ||
                (maximum.LeftThighDeg - minimum.LeftThighDeg <= settings.stableRangeDeg &&
                 maximum.RightThighDeg - minimum.RightThighDeg <= settings.stableRangeDeg));
        }

        private void EnterTooDeep()
        {
            Enter(ShallowSquatStage.TooDeep);
            BlockReason = "已超出浅蹲范围，请回浅一点；回到范围后重新保持";
        }

        private void Enter(ShallowSquatStage stage)
        {
            Stage = stage;
            ResetStableWindow();
            HoldSeconds = stage == ShallowSquatStage.Returning
                ? settings.holdSeconds
                : 0f;
        }

        private void ResetCycle()
        {
            Stage = ShallowSquatStage.Preparing;
            DropProgress01 = LeftProgress01 = RightProgress01 = 0f;
            HoldSeconds = 0f;
            LeftKneeFlexionDeg = RightKneeFlexionDeg = 0f;
            CoordinationWarning = false;
            lastSampleTime = float.NegativeInfinity;
            ClearReferences();
            ResetStableWindow();
        }

        private void ClearReferences()
        {
            standingLeftThigh = standingLeftKnee =
                standingRightThigh = standingRightKnee = float.NaN;
            LeftKneeFlexionDeg = RightKneeFlexionDeg = 0f;
            CoordinationWarning = false;
        }

        private void ResetStableWindow()
        {
            hasStableWindow = false;
            stableWindowSamples = 0;
        }

        private static bool Angle(float value, float minimum) =>
            ShallowSquatSettings.Finite(value) && value >= minimum && value <= 180f;
        private static bool InRange(float value, float minimum, float maximum) =>
            value >= minimum && value <= maximum;
        private static float Clamp01(float value) =>
            value <= 0f ? 0f : value >= 1f ? 1f : value;
    }
}
