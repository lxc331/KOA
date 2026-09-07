using System;

namespace RehabPhotoGame
{
    public sealed class SeatedKneeExtensionEvaluator
    {
        private readonly SeatedKneeExtensionSettings settings;
        private TrainingLeg leg;
        private int calibrationVersion = -1;
        private float lastSampleTime = float.NegativeInfinity;
        private float windowStart;
        private bool hasWindow;
        private int windowSampleCount;
        private LowerBodyMeasurement minimum, maximum;
        private float leftReadyReference = float.NaN;
        private float rightReadyReference = float.NaN;
        private bool signalPaused;
        private bool signalPauseExpired;
        private float signalPauseStartedAt;

        public KneeExtensionStage Stage { get; private set; }
        public int CompletedRepetitions { get; private set; }
        public float HoldSeconds { get; private set; }
        public string BlockReason { get; private set; } = "等待有效数据";
        public TrainingLeg Leg => leg;
        public bool HasReadyReference => leg == TrainingLeg.Left
            ? SeatedKneeExtensionSettings.Finite(leftReadyReference)
            : leg == TrainingLeg.Right
                ? SeatedKneeExtensionSettings.Finite(rightReadyReference)
                : SeatedKneeExtensionSettings.Finite(leftReadyReference) && SeatedKneeExtensionSettings.Finite(rightReadyReference);
        public float ReadyReferenceKneeDeg => SelectedReference();
        public float TargetKneeDeg => Math.Max(0f, SelectedReference() - settings.minimumExtensionDeg);
        public bool SignalPaused => signalPaused || signalPauseExpired;

        public SeatedKneeExtensionEvaluator(SeatedKneeExtensionSettings settings, TrainingLeg leg)
        {
            this.settings = settings;
            this.leg = leg;
        }

        public void Reset(TrainingLeg selectedLeg, bool clearCount)
        {
            leg = selectedLeg;
            Stage = KneeExtensionStage.Preparing;
            HoldSeconds = 0f;
            hasWindow = false;
            windowSampleCount = 0;
            lastSampleTime = float.NegativeInfinity;
            leftReadyReference = rightReadyReference = float.NaN;
            signalPaused = false;
            signalPauseExpired = false;
            if (clearCount) CompletedRepetitions = 0;
        }

        public void Update(LowerBodyMeasurement sample, float now)
        {
            if (settings == null || !settings.IsValid)
            {
                Reset(leg, false);
                BlockReason = "训练参数无效，请检查 Inspector";
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
                Reset(leg, false);
                BlockReason = failure;
                return;
            }
            bool resumedFromSignalPause = ResumeAfterSignal(now);
            BlockReason = "";
            if (calibrationVersion != sample.CalibrationVersion)
            {
                Reset(leg, false);
                calibrationVersion = sample.CalibrationVersion;
            }
            bool newSample = sample.SampleTimeSeconds > lastSampleTime;
            if (sample.SampleTimeSeconds < lastSampleTime ||
                (!resumedFromSignalPause && newSample && SeatedKneeExtensionSettings.Finite(lastSampleTime) &&
                 sample.SampleTimeSeconds - lastSampleTime > settings.sensorTimeoutSeconds))
            {
                Reset(leg, false);
                newSample = true;
            }
            if (newSample) lastSampleTime = sample.SampleTimeSeconds;
            if (!SelectedThighsSeated(sample))
            {
                Reset(leg, false);
                BlockReason = "请坐稳，大腿保持接近水平";
                return;
            }
            switch (Stage)
            {
                case KneeExtensionStage.Preparing:
                    if (ReadyCandidate(sample) && StableFor(sample, newSample, settings.readySeconds, now))
                    {
                        CaptureReadyReference(sample);
                        Enter(KneeExtensionStage.Extending);
                    }
                    else if (!ReadyCandidate(sample))
                    {
                        hasWindow = false;
                        windowSampleCount = 0;
                    }
                    break;
                case KneeExtensionStage.Extending:
                    if (InTarget(sample, 0f))
                    {
                        Enter(KneeExtensionStage.Holding);
                        StableFor(sample, newSample, settings.holdSeconds, now);
                    }
                    break;
                case KneeExtensionStage.Holding:
                    if (!InTarget(sample, settings.targetExitMarginDeg)) Enter(KneeExtensionStage.Extending);
                    else if (StableFor(sample, newSample, settings.holdSeconds, now)) Enter(KneeExtensionStage.Returning);
                    break;
                case KneeExtensionStage.Returning:
                    if (ReturnedToReady(sample) && StableFor(sample, newSample, settings.readySeconds, now))
                    {
                        CompletedRepetitions++;
                        Enter(KneeExtensionStage.Preparing);
                    }
                    else if (!ReturnedToReady(sample))
                    {
                        hasWindow = false;
                        windowSampleCount = 0;
                    }
                    break;
            }
        }

        private string Validate(LowerBodyMeasurement s, float now)
        {
            if (!s.IsValid) return string.IsNullOrEmpty(s.FailureReason) ? "等待有效数据" : s.FailureReason;
            int requiredMask = leg == TrainingLeg.Left ? 3 : leg == TrainingLeg.Right ? 12 : 15;
            if ((s.FreshMask & requiredMask) != requiredMask) return "当前训练腿传感器未持续更新";
            if (!SeatedKneeExtensionSettings.Finite(now) || !SeatedKneeExtensionSettings.Finite(s.SampleTimeSeconds) ||
                now < s.SampleTimeSeconds || now - s.SampleTimeSeconds > settings.sensorTimeoutSeconds)
                return "当前训练腿数据已超时";
            bool leftValid = leg == TrainingLeg.Right || (Angle(s.LeftKneeDeg, 0f) && Angle(s.LeftThighDeg, -180f));
            bool rightValid = leg == TrainingLeg.Left || (Angle(s.RightKneeDeg, 0f) && Angle(s.RightThighDeg, -180f));
            return leftValid && rightValid ? "" : "当前训练腿角度无效";
        }

        private bool IsRecoverableSignalFailure(LowerBodyMeasurement s)
        {
            if (s.CalibrationVersion <= 0) return false;
            string reason = s.FailureReason ?? "";
            if (reason.Contains("等待串口") || reason.Contains("站姿标定") || reason.Contains("开始驱动")) return false;
            int requiredMask = leg == TrainingLeg.Left ? 3 : leg == TrainingLeg.Right ? 12 : 15;
            bool missingRequired = (s.FreshMask & requiredMask) != requiredMask;
            bool signalReason = reason.Contains("超时") || reason.Contains("时间差") || reason.Contains("未持续更新");
            return signalReason || (!s.IsValid && missingRequired && reason.Contains("数据"));
        }

        private void PauseForSignal(float now)
        {
            if (!SeatedKneeExtensionSettings.Finite(now))
            {
                Reset(leg, false);
                BlockReason = "训练时钟无效，请重新准备";
                return;
            }
            if (signalPauseExpired)
            {
                BlockReason = "数据中断较久，本次动作已取消。请先放松，恢复后重新准备";
                return;
            }
            if (!signalPaused)
            {
                signalPaused = true;
                signalPauseStartedAt = now;
                if (Stage == KneeExtensionStage.Preparing)
                {
                    hasWindow = false;
                    windowSampleCount = 0;
                }
            }
            float pausedFor = Math.Max(0f, now - signalPauseStartedAt);
            if (pausedFor > settings.signalGraceSeconds)
            {
                Stage = KneeExtensionStage.Preparing;
                HoldSeconds = 0f;
                hasWindow = false;
                windowSampleCount = 0;
                lastSampleTime = float.NegativeInfinity;
                leftReadyReference = rightReadyReference = float.NaN;
                signalPaused = false;
                signalPauseExpired = true;
                BlockReason = "数据中断较久，本次动作已取消。请先放松，恢复后重新准备";
                return;
            }
            BlockReason = $"信号短暂波动，训练进度已暂停。可以先放松，不需要保持当前动作（{pausedFor:F1}/{settings.signalGraceSeconds:F0} 秒）";
        }

        private bool ResumeAfterSignal(float now)
        {
            if (signalPauseExpired) { signalPauseExpired = false; return false; }
            if (!signalPaused) return false;
            if (hasWindow) windowStart += Math.Max(0f, now - signalPauseStartedAt);
            signalPaused = false;
            return true;
        }

        private static bool Angle(float value, float min) => SeatedKneeExtensionSettings.Finite(value) && value >= min && value <= 180f;
        private bool SelectedThighsSeated(LowerBodyMeasurement s) => Selected(
            InRange(s.LeftThighDeg, settings.seatedThighMinDeg, settings.seatedThighMaxDeg),
            InRange(s.RightThighDeg, settings.seatedThighMinDeg, settings.seatedThighMaxDeg));
        private bool ReadyCandidate(LowerBodyMeasurement s) => Selected(
            InRange(s.LeftKneeDeg, settings.readyKneeMinDeg, settings.readyKneeMaxDeg),
            InRange(s.RightKneeDeg, settings.readyKneeMinDeg, settings.readyKneeMaxDeg));
        private void CaptureReadyReference(LowerBodyMeasurement s)
        {
            if (leg != TrainingLeg.Right) leftReadyReference = s.LeftKneeDeg;
            if (leg != TrainingLeg.Left) rightReadyReference = s.RightKneeDeg;
        }
        private bool InTarget(LowerBodyMeasurement s, float margin) => Selected(
            s.LeftKneeDeg <= leftReadyReference - settings.minimumExtensionDeg + margin,
            s.RightKneeDeg <= rightReadyReference - settings.minimumExtensionDeg + margin);
        private bool ReturnedToReady(LowerBodyMeasurement s) => Selected(
            Math.Abs(s.LeftKneeDeg - leftReadyReference) <= settings.returnToleranceDeg,
            Math.Abs(s.RightKneeDeg - rightReadyReference) <= settings.returnToleranceDeg);
        private bool Selected(bool left, bool right) => leg == TrainingLeg.Left ? left : leg == TrainingLeg.Right ? right : left && right;
        private float SelectedReference()
        {
            if (leg == TrainingLeg.Left) return leftReadyReference;
            if (leg == TrainingLeg.Right) return rightReadyReference;
            return SeatedKneeExtensionSettings.Finite(leftReadyReference) && SeatedKneeExtensionSettings.Finite(rightReadyReference)
                ? Math.Min(leftReadyReference, rightReadyReference) : float.NaN;
        }
        private static bool InRange(float value, float min, float max) => value >= min && value <= max;
        private void Enter(KneeExtensionStage stage)
        {
            Stage = stage;
            hasWindow = false;
            windowSampleCount = 0;
            HoldSeconds = stage == KneeExtensionStage.Returning ? settings.holdSeconds : 0f;
        }
        private bool StableFor(LowerBodyMeasurement s, bool newSample, float duration, float now)
        {
            if (!hasWindow)
            {
                if (!newSample) return false;
                minimum = maximum = s;
                windowStart = now;
                windowSampleCount = 1;
                hasWindow = true;
            }
            else if (newSample)
            {
                minimum.LeftKneeDeg = Math.Min(minimum.LeftKneeDeg, s.LeftKneeDeg);
                minimum.RightKneeDeg = Math.Min(minimum.RightKneeDeg, s.RightKneeDeg);
                minimum.LeftThighDeg = Math.Min(minimum.LeftThighDeg, s.LeftThighDeg);
                minimum.RightThighDeg = Math.Min(minimum.RightThighDeg, s.RightThighDeg);
                maximum.LeftKneeDeg = Math.Max(maximum.LeftKneeDeg, s.LeftKneeDeg);
                maximum.RightKneeDeg = Math.Max(maximum.RightKneeDeg, s.RightKneeDeg);
                maximum.LeftThighDeg = Math.Max(maximum.LeftThighDeg, s.LeftThighDeg);
                maximum.RightThighDeg = Math.Max(maximum.RightThighDeg, s.RightThighDeg);
                windowSampleCount++;
            }
            bool stable = Selected(
                maximum.LeftKneeDeg - minimum.LeftKneeDeg <= settings.stableRangeDeg && maximum.LeftThighDeg - minimum.LeftThighDeg <= settings.stableRangeDeg,
                maximum.RightKneeDeg - minimum.RightKneeDeg <= settings.stableRangeDeg && maximum.RightThighDeg - minimum.RightThighDeg <= settings.stableRangeDeg);
            if (!stable)
            {
                minimum = maximum = s;
                windowStart = now;
                windowSampleCount = newSample ? 1 : 0;
                hasWindow = newSample;
                HoldSeconds = 0f;
                return false;
            }
            float elapsed = Math.Max(0f, now - windowStart);
            if (Stage == KneeExtensionStage.Holding) HoldSeconds = Math.Min(duration, elapsed);
            return elapsed >= duration && windowSampleCount >= settings.minimumConfirmedSamples;
        }
    }
}
