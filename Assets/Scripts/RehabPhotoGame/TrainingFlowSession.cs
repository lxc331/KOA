using System;

namespace RehabPhotoGame
{
    public enum TrainingFlowState
    {
        Setup,
        Training,
        ReturnPreparation,
        SignalRecovery,
        Paused,
        SensorError,
        Review,
        SafetyStop
    }

    [Serializable]
    public sealed class TrainingFlowSettings
    {
        public string config_version = "stage5-game-flow-v2";
        public float recovery_confirm_seconds = 0.7f;
        public float sensor_error_timeout_seconds = 15f;
        public bool IsValid => !string.IsNullOrWhiteSpace(config_version) &&
            Finite(recovery_confirm_seconds) && recovery_confirm_seconds > 0f &&
            Finite(sensor_error_timeout_seconds) &&
            sensor_error_timeout_seconds > recovery_confirm_seconds;
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        public TrainingFlowSettings Copy() => (TrainingFlowSettings)MemberwiseClone();
    }

    [Serializable]
    public sealed class TrainingFlowEvent
    {
        public string event_id, session_id, timestamp_utc, timezone_offset;
        public string event_type, reason_code, state, action_type, review_status;
        public string rule_version = TrainingFlowSession.RuleVersion;
        public string config_version;
        public string app_version;
        public TrainingFlowSettings config_snapshot;
        public int captured_photos;
        public string stage;
        public S2CombinationEvent combination;
    }

    [Serializable]
    public sealed class TrainingSessionResult
    {
        public string session_id, started_at_utc, timestamp_utc, timezone_offset;
        public string stage = "S1", scope = "single_action_photo_session";
        public string action_type, app_version, termination_reason, review_status;
        public string rule_version = TrainingFlowSession.RuleVersion;
        public string config_version;
        public TrainingFlowSettings config_snapshot;
        public int planned_photos, captured_photos, pause_count, sensor_error_count;
        public double duration_seconds, active_seconds, paused_seconds, sensor_error_seconds;
        public float photo_completion_rate;
        public bool preflight_confirmed, safety_stop;
        public string quality_assessment = "not_evaluated";
        public string clinical_pass = "not_evaluated";
        public string sensor_confidence = "not_available";
        public string pain_assessment = "self_report_only_no_numeric_score";
        public bool has_last_valid_measurement;
        public LowerBodyMeasurement last_valid_measurement;
        public string limitations = "四传感器不能自动识别躯干代偿、疼痛或真实跌倒；照片数不等于临床正确动作数。";
        public S2CombinationResult combinations;
    }

    /// <summary>第5段游戏流程。只控制游戏许可与统计，不触碰人体姿态求解。</summary>
    public sealed class TrainingFlowSession
    {
        public const string RuleVersion = "stage5-flow-v2";
        private TrainingFlowSettings settings;
        private double lastTime, signalIssueStarted, healthyStarted = -1;
        private float lastHealthySample = float.NegativeInfinity;
        private int healthySamples, calibrationVersion;
        private TrainingFlowState resumeState = TrainingFlowState.Training;
        public TrainingFlowState State { get; private set; } = TrainingFlowState.Setup;
        public TrainingSessionResult Result { get; private set; }
        public bool CanRecover { get; private set; }
        public double SignalIssueSeconds { get; private set; }
        public bool CanPause => State == TrainingFlowState.Training ||
            State == TrainingFlowState.ReturnPreparation ||
            State == TrainingFlowState.SignalRecovery;
        public bool IsActive => State == TrainingFlowState.Training ||
            State == TrainingFlowState.ReturnPreparation ||
            State == TrainingFlowState.SignalRecovery ||
            State == TrainingFlowState.Paused || State == TrainingFlowState.SensorError;
        public bool AllowsCapture => State == TrainingFlowState.Training;
        public event Action<TrainingFlowEvent> EventRaised;

        public bool Begin(string action, int target, bool preflight, bool allFourReady,
            int calibration, TrainingFlowSettings config, double now, string appVersion, string stage = "S1")
        {
            if (State != TrainingFlowState.Setup || !preflight || !allFourReady ||
                config == null || !config.IsValid || target < 1 || !Finite(now)) return false;
            settings = config.Copy();
            DateTimeOffset time = DateTimeOffset.Now;
            Result = new TrainingSessionResult
            {
                session_id = Guid.NewGuid().ToString("N"),
                started_at_utc = time.UtcDateTime.ToString("O"),
                timezone_offset = time.ToString("zzz"), action_type = action,
                app_version = appVersion, config_version = settings.config_version,
                stage = stage, scope = stage == "S2" ? "ordered_combination_photo_session" : "single_action_photo_session",
                config_snapshot = settings.Copy(), planned_photos = target,
                preflight_confirmed = true
            };
            lastTime = now;
            calibrationVersion = calibration;
            resumeState = TrainingFlowState.Training;
            SignalIssueSeconds = 0;
            ClearRecovery();
            State = TrainingFlowState.Training;
            Emit("session_started", "preflight_confirmed");
            return true;
        }

        public void Tick(double now, bool healthValid, float sampleTime, int calibration)
        {
            if (!IsActive) return;
            Advance(now);
            bool changedCalibration = calibration != calibrationVersion;
            if (changedCalibration) calibrationVersion = calibration;
            if (changedCalibration && CanPause)
            {
                Freeze(now, "calibration_changed");
                return;
            }

            bool usable = healthValid && !float.IsNaN(sampleTime) && !float.IsInfinity(sampleTime);
            if (State == TrainingFlowState.Training || State == TrainingFlowState.ReturnPreparation)
            {
                if (!usable) BeginSignalRecovery(now, "required_sensor_invalid");
                return;
            }

            if (State == TrainingFlowState.SignalRecovery)
            {
                SignalIssueSeconds = Math.Max(0, now - signalIssueStarted);
                if (!usable)
                {
                    ClearRecovery();
                    if (SignalIssueSeconds >= settings.sensor_error_timeout_seconds)
                        EscalateSignalError("required_sensor_invalid_timeout");
                    return;
                }

                TrackRecovery(now, sampleTime);
                if (!CanRecover) return;
                TrainingFlowState recoveredState = resumeState == TrainingFlowState.ReturnPreparation
                    ? TrainingFlowState.ReturnPreparation
                    : TrainingFlowState.Training;
                State = recoveredState;
                SignalIssueSeconds = 0;
                ClearRecovery();
                Emit("signal_recovered", "automatic_continuous_samples");
                return;
            }

            if (State != TrainingFlowState.SensorError && State != TrainingFlowState.Paused) return;
            if (!usable) ClearRecovery();
            else TrackRecovery(now, sampleTime);
        }

        public bool Pause(double now, string reason = "user_pause")
        {
            if (!CanPause) return false;
            Advance(now);
            State = TrainingFlowState.Paused;
            resumeState = TrainingFlowState.Training;
            SignalIssueSeconds = 0;
            Result.pause_count++;
            ClearRecovery();
            Emit("session_paused", reason);
            return true;
        }

        public bool Resume(double now)
        {
            if ((State != TrainingFlowState.Paused && State != TrainingFlowState.SensorError) || !CanRecover)
                return false;
            Advance(now);
            State = TrainingFlowState.Training;
            resumeState = TrainingFlowState.Training;
            SignalIssueSeconds = 0;
            ClearRecovery();
            Emit("session_resumed", "user_confirmed_reprepare");
            return true;
        }

        public void Freeze(double now, string reason)
        {
            if (!CanPause) return;
            Advance(now);
            bool alreadyCounted = State == TrainingFlowState.SignalRecovery;
            State = TrainingFlowState.SensorError;
            resumeState = TrainingFlowState.Training;
            SignalIssueSeconds = 0;
            if (!alreadyCounted) Result.sensor_error_count++;
            ClearRecovery();
            Emit("sensor_error", reason);
        }

        public bool RecordCapture(double now, bool requiresReturn = true)
        {
            if (!AllowsCapture || Result.captured_photos >= Result.planned_photos) return false;
            Advance(now);
            Result.captured_photos++;
            if (requiresReturn) State = TrainingFlowState.ReturnPreparation;
            Emit("photo_recorded", requiresReturn ? "game_hold_completed" : "combination_steps_completed");
            return true;
        }

        public bool CompleteReturnPreparation(double now)
        {
            if (State != TrainingFlowState.ReturnPreparation) return false;
            Advance(now);
            State = TrainingFlowState.Training;
            Emit("return_preparation_completed", "existing_action_returned");
            return true;
        }

        public void ObserveMeasurement(LowerBodyMeasurement sample)
        {
            if (!IsActive || !sample.IsValid) return;
            Result.has_last_valid_measurement = true;
            Result.last_valid_measurement = sample;
        }

        public bool Finish(double now, string reason)
        {
            if (!IsActive) return false;
            Advance(now);
            State = TrainingFlowState.Review;
            FinalizeResult(reason, false);
            Emit("session_finished", reason);
            return true;
        }

        public bool StopForSafety(double now, string reason = "user_safety_stop")
        {
            if (!IsActive) return false;
            Advance(now);
            State = TrainingFlowState.SafetyStop;
            FinalizeResult(reason, true);
            Emit("safety_stop", reason);
            return true;
        }

        public void ReturnToSetup()
        {
            if (IsActive) return;
            State = TrainingFlowState.Setup;
            resumeState = TrainingFlowState.Training;
            SignalIssueSeconds = 0;
            ClearRecovery();
        }

        private void FinalizeResult(string reason, bool safety)
        {
            Result.termination_reason = reason;
            Result.safety_stop = safety;
            Result.review_status = safety ? "review_required" :
                reason == "sensor_timeout" ? "invalid_session" : "descriptive_only";
            Result.timestamp_utc = DateTimeOffset.UtcNow.ToString("O");
            Result.photo_completion_rate = (float)Result.captured_photos / Result.planned_photos;
            SignalIssueSeconds = 0;
            ClearRecovery();
        }

        private void Advance(double now)
        {
            if (!Finite(now) || now < lastTime) return;
            double delta = now - lastTime;
            lastTime = now;
            Result.duration_seconds += delta;
            if (State == TrainingFlowState.Training || State == TrainingFlowState.ReturnPreparation)
                Result.active_seconds += delta;
            else if (State == TrainingFlowState.Paused) Result.paused_seconds += delta;
            else if (State == TrainingFlowState.SignalRecovery || State == TrainingFlowState.SensorError)
                Result.sensor_error_seconds += delta;
        }

        private void BeginSignalRecovery(double now, string reason)
        {
            resumeState = State;
            State = TrainingFlowState.SignalRecovery;
            signalIssueStarted = now;
            SignalIssueSeconds = 0;
            Result.sensor_error_count++;
            ClearRecovery();
            Emit("sensor_error", reason);
        }

        private void EscalateSignalError(string reason)
        {
            State = TrainingFlowState.SensorError;
            resumeState = TrainingFlowState.Training;
            ClearRecovery();
            Emit("sensor_error_escalated", reason);
        }

        private void TrackRecovery(double now, float sampleTime)
        {
            if (sampleTime < lastHealthySample)
            {
                ClearRecovery();
                return;
            }
            if (sampleTime <= lastHealthySample) return;
            if (healthyStarted < 0) healthyStarted = now;
            lastHealthySample = sampleTime;
            healthySamples++;
            CanRecover = healthySamples >= 2 &&
                now - healthyStarted >= settings.recovery_confirm_seconds;
        }

        private void ClearRecovery()
        {
            CanRecover = false;
            healthyStarted = -1;
            healthySamples = 0;
            lastHealthySample = float.NegativeInfinity;
        }

        private void Emit(string type, string reason)
        {
            EventRaised?.Invoke(new TrainingFlowEvent
            {
                event_id = Guid.NewGuid().ToString("N"), session_id = Result.session_id,
                timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
                timezone_offset = DateTimeOffset.Now.ToString("zzz"),
                event_type = type, reason_code = reason, state = State.ToString(),
                action_type = Result.action_type, config_version = settings.config_version,
                app_version = Result.app_version,
                config_snapshot = type == "session_started" ? settings.Copy() : null,
                review_status = Result.review_status,
                stage = Result.stage,
                captured_photos = Result.captured_photos
            });
        }
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
