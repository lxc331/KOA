using System;

namespace RehabPhotoGame
{
    /// <summary>
    /// 无场景依赖的动作状态机。保持计时由四传感器采样进度驱动，不能用渲染帧
    /// 重复累计一帧旧姿态。断流、重标定、切换训练腿均丢弃尚未完成的动作。
    /// </summary>
    public sealed class SeatedKneeExtensionEvaluator
    {
        private readonly SeatedKneeExtensionSettings settings;
        private TrainingLeg leg;
        private int calibrationVersion = -1;
        private float lastSampleTime = float.NegativeInfinity;
        private float windowStart;
        private bool hasWindow;
        private LowerBodyMeasurement minimum, maximum;

        public KneeExtensionStage Stage { get; private set; }
        public int CompletedRepetitions { get; private set; }
        public float HoldSeconds { get; private set; }
        public string BlockReason { get; private set; } = "等待有效数据";
        public TrainingLeg Leg => leg;

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
            lastSampleTime = float.NegativeInfinity;
            if (clearCount) CompletedRepetitions = 0;
        }

        public void Update(LowerBodyMeasurement sample, float now)
        {
            string failure = Validate(sample, now);
            if (failure != "")
            {
                Reset(leg, false);
                BlockReason = failure;
                return;
            }
            BlockReason = "";
            if (calibrationVersion != sample.CalibrationVersion)
            {
                Reset(leg, false);
                calibrationVersion = sample.CalibrationVersion;
            }

            bool newSample = sample.SampleTimeSeconds > lastSampleTime;
            if (sample.SampleTimeSeconds < lastSampleTime ||
                (newSample && SeatedKneeExtensionSettings.Finite(lastSampleTime) &&
                 sample.SampleTimeSeconds - lastSampleTime > settings.sensorTimeoutSeconds))
                Reset(leg, false);
            if (newSample) lastSampleTime = sample.SampleTimeSeconds;

            // 即使最慢设备尚未更新，其余设备已经离开范围也必须立即撤销保持。
            if (!BothThighsSeated(sample))
            {
                Reset(leg, false);
                BlockReason = "请保持坐位，大腿接近水平";
                return;
            }

            switch (Stage)
            {
                case KneeExtensionStage.Preparing:
                    if (Ready(sample) && StableFor(sample, newSample, settings.readySeconds, now))
                        Enter(KneeExtensionStage.Extending);
                    else if (!Ready(sample)) hasWindow = false;
                    break;
                case KneeExtensionStage.Extending:
                    if (InTarget(sample, settings.targetKneeMaxDeg))
                    {
                        Enter(KneeExtensionStage.Holding);
                        StableFor(sample, newSample, settings.holdSeconds, now);
                    }
                    break;
                case KneeExtensionStage.Holding:
                    if (!InTarget(sample, settings.targetKneeMaxDeg + settings.targetExitMarginDeg))
                        Enter(KneeExtensionStage.Extending);
                    else if (StableFor(sample, newSample, settings.holdSeconds, now))
                        Enter(KneeExtensionStage.Returning);
                    break;
                case KneeExtensionStage.Returning:
                    if (Ready(sample) && StableFor(sample, newSample, settings.readySeconds, now))
                    {
                        CompletedRepetitions++;
                        Enter(KneeExtensionStage.Preparing);
                    }
                    else if (!Ready(sample)) hasWindow = false;
                    break;
            }
        }

        private string Validate(LowerBodyMeasurement s, float now)
        {
            if (settings == null || !settings.IsValid) return "技术验证参数无效，请检查 Inspector";
            if (!s.IsValid) return string.IsNullOrEmpty(s.FailureReason) ? "等待有效数据" : s.FailureReason;
            if (s.FreshMask != 15) return "06～09 必须全部在线";
            if (!SeatedKneeExtensionSettings.Finite(now) ||
                !SeatedKneeExtensionSettings.Finite(s.SampleTimeSeconds) ||
                now < s.SampleTimeSeconds || now - s.SampleTimeSeconds > settings.sensorTimeoutSeconds)
                return "数据已超时；请回到准备姿势";
            if (!Angle(s.LeftKneeDeg, 0f) || !Angle(s.RightKneeDeg, 0f) ||
                !Angle(s.LeftThighDeg, -180f) || !Angle(s.RightThighDeg, -180f))
                return "角度无效";
            return "";
        }

        private static bool Angle(float value, float min) =>
            SeatedKneeExtensionSettings.Finite(value) && value >= min && value <= 180f;

        private bool BothThighsSeated(LowerBodyMeasurement s) =>
            InRange(s.LeftThighDeg, settings.seatedThighMinDeg, settings.seatedThighMaxDeg) &&
            InRange(s.RightThighDeg, settings.seatedThighMinDeg, settings.seatedThighMaxDeg);

        private bool Ready(LowerBodyMeasurement s) =>
            Selected(InRange(s.LeftKneeDeg, settings.readyKneeMinDeg, settings.readyKneeMaxDeg),
                InRange(s.RightKneeDeg, settings.readyKneeMinDeg, settings.readyKneeMaxDeg));

        private bool InTarget(LowerBodyMeasurement s, float limit) =>
            Selected(s.LeftKneeDeg <= limit, s.RightKneeDeg <= limit);

        private bool Selected(bool left, bool right) =>
            leg == TrainingLeg.Left ? left : leg == TrainingLeg.Right ? right : left && right;

        private static bool InRange(float value, float min, float max) => value >= min && value <= max;

        private void Enter(KneeExtensionStage stage)
        {
            Stage = stage;
            hasWindow = false;
            HoldSeconds = stage == KneeExtensionStage.Returning ? settings.holdSeconds : 0f;
        }

        private bool StableFor(LowerBodyMeasurement s, bool newSample, float duration, float now)
        {
            if (!hasWindow)
            {
                if (!newSample) return false;
                minimum = maximum = s;
                // 不能把进入目标之前较老的设备时间当成保持起点；否则帧间延迟
                // 缩短时可能凭空多累计一段保持时间。
                windowStart = now;
                hasWindow = true;
            }
            minimum.LeftKneeDeg = Math.Min(minimum.LeftKneeDeg, s.LeftKneeDeg);
            minimum.RightKneeDeg = Math.Min(minimum.RightKneeDeg, s.RightKneeDeg);
            minimum.LeftThighDeg = Math.Min(minimum.LeftThighDeg, s.LeftThighDeg);
            minimum.RightThighDeg = Math.Min(minimum.RightThighDeg, s.RightThighDeg);
            maximum.LeftKneeDeg = Math.Max(maximum.LeftKneeDeg, s.LeftKneeDeg);
            maximum.RightKneeDeg = Math.Max(maximum.RightKneeDeg, s.RightKneeDeg);
            maximum.LeftThighDeg = Math.Max(maximum.LeftThighDeg, s.LeftThighDeg);
            maximum.RightThighDeg = Math.Max(maximum.RightThighDeg, s.RightThighDeg);
            bool stable = Selected(
                maximum.LeftKneeDeg - minimum.LeftKneeDeg <= settings.stableRangeDeg,
                maximum.RightKneeDeg - minimum.RightKneeDeg <= settings.stableRangeDeg) &&
                maximum.LeftThighDeg - minimum.LeftThighDeg <= settings.stableRangeDeg &&
                maximum.RightThighDeg - minimum.RightThighDeg <= settings.stableRangeDeg;
            if (!stable)
            {
                minimum = maximum = s;
                windowStart = now;
                HoldSeconds = 0f;
                // 等最慢设备也更新后重新建立时间窗口。
                hasWindow = newSample;
                return false;
            }
            if (!newSample) return false;
            float elapsed = Math.Max(0f, s.SampleTimeSeconds - windowStart);
            if (Stage == KneeExtensionStage.Holding) HoldSeconds = Math.Min(duration, elapsed);
            return elapsed >= duration;
        }
    }
}
