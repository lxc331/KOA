using System;
using System.Collections.Generic;

namespace RehabPhotoGame
{
    public enum S2CombinationStep
    {
        SeatedFocus, LowerLeg, RiseHigh, ReturnSitting,
        StandingPreparation, LowerLow, ReturnStanding, Completed
    }

    public enum S2TrainingPlan
    {
        AOnly,
        BOnly,
        AThenB
    }

    [Serializable]
    public sealed class S2CombinationSettings
    {
        public string config_version = "stage6-combinations-v2";
        public int groups_a = 5, groups_b = 5;
        public float transition_reminder_seconds = 5f;
        public bool IsValid => !string.IsNullOrWhiteSpace(config_version) &&
            groups_a >= 1 && groups_a <= 5 && groups_b >= 1 && groups_b <= 5 &&
            !float.IsNaN(transition_reminder_seconds) &&
            !float.IsInfinity(transition_reminder_seconds) && transition_reminder_seconds > 0;
        public S2CombinationSettings Copy() => (S2CombinationSettings)MemberwiseClone();
    }

    /// <summary>只包装已有动作快照中的分相与计数，不计算角度或动作质量。</summary>
    public struct S2ActionFrame
    {
        public PhotoTrainingMode Mode;
        public int Version, Completed, CaptureSequence;
        public TrainingLeg Leg;
        public bool Valid, Reference, Ready, Holding, Returning;

        public static S2ActionFrame From(KneeExtensionTrainingSnapshot s) => new S2ActionFrame
        {
            Mode = PhotoTrainingMode.SeatedKneeExtension, Version = s.SessionVersion,
            Completed = s.CompletedRepetitions, Leg = s.Leg,
            Valid = s.IsDataValid && !s.IsSessionPaused && !s.IsSwitchingLeg,
            Reference = s.HasReadyReference, Ready = s.Stage == KneeExtensionStage.Extending,
            Holding = s.Stage == KneeExtensionStage.Holding, Returning = s.Stage == KneeExtensionStage.Returning
        };
        public static S2ActionFrame From(SitToStandTrainingSnapshot s) => new S2ActionFrame
        {
            Mode = PhotoTrainingMode.SitToStand, Version = s.SessionVersion,
            Completed = s.CompletedRepetitions, CaptureSequence = s.CaptureSequence,
            Valid = s.IsModeActive && s.IsDataValid && !s.IsSessionPaused,
            Reference = s.HasSeatedReference, Ready = s.Stage == SitToStandStage.Rising,
            Holding = s.Stage == SitToStandStage.Holding, Returning = s.Stage == SitToStandStage.Returning
        };
        public static S2ActionFrame From(ShallowSquatTrainingSnapshot s) => new S2ActionFrame
        {
            Mode = PhotoTrainingMode.ShallowSquat, Version = s.SessionVersion,
            Completed = s.CompletedRepetitions, CaptureSequence = s.CaptureSequence,
            Valid = s.IsModeActive && s.IsDataValid && !s.IsSessionPaused,
            Reference = s.HasStandingReference, Ready = s.Stage == ShallowSquatStage.Lowering,
            Holding = s.Stage == ShallowSquatStage.Holding, Returning = s.Stage == ShallowSquatStage.Returning
        };
    }

    [Serializable]
    public sealed class S2CombinationPhoto
    {
        public string photo_id, combination_type, timestamp_utc;
        public int group_number;
        public string first_resource, second_resource, image_file;
        public string image_status = "pending";
        public string first_caption, second_caption;
    }

    [Serializable]
    public sealed class S2CombinationResult
    {
        public string rule_version = S2CombinationSession.RuleVersion;
        public string training_plan;
        public S2CombinationSettings config_snapshot;
        public int resolved_a, resolved_b, completed_a, completed_b, skipped_groups, restart_count;
        public int transition_reminders;
        public string final_step;
        public bool pending_low_photo;
        public List<S2CombinationPhoto> photos = new List<S2CombinationPhoto>();
        public string quality_assessment = "existing_action_events_only_not_clinical_correctness";
    }

    [Serializable]
    public sealed class S2CombinationEvent
    {
        public string event_type, reason_code, combination_type, step;
        public int group_number;
        public S2CombinationPhoto photo;
        public S2CombinationSettings config_snapshot;
        public double step_active_seconds;
        public string completed_step;
        public double completed_step_active_seconds;
    }

    /// <summary>S2 游戏编排：只消费现有状态机事件，安全许可由第五段数据门禁提供。</summary>
    public sealed class S2CombinationSession
    {
        public const string RuleVersion = "stage6-sequence-v2";
        private S2CombinationSettings settings;
        private S2TrainingPlan plan;
        private S2ActionFrame previous;
        private bool bound, readyObserved, reminderRaised, reprepareAfterGate;
        private double stepStarted, lastActiveTime;
        private int returnBaseline;
        private string firstResource = "", sceneResource = "";
        public S2CombinationStep Step { get; private set; } = S2CombinationStep.Completed;
        public S2CombinationResult Result { get; private set; }
        public S2TrainingPlan Plan => plan;
        public bool IsRunning => Result != null && Step != S2CombinationStep.Completed;
        public bool IsA => Step == S2CombinationStep.SeatedFocus || Step == S2CombinationStep.LowerLeg ||
            Step == S2CombinationStep.RiseHigh || Step == S2CombinationStep.ReturnSitting;
        public int GroupNumber => Result == null ? 1 : IsA ? Result.resolved_a + 1 : Result.resolved_b + 1;
        public int CompletedPhotos => Result == null ? 0 : Result.photos.Count;
        public int ResolvedGroups => Result == null ? 0 : Result.resolved_a + Result.resolved_b;
        public PhotoTrainingMode RequiredMode => Step == S2CombinationStep.RiseHigh || Step == S2CombinationStep.ReturnSitting
            ? PhotoTrainingMode.SitToStand : IsA ? PhotoTrainingMode.SeatedKneeExtension : PhotoTrainingMode.ShallowSquat;
        public event Action<S2CombinationEvent> EventRaised;

        public bool Begin(S2CombinationSettings config, double activeTime) =>
            Begin(config, S2TrainingPlan.AThenB, activeTime);

        public bool Begin(S2CombinationSettings config, S2TrainingPlan selectedPlan, double activeTime)
        {
            if (IsRunning || config == null || !config.IsValid || !Finite(activeTime)) return false;
            settings = config.Copy();
            plan = selectedPlan;
            Result = new S2CombinationResult
            {
                config_snapshot = settings.Copy(),
                training_plan = PlanCode(selectedPlan)
            };
            Step = selectedPlan == S2TrainingPlan.BOnly
                ? S2CombinationStep.StandingPreparation
                : S2CombinationStep.SeatedFocus;
            firstResource = sceneResource = "";
            bound = readyObserved = reminderRaised = false;
            reprepareAfterGate = false;
            stepStarted = activeTime;
            lastActiveTime = activeTime;
            Emit("combination_started", "selected_" + Result.training_plan);
            return true;
        }

        // 调用现有模式启停接口后立即播种，绝不把旧 CaptureSequence 当作新事件。
        public void Bind(S2ActionFrame frame)
        {
            if (frame.Mode != RequiredMode) return;
            previous = frame;
            bound = true;
            readyObserved = false;
            returnBaseline = frame.Completed;
        }

        public void SetPhotoResources(string distant, string scene)
        {
            firstResource = distant ?? "";
            sceneResource = scene ?? "";
        }

        public void Observe(S2ActionFrame frame, bool allowed, double activeTime)
        {
            if (!IsRunning || frame.Mode != RequiredMode || !Finite(activeTime) || activeTime < lastActiveTime) return;
            lastActiveTime = activeTime;
            if (!bound) Bind(frame);
            if (frame.Version != previous.Version ||
                (frame.Mode == PhotoTrainingMode.SeatedKneeExtension && frame.Leg != previous.Leg))
            {
                Restart(activeTime, "action_session_changed");
                Bind(frame);
                return;
            }
            if (!allowed || !frame.Valid)
            {
                // 消费被门禁挡住的边沿，恢复时不能补拍或补做回位。
                if (frame.Completed > previous.Completed || frame.CaptureSequence > previous.CaptureSequence ||
                    (frame.Returning && previous.Holding)) reprepareAfterGate = true;
                previous = frame;
                return;
            }
            if (reprepareAfterGate)
            {
                reprepareAfterGate = false;
                Restart(activeTime, "gated_action_reprepare_not_error");
                Bind(frame);
                return;
            }
            if (frame.Completed < previous.Completed || frame.CaptureSequence < previous.CaptureSequence)
            {
                Restart(activeTime, "action_counter_reset");
                Bind(frame);
                return;
            }
            bool held = readyObserved && previous.Valid && previous.Holding && frame.Returning && frame.Reference;
            bool shutter = held && (frame.Mode == PhotoTrainingMode.SeatedKneeExtension ||
                frame.CaptureSequence > previous.CaptureSequence);
            bool returned = previous.Valid && frame.Completed > previous.Completed && frame.Completed > returnBaseline;
            if (frame.Reference && frame.Ready) readyObserved = true;
            previous = frame;

            switch (Step)
            {
                case S2CombinationStep.SeatedFocus:
                    if (shutter)
                    {
                        returnBaseline = frame.Completed;
                        Move(S2CombinationStep.LowerLeg, activeTime, "focus_hold_completed");
                    }
                    break;
                case S2CombinationStep.LowerLeg:
                    if (returned) Move(S2CombinationStep.RiseHigh, activeTime, "knee_return_completed");
                    else if (!frame.Reference) Restart(activeTime, "knee_reference_lost");
                    break;
                case S2CombinationStep.RiseHigh:
                    if (shutter)
                    {
                        CommitPhoto(true);
                        Result.resolved_a++;
                        Result.completed_a++;
                        returnBaseline = frame.Completed;
                        Move(Result.resolved_a < settings.groups_a ? S2CombinationStep.ReturnSitting :
                            plan == S2TrainingPlan.AOnly ? S2CombinationStep.Completed :
                            S2CombinationStep.StandingPreparation, activeTime, "high_photo_completed");
                    }
                    else if (readyObserved && !frame.Reference) Restart(activeTime, "seated_reference_lost");
                    break;
                case S2CombinationStep.ReturnSitting:
                    if (returned) Move(S2CombinationStep.SeatedFocus, activeTime, "sit_return_completed");
                    else if (!frame.Reference) Restart(activeTime, "sit_return_reference_lost");
                    break;
                case S2CombinationStep.StandingPreparation:
                    if (frame.Ready && frame.Reference)
                        Move(S2CombinationStep.LowerLow, activeTime, "standing_prepared");
                    break;
                case S2CombinationStep.LowerLow:
                    if (shutter)
                    {
                        Result.pending_low_photo = true;
                        returnBaseline = frame.Completed;
                        Emit("combination_low_shutter", "await_standing_return");
                        Move(S2CombinationStep.ReturnStanding, activeTime, "low_hold_completed");
                    }
                    else if (readyObserved && !frame.Reference) Restart(activeTime, "standing_reference_lost");
                    break;
                case S2CombinationStep.ReturnStanding:
                    if (returned)
                    {
                        CommitPhoto(false);
                        Result.pending_low_photo = false;
                        Result.resolved_b++;
                        Result.completed_b++;
                        Move(Result.resolved_b < settings.groups_b ? S2CombinationStep.StandingPreparation :
                            S2CombinationStep.Completed, activeTime, "standing_return_completed");
                    }
                    else if (!frame.Reference) Restart(activeTime, "standing_return_reference_lost");
                    break;
            }
            if (IsRunning && !reminderRaised && activeTime - stepStarted >= settings.transition_reminder_seconds &&
                (Step == S2CombinationStep.LowerLeg || Step == S2CombinationStep.RiseHigh || Step == S2CombinationStep.ReturnSitting))
            {
                reminderRaised = true;
                Result.transition_reminders++;
                Emit("combination_transition_reminder", "slow_transition_not_error");
            }
            Result.final_step = Step.ToString();
        }

        public void Restart(double activeTime, string reason)
        {
            if (!IsRunning || !Finite(activeTime)) return;
            lastActiveTime = Math.Max(lastActiveTime, activeTime);
            Result.restart_count++;
            Result.pending_low_photo = false;
            firstResource = sceneResource = "";
            Emit("combination_restarted", reason);
            Move(IsA ? S2CombinationStep.SeatedFocus : S2CombinationStep.StandingPreparation, activeTime, "reprepare_current_group");
            bound = readyObserved = false;
            reprepareAfterGate = false;
        }

        public bool Skip(double activeTime)
        {
            if (!IsRunning || !Finite(activeTime) || Step == S2CombinationStep.ReturnSitting) return false;
            lastActiveTime = Math.Max(lastActiveTime, activeTime);
            Emit("combination_group_skipped", "user_adjust_next_group");
            Result.skipped_groups++;
            Result.pending_low_photo = false;
            if (IsA)
            {
                Result.resolved_a++;
                Move(Result.resolved_a < settings.groups_a ? S2CombinationStep.SeatedFocus :
                    plan == S2TrainingPlan.AOnly ? S2CombinationStep.Completed :
                    S2CombinationStep.StandingPreparation, activeTime, "next_group_preparation");
            }
            else
            {
                Result.resolved_b++;
                Move(Result.resolved_b < settings.groups_b ? S2CombinationStep.StandingPreparation :
                    S2CombinationStep.Completed, activeTime, "next_group_preparation");
            }
            firstResource = sceneResource = "";
            bound = readyObserved = false;
            return true;
        }

        public void End(string reason)
        {
            if (!IsRunning) return;
            Result.final_step = Step.ToString();
            Result.pending_low_photo = false;
            Emit("combination_stopped", reason);
            Step = S2CombinationStep.Completed;
        }

        private void CommitPhoto(bool a)
        {
            var photo = new S2CombinationPhoto
            {
                photo_id = Guid.NewGuid().ToString("N"), combination_type = a ? "A" : "B",
                group_number = GroupNumber, timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
                first_resource = a ? firstResource : sceneResource, second_resource = sceneResource,
                first_caption = a ? "坐姿伸膝对焦" : "站立取景", second_caption = a ? "坐站高处" : "浅蹲低处（已站回）"
            };
            Result.photos.Add(photo);
            Emit("combination_photo_completed", "existing_actions_in_order", photo);
        }

        private void Move(S2CombinationStep next, double time, string reason)
        {
            PhotoTrainingMode oldMode = RequiredMode;
            string oldStep = Step.ToString();
            double elapsed = Math.Max(0, time - stepStarted);
            Step = next;
            stepStarted = time;
            reminderRaised = false;
            if (oldMode != RequiredMode) bound = readyObserved = false;
            if (next == S2CombinationStep.SeatedFocus || next == S2CombinationStep.StandingPreparation)
                readyObserved = false;
            Result.final_step = next.ToString();
            Emit(next == S2CombinationStep.Completed ? "combination_plan_completed" : "combination_step_changed",
                reason, completedStep: oldStep, completedStepSeconds: elapsed);
        }

        private void Emit(string type, string reason, S2CombinationPhoto photo = null,
            string completedStep = null, double completedStepSeconds = 0) => EventRaised?.Invoke(new S2CombinationEvent
        {
            event_type = type, reason_code = reason, combination_type = IsA ? "A" : "B",
            step = Step.ToString(), group_number = GroupNumber, photo = photo,
            config_snapshot = type == "combination_started" ? settings.Copy() : null,
            step_active_seconds = Math.Max(0, lastActiveTime - stepStarted),
            completed_step = completedStep, completed_step_active_seconds = completedStepSeconds
        });
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static string PlanCode(S2TrainingPlan selectedPlan) =>
            selectedPlan == S2TrainingPlan.AOnly ? "a_only" :
            selectedPlan == S2TrainingPlan.BOnly ? "b_only" : "a_then_b";
    }
}
