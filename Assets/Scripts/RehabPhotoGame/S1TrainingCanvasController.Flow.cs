using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RehabPhotoGame
{
    public sealed partial class S1TrainingCanvasController
    {
        private readonly TrainingFlowSession flow = new TrainingFlowSession();
        private TrainingFlowSettings flowSettings;
        private TrainingResultStore resultStore;
        private readonly Queue<TrainingFlowEvent> pendingEvents = new Queue<TrainingFlowEvent>();
        private string savedSessionId = "", writeError = "", resultsDirectory = "";
        private string preparationHint = "";
        private bool environmentConfirmed, attachmentConfirmed, wellbeingConfirmed, helpOpen;
        private string postFeedback = "";
        private bool postDiscomfort;
        private GameObject preflightControls, feedbackControls;
        private Button environmentButton, attachmentButton, wellbeingButton, calibrateButton;
        private Button safetyButton, helpButton, flowDeviceButton, retrySaveButton;
        private TMP_Text safetyNotice;
        private TrainingVoiceGuide voiceGuide;
        private bool PreflightConfirmed => environmentConfirmed && attachmentConfirmed && wellbeingConfirmed;
        private bool FreezeGameFeedback => flow.State == TrainingFlowState.Paused ||
            flow.State == TrainingFlowState.SensorError || flow.State == TrainingFlowState.SafetyStop ||
            flow.State == TrainingFlowState.Review;

        private void InitializeStageFive()
        {
            TrainingFlowConfig config = Resources.Load<TrainingFlowConfig>("RehabPhotoGame/TrainingFlowConfig");
            flowSettings = config != null ? config.settings : new TrainingFlowSettings();
            resultsDirectory = Path.Combine(Application.isEditor
                ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"))
                : Path.Combine(Application.persistentDataPath, "Logs"), "TrainingSessions");
            resultStore = new TrainingResultStore(resultsDirectory);
            voiceGuide = GetComponent<TrainingVoiceGuide>();
            if (voiceGuide == null) voiceGuide = gameObject.AddComponent<TrainingVoiceGuide>();
            flow.EventRaised += WriteFlowEvent;
            InitializeStageSix();
            if (motionUI != null) motionUI.OnResetRequested += HandleFlowReset;
        }

        private void BuildPreflightControls(Transform card)
        {
            preflightControls = new GameObject("Preflight Checklist", typeof(RectTransform));
            preflightControls.transform.SetParent(card, false);
            SetRect((RectTransform)preflightControls.transform, new Vector2(.08f, .33f), new Vector2(.92f, .57f));
            environmentButton = ChecklistButton("Environment Check", .76f, 1f,
                ToggleEnvironmentConfirmation);
            attachmentButton = ChecklistButton("Attachment Check", .51f, .75f,
                ToggleAttachmentConfirmation);
            wellbeingButton = ChecklistButton("Wellbeing Check", .26f, .50f,
                ToggleWellbeingConfirmation);
            calibrateButton = ChecklistButton("Standing Calibration", 0f, .24f, ConfirmStandingCalibration);

            feedbackControls = new GameObject("Post Training Feedback", typeof(RectTransform));
            feedbackControls.transform.SetParent(card, false);
            SetRect((RectTransform)feedbackControls.transform, new Vector2(.08f, .22f), new Vector2(.92f, .40f));
            CreateText(feedbackControls.transform, "Feedback Label", "训练后感受（本人反馈，可不填）",
                new Vector2(0, .62f), Vector2.one, 20, FontStyles.Normal, DarkGreen, TextAlignmentOptions.Center);
            string[] labels = { "感觉尚可", "疲劳，想休息", "疼痛或不适" };
            string[] codes = { "feeling_ok", "fatigue", "discomfort" };
            for (int i = 0; i < labels.Length; i++)
            {
                string code = codes[i];
                Button button = CreateButton(feedbackControls.transform, "Feedback " + code, labels[i],
                    new Vector2(i / 3f + .01f, .12f), new Vector2((i + 1) / 3f - .01f, .58f), PaleGreen, DarkGreen);
                button.onClick.AddListener(() => RecordPostFeedback(code));
            }
            feedbackControls.SetActive(false);
        }

        private Button ChecklistButton(string name, float low, float high, UnityEngine.Events.UnityAction action)
        {
            Button button = CreateButton(preflightControls.transform, name, name,
                new Vector2(0, low), new Vector2(1, high), PaleGreen, DarkGreen);
            button.onClick.AddListener(action);
            return button;
        }

        private void ToggleEnvironmentConfirmation()
        {
            environmentConfirmed = !environmentConfirmed;
            preparationHint = "";
        }

        private void ToggleAttachmentConfirmation()
        {
            attachmentConfirmed = !attachmentConfirmed;
            preparationHint = "";
        }

        private void ToggleWellbeingConfirmation()
        {
            wellbeingConfirmed = !wellbeingConfirmed;
            preparationHint = "";
        }

        private void BuildFlowControls(Transform parent)
        {
            // 放在模态背景之后：准备、暂停、异常时仍能访问停止/帮助/设备入口。
            Transform bar = CreateImage(parent, "Always Available Safety Bar",
                Vector2.zero, new Vector2(1, .055f), DarkGreen).transform;
            safetyNotice = CreateText(bar, "Safety Boundary", "操作缓慢可控，如有不适请立即停止",
                new Vector2(.018f, .1f), new Vector2(.54f, .9f), 20, FontStyles.Normal,
                Color.white, TextAlignmentOptions.MidlineLeft);
            flowDeviceButton = CreateButton(bar, "Flow Device Settings", "设备设置",
                new Vector2(.55f, .12f), new Vector2(.66f, .88f), PanelNavyLight, Color.white);
            flowDeviceButton.onClick.AddListener(ToggleConnectionPanel);
            helpButton = CreateButton(bar, "Flow Help", "帮助 / 休息",
                new Vector2(.67f, .12f), new Vector2(.80f, .88f), PanelNavyLight, Color.white);
            helpButton.onClick.AddListener(OpenFlowHelp);
            safetyButton = CreateButton(bar, "Safety Stop", "不适 / 安全停止",
                new Vector2(.81f, .12f), new Vector2(.985f, .88f), Hex("B64343"), Color.white);
            safetyButton.onClick.AddListener(SafetyStopTraining);
            retrySaveButton = CreateButton(modalOverlay.transform, "Retry Result Save", "重试保存记录",
                new Vector2(.38f, .12f), new Vector2(.62f, .17f), Warning, DarkGreen);
            retrySaveButton.onClick.AddListener(PersistFlowResult);
            retrySaveButton.gameObject.SetActive(false);
        }

        private bool FourSensorsFresh()
        {
            MotionCaptureState state = motionCapture != null ? motionCapture.State : null;
            return state != null && state.IsConnected && !HasStaleLowerBodySensor(state);
        }

        private LowerBodyMeasurement ReadFlowHealth(bool allFour)
        {
            TrainingLeg leg = allFour || s2Selected || trainingMode != PhotoTrainingMode.SeatedKneeExtension
                ? TrainingLeg.Both : training != null ? training.CurrentSnapshot.Leg : TrainingLeg.Left;
            return motionCapture != null ? motionCapture.ReadLowerBodyMeasurement(
                ActiveSensorTimeout(), ActiveSensorTimeout(), leg) : new LowerBodyMeasurement();
        }

        private bool AllFourReady() => FourSensorsFresh() && ReadFlowHealth(true).IsValid;

        private void ConfirmStandingCalibration()
        {
            if (!PreflightConfirmed)
            {
                preparationHint = "请先完成上方三项本人确认。";
                return;
            }
            if (MotionReady())
            {
                preparationHint = "站姿标定和动捕已经就绪，可以开始训练。";
                return;
            }
            if (!FourSensorsFresh())
            {
                preparationHint = "06～09 尚未全部有效，已打开设备设置，请检查连接和佩戴。";
                motionUI?.SetFormalConnectionPanelVisible(true);
                return;
            }
            // 仅调用客户现有标定入口，不自行计算四元数或创建第二套标定。
            if (motionUI == null || !motionUI.TryBeginDrivingFromFormalUI())
            {
                preparationHint = "数据已到达，但现有站姿标定尚未满足条件；请在设备设置中完成标定。";
                motionUI?.SetFormalConnectionPanelVisible(true);
            }
            else preparationHint = "站姿标定和动捕已就绪，可以开始训练。";
        }

        private bool BeginFlowSession()
        {
            LowerBodyMeasurement health = ReadFlowHealth(true);
            if (!flow.Begin(ActionCode(), session.TargetRepetitions, PreflightConfirmed,
                AllFourReady(), health.CalibrationVersion, flowSettings,
                Time.realtimeSinceStartup, Application.version, s2Selected ? "S2" : "S1")) return false;
            flow.ObserveMeasurement(health);
            savedSessionId = "";
            postFeedback = "";
            postDiscomfort = false;
            if (writeError != "")
            {
                FinishFlowSession("storage_error");
                return false;
            }
            return true;
        }

        private void TickFlowHealth()
        {
            LowerBodyMeasurement health = ReadFlowHealth(false);
            flow.ObserveMeasurement(health);
            flow.Tick(Time.realtimeSinceStartup, MotionReady() && health.IsValid,
                health.SampleTimeSeconds, health.CalibrationVersion);
        }

        private void UpdateStageFiveFlow()
        {
            TrainingFlowState before = flow.State;
            TickFlowHealth();
            // 短时信号波动仍冻结游戏并在界面显示，但不再反复播报语音。
            if (flow.State == TrainingFlowState.SensorError && before != flow.State)
            {
                if (s2Selected) combination.Restart(flow.Result.active_seconds, "sensor_error_reprepare_not_error");
                session.Pause();
                SuspendGameRuntimes("stage5_sensor_error");
                SpeakFlowCue(TrainingVoiceCue.SignalError);
            }
            if (flow.State == TrainingFlowState.Review && before != flow.State)
            {
                session.End();
                SuspendGameRuntimes("stage5_sensor_timeout");
                PersistFlowResult();
            }
            if ((writeError != "" || s2PhotoError != "") && flow.IsActive)
            {
                FinishFlowSession("storage_error");
                session.End();
                SuspendGameRuntimes("stage5_storage_error");
            }
        }

        private void UpdateReturnPreparation()
        {
            if (flow.State != TrainingFlowState.ReturnPreparation || !ReturnPoseConfirmed()) return;
            if (!flow.CompleteReturnPreparation(Time.realtimeSinceStartup)) return;
            SpeakFlowCue(TrainingVoiceCue.ReadyForNext);
        }

        private bool ReturnPoseConfirmed()
        {
            int expectedCompleted = flow.Result != null ? flow.Result.captured_photos : int.MaxValue;
            if (trainingMode == PhotoTrainingMode.ShallowSquat)
                return shallowSquatSnapshot.IsDataValid &&
                    (shallowSquatSnapshot.CompletedRepetitions >= expectedCompleted ||
                     (shallowSquatSnapshot.Stage == ShallowSquatStage.Lowering &&
                      shallowSquatSnapshot.HasStandingReference));
            if (trainingMode == PhotoTrainingMode.SitToStand)
                return sitToStandSnapshot.IsDataValid &&
                    (sitToStandSnapshot.CompletedRepetitions >= expectedCompleted ||
                     (sitToStandSnapshot.Stage == SitToStandStage.Rising &&
                      sitToStandSnapshot.HasSeatedReference));
            return snapshot.IsDataValid &&
                (snapshot.CompletedRepetitions >= expectedCompleted ||
                 (snapshot.Stage == KneeExtensionStage.Extending && snapshot.HasReadyReference));
        }

        private void SpeakFlowCue(TrainingVoiceCue cue)
        {
            string text = TrainingVoicePrompts.Text(cue, trainingMode);
            if (voiceGuide != null && !voiceGuide.Speak(cue, trainingMode)) return;
            motionCapture?.LogTrainingDiagnostic("training_flow", "voice_prompt",
                $"cue={cue}, mode={ActionCode()}, text={text}");
        }

        private void SuspendGameRuntimes(string reason)
        {
            training?.SetSessionPaused(true, reason);
            sitToStandTraining?.SetSessionPaused(true, reason);
            shallowSquatTraining?.SetSessionPaused(true, reason);
            voiceGuide?.StopPlayback();
            if (audioSource != null) audioSource.Stop();
            flashEndsAt = resultEndsAt = 0;
            if (flashImage != null) flashImage.color = Color.clear;
            if (resultCard != null) resultCard.SetActive(false);
        }

        private void SafetyStopTraining()
        {
            if (s2Selected && flow.IsActive) combination.End("user_safety_stop");
            if (!flow.StopForSafety(Time.realtimeSinceStartup)) return;
            session.End();
            helpOpen = false;
            SuspendGameRuntimes("stage5_safety_stop");
            PersistFlowResult();
            RefreshModal();
            RefreshFlowControls();
        }

        private void FinishFlowSession(string reason)
        {
            if (s2Selected && flow.IsActive) combination.End(reason);
            if (flow.Finish(Time.realtimeSinceStartup, reason)) PersistFlowResult();
        }

        private void ReturnFlowToSetup()
        {
            PersistFlowResult();
            if (writeError != "") return;
            flow.ReturnToSetup();
            session.ReturnToSetup();
            if (s2Selected) PrepareS2Setup();
            focusSession.Reset();
            environmentConfirmed = attachmentConfirmed = wellbeingConfirmed = helpOpen = false;
            preparationHint = "";
            voiceGuide?.StopPlayback();
            snapshot = default;
            sitToStandSnapshot = default;
            shallowSquatSnapshot = default;
        }

        private void OpenFlowHelp()
        {
            if (flow.CanPause) PauseTraining();
            helpOpen = !helpOpen;
            if (flow.IsActive) WriteFlowEvent(new TrainingFlowEvent
            {
                event_id = Guid.NewGuid().ToString("N"), session_id = flow.Result.session_id,
                event_type = "help_opened", reason_code = "user_requested_support",
                timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
                timezone_offset = DateTimeOffset.Now.ToString("zzz"), state = flow.State.ToString(),
                action_type = ActionCode(), config_version = flow.Result.config_version
            });
            RefreshModal();
        }

        private void HandleFlowReset()
        {
            if (flow.CanPause)
            {
                if (s2Selected) combination.Restart(flow.Result.active_seconds, "calibration_reset_reprepare_not_error");
                flow.Freeze(Time.realtimeSinceStartup, "user_calibration_reset");
                session.Pause();
                SuspendGameRuntimes("stage5_reset_reprepare");
            }
        }

        private void RefreshFlowControls()
        {
            if (safetyButton == null) return;
            safetyButton.interactable = flow.IsActive;
            safetyNotice.text = writeError != "" ? "记录尚未保存，请重试保存；训练已停止" :
                postFeedback != "" && flow.State != TrainingFlowState.Setup ? postFeedback :
                flow.State == TrainingFlowState.SignalRecovery
                    ? $"信号波动，进度和拍照已冻结，正在自动恢复（{flow.SignalIssueSeconds:F0}/{flowSettings.sensor_error_timeout_seconds:F0} 秒）" :
                flow.State == TrainingFlowState.ReturnPreparation
                    ? ReturnPreparationText() :
                "操作缓慢可控，如有不适请立即停止";
            if (s2Selected && flow.State == TrainingFlowState.Training)
                safetyNotice.text = "S2 按顺序完成组合；可以重新本组、调整下一组或随时休息";
            RefreshS2Controls();
            retrySaveButton.gameObject.SetActive(writeError != "" && !flow.IsActive);
        }

        private bool RefreshFlowModal()
        {
            if (preflightControls == null) return false;
            bool setup = flow.State == TrainingFlowState.Setup && session.State == S1TrainingRunState.Idle;
            preflightControls.SetActive(setup);
            bool review = flow.State == TrainingFlowState.Review || flow.State == TrainingFlowState.SafetyStop;
            feedbackControls.SetActive(review && writeError == "");
            if (!setup && !FreezeGameFeedback) return false;
            modalOverlay.SetActive(true);
            targetControls.SetActive(setup);
            modalSecondaryButton.gameObject.SetActive(!setup && !review);
            SetButtonLabel(modalSecondaryButton, "结束训练");
            if (setup)
            {
                SetRect(modalBodyText.rectTransform, new Vector2(.07f, .57f), new Vector2(.93f, .87f));
                modalTitleText.text = "训练前检查与准备";
                modalBodyText.text = helpOpen
                    ? "先连接 06～09，确认设备固定、环境和本人状态。\n站稳后完成站姿标定，再选择动作和次数。\n伸膝/坐站需坐稳准备；浅蹲需站稳准备。\n四传感器不能自动识别疼痛、躯干代偿或真实跌倒。"
                    : "① 连接四传感器 06～09（仅检查这四个槽位）\n" +
                      (FourSensorsFresh() ? "设备数据正在更新\n" : "请在设备设置中连接并检查更新状态\n") +
                      "② 完成下方本人确认与站姿标定\n③ 选择动作和照片目标，开始后按提示准备\n" +
                      StartGateSummary();
                SetChecklistState(environmentButton, environmentConfirmed,
                    "环境无障碍，椅子/所需支撑稳固");
                SetChecklistState(attachmentButton, attachmentConfirmed,
                    "06～09 位置正确、佩戴固定");
                SetChecklistState(wellbeingButton, wellbeingConfirmed,
                    "本人已考虑疼痛/疲劳状态，本次可以开始");
                SetButtonLabel(calibrateButton, MotionReady()
                    ? "已就绪：站姿标定和动捕（位置改变请先重置）"
                    : FourSensorsFresh()
                        ? "下一步：我已站稳，检查站姿标定"
                        : "下一步：打开设备设置，连接 06～09");
                StyleSelection(calibrateButton, MotionReady());
                calibrateButton.interactable = true;
                targetValueText.text = session.TargetRepetitions + " 张";
                targetMinusButton.interactable = session.TargetRepetitions > S1TrainingSession.MinimumTarget;
                targetPlusButton.interactable = session.TargetRepetitions < S1TrainingSession.MaximumTarget;
                RefreshS2Setup();
                SetButtonLabel(modalPrimaryButton, "开始训练");
                modalPrimaryButton.interactable = CanStartTraining();
                return true;
            }

            if (review)
            {
                TrainingSessionResult result = flow.Result;
                bool safety = flow.State == TrainingFlowState.SafetyStop;
                modalTitleText.text = safety || postDiscomfort ? "已停止 · 本次需复核" : "本次训练总结";
                modalBodyText.text = (result.stage == "S2" ? S2SummaryLine() : "") +
                    $"照片 {result.captured_photos} / {result.planned_photos} 张" +
                    $"　拍摄完成率 {result.photo_completion_rate:P0}\n" +
                    $"游戏有效时长 {result.active_seconds:F0} 秒　暂停 {result.paused_seconds:F0} 秒\n" +
                    $"信号冻结 {result.sensor_error_seconds:F0} 秒（{result.sensor_error_count} 次）\n" +
                    $"结束原因：{TerminationLabel(result.termination_reason)}\n" +
                    (safety || postDiscomfort ? "本次不能直接继续，请先休息并完成适当复核。\n" : "已拍照片保留，可休息后再开始。\n") +
                    "仅为游戏过程记录，不判定临床正确率或疗效。\n" +
                    (writeError != "" ? "保存未完成：" + writeError : "记录已保存至本地 TrainingSessions。");
                SetRect(modalBodyText.rectTransform, new Vector2(.07f, .43f), new Vector2(.93f, .87f));
                SetButtonLabel(modalPrimaryButton, safety || postDiscomfort ? "已知需复核，返回设置" : "返回设置 / 休息");
                modalPrimaryButton.interactable = writeError == "";
                return true;
            }
            SetRect(modalBodyText.rectTransform, new Vector2(.07f, .57f), new Vector2(.93f, .87f));
            bool sensor = flow.State == TrainingFlowState.SensorError;
            modalTitleText.text = sensor ? "信号异常 · 游戏反馈已冻结" : helpOpen ? "帮助与休息" : "训练已暂停";
            modalBodyText.text = (sensor ? "当前训练所需传感器数据无效。请检查连接与佩戴。\n" : "暂停时间不计入游戏有效时长，已拍照片保留。\n") +
                "恢复后需重新确认动作准备姿势，不补算旧保持时间。\n" +
                (s2Selected ? "S2：继续时重新准备本组，已获得的组合照片保留。\n" :
                 trainingMode == PhotoTrainingMode.ShallowSquat ? "浅蹲：站稳 → 双膝屈曲 → 保持 → 拍照 → 站回。\n" :
                 trainingMode == PhotoTrainingMode.SitToStand ? "坐站：坐稳 → 站起 → 保持 → 拍照 → 坐回。\n" :
                 "伸膝：坐稳垂腿 → 伸膝 → 保持 → 拍照 → 放下。\n") +
                (flow.CanRecover ? "数据已恢复，确认准备好后再继续。" : "等待所需传感器连续有效更新。") +
                "\n如有疼痛或不适，请使用底部安全停止。";
            SetButtonLabel(modalPrimaryButton, "已准备好，继续训练");
            modalPrimaryButton.interactable = flow.CanRecover;
            return true;
        }

        private void WriteFlowEvent(TrainingFlowEvent item)
        {
            pendingEvents.Enqueue(item);
            FlushFlowEvents();
        }

        private void FlushFlowEvents()
        {
            try
            {
                while (pendingEvents.Count > 0)
                {
                    TrainingFlowEvent item = pendingEvents.Peek();
                    string json = JsonUtility.ToJson(item);
                    resultStore.Append(item.session_id, json);
                    pendingEvents.Dequeue();
                    motionCapture?.LogTrainingDiagnostic("training_flow", item.event_type, json);
                }
                writeError = "";
            }
            catch (Exception exception) { writeError = exception.Message; Debug.LogWarning("训练记录保存失败：" + exception.Message); }
        }

        private void PersistFlowResult()
        {
            if (flow.Result == null || flow.IsActive) return;
            FlushFlowEvents();
            if (writeError != "") return;
            if (!RetryS2Photos()) return;
            try
            {
                resultStore.Save(flow.Result);
                if (savedSessionId != flow.Result.session_id)
                    motionCapture?.LogTrainingDiagnostic("training_flow", "session_summary", JsonUtility.ToJson(flow.Result));
                savedSessionId = flow.Result.session_id;
            }
            catch (Exception exception) { writeError = exception.Message; Debug.LogWarning("训练总结保存失败：" + exception.Message); }
        }

        private void RecordPostFeedback(string code)
        {
            if (flow.Result == null || flow.IsActive) return;
            WriteFlowEvent(new TrainingFlowEvent
            {
                event_id = Guid.NewGuid().ToString("N"), session_id = flow.Result.session_id,
                event_type = "post_training_self_report", reason_code = code,
                timestamp_utc = DateTimeOffset.UtcNow.ToString("O"), timezone_offset = DateTimeOffset.Now.ToString("zzz"),
                state = flow.State.ToString(), action_type = flow.Result.action_type,
                config_version = flow.Result.config_version, captured_photos = flow.Result.captured_photos,
                review_status = code == "discomfort" || postDiscomfort ? "review_required" : flow.Result.review_status
            });
            if (code == "discomfort") postDiscomfort = true;
            postFeedback = postDiscomfort ? "已记录本人不适反馈，请先停止活动并完成适当复核。" : "已记录本人反馈，可以休息。";
            RefreshModal();
        }

        private string ActionCode() => s2Selected ? "s2_combination" : trainingMode == PhotoTrainingMode.ShallowSquat ? "squat" :
            trainingMode == PhotoTrainingMode.SitToStand ? "sit_to_stand" : "sit_knee_extend";

        private string ReturnPreparationText()
        {
            return trainingMode == PhotoTrainingMode.ShallowSquat
                ? "拍照完成，请缓慢站直并站稳，回位后自动准备下一次"
                : trainingMode == PhotoTrainingMode.SitToStand
                    ? "拍照完成，请缓慢坐回并坐稳，回位后自动准备下一次"
                    : "拍照完成，请缓慢放下小腿并坐稳，回位后自动准备下一次";
        }

        private void SetChecklistState(Button button, bool confirmed, string label)
        {
            SetButtonLabel(button, (confirmed ? "已确认：" : "点击确认：") + label);
            StyleSelection(button, confirmed);
        }

        private string StartGateSummary()
        {
            if (flowSettings == null || !flowSettings.IsValid)
                return "流程参数无效，请检查 TrainingFlowConfig。";
            if (!PreflightConfirmed)
                return "请依次点击下方三项本人确认。\n" + preparationHint;
            if (!FourSensorsFresh())
                return "06～09 尚未全部有效，请点击下一步打开设备设置。\n" + preparationHint;
            if (!MotionReady())
                return "四传感器数据已到达，请站稳并点击下一步完成现有标定/动捕。\n" + preparationHint;
            return "四传感器、本人确认和站姿标定均已就绪。请选择动作和照片目标后开始。";
        }

        private static string TerminationLabel(string reason)
        {
            switch (reason)
            {
                case "target_completed": return "照片目标完成";
                case "combination_plan_completed": return "组合计划完成（含主动跳过的组）";
                case "sensor_timeout": return "传感器异常超时（不计表现失败）";
                case "user_safety_stop": return "本人主动安全停止";
                case "application_exit": return "退出游戏或停止播放";
                case "storage_error": return "本地记录写入异常";
                default: return "本人主动结束";
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused && flow.CanPause) PauseTraining();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && flow.CanPause) PauseTraining();
        }

        private void OnApplicationQuit() { FinishFlowSession("application_exit"); }

        private void OnDisable()
        {
            FinishFlowSession("application_exit");
            SuspendGameRuntimes("stage5_disabled");
        }

        private void DisposeStageFive()
        {
            FinishFlowSession("application_exit");
            voiceGuide?.StopPlayback();
            if (motionUI != null) motionUI.OnResetRequested -= HandleFlowReset;
            flow.EventRaised -= WriteFlowEvent;
            combination.EventRaised -= HandleS2Event;
        }
    }
}
