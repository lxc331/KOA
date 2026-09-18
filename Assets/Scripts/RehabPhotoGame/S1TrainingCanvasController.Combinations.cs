using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RehabPhotoGame
{
    public sealed partial class S1TrainingCanvasController
    {
        private readonly S2CombinationSession combination = new S2CombinationSession();
        private S2CombinationSettings s2Settings;
        private S2TrainingPlan s2Plan = S2TrainingPlan.AThenB;
        private bool s2Selected;
        private S2CombinationStep activatedS2Step = S2CombinationStep.Completed;
        private string s2TransitionHint = "";
        private string s2PhotoError = "";
        private Button s1StageButton, s2StageButton, s2RestartButton, s2SkipButton;
        private Button s2AOnlyButton, s2BOnlyButton, s2BothButton;
        private TMP_Text s2PlanText, s2StepsText, s2AlbumText;
        private GameObject s2StepPanel, s2ActionControls, s2AlbumPanel;
        private RawImage s2AlbumFirst, s2AlbumSecond;
        // B 独立选材，不改变 A 和 S1 已验收的素材顺序。
        private static readonly string[] S2BHighResources =
        {
            "RehabPhotoGame/Photos/B_High/birds_01",
            "RehabPhotoGame/Photos/B_High/kite_01",
            "RehabPhotoGame/Photos/B_High/lanterns_01",
            "RehabPhotoGame/Photos/B_High/mountains_01"
        };
        private static readonly string[] S2BLowResources =
        {
            "RehabPhotoGame/Photos/C_Low/flowers_01",
            "RehabPhotoGame/Photos/C_Low/leaves_01",
            "RehabPhotoGame/Photos/C_Low/lotus_01",
            "RehabPhotoGame/Photos/C_Low/butterflies_01"
        };

        private void InitializeStageSix()
        {
            var config = Resources.Load<S2CombinationConfig>("RehabPhotoGame/S2CombinationConfig");
            s2Settings = config != null && config.settings != null ? config.settings.Copy() : new S2CombinationSettings();
            combination.EventRaised += HandleS2Event;
        }

        private void BuildStageSelection(Transform parent)
        {
            s1StageButton = CreateButton(parent, "S1 Stage", "S1 单动作",
                new Vector2(0, .72f), new Vector2(.48f, 1), Green, Color.white);
            s2StageButton = CreateButton(parent, "S2 Stage", "S2 组合摄影",
                new Vector2(.52f, .72f), Vector2.one, PaleGreen, DarkGreen);
            s1StageButton.onClick.AddListener(() => SelectGameStage(false));
            s2StageButton.onClick.AddListener(() => SelectGameStage(true));
            s2PlanText = CreateText(parent, "S2 Plan", "请选择本次训练内容",
                new Vector2(0, .63f), new Vector2(1, .71f), 14, FontStyles.Bold, DarkGreen, TextAlignmentOptions.Center);
            s2PlanText.gameObject.SetActive(false);
            s2AOnlyButton = CreateButton(parent, "S2 A Only", "仅组合 A",
                new Vector2(0, .34f), new Vector2(.31f, .61f), PaleGreen, DarkGreen);
            s2BOnlyButton = CreateButton(parent, "S2 B Only", "仅组合 B",
                new Vector2(.345f, .34f), new Vector2(.655f, .61f), PaleGreen, DarkGreen);
            s2BothButton = CreateButton(parent, "S2 A Then B", "A + B 连续",
                new Vector2(.69f, .34f), new Vector2(1, .61f), Green, Color.white);
            s2AOnlyButton.onClick.AddListener(() => SelectS2Plan(S2TrainingPlan.AOnly));
            s2BOnlyButton.onClick.AddListener(() => SelectS2Plan(S2TrainingPlan.BOnly));
            s2BothButton.onClick.AddListener(() => SelectS2Plan(S2TrainingPlan.AThenB));
            s2AOnlyButton.gameObject.SetActive(false);
            s2BOnlyButton.gameObject.SetActive(false);
            s2BothButton.gameObject.SetActive(false);
        }

        private void BuildS2Controls(Transform parent)
        {
            // 不移动既有取景画框；顺序条与组合相册放在人物区域的边缘。
            s2StepPanel = CreateImage(parent, "S2 Step Panel", new Vector2(.25f, .81f),
                new Vector2(.635f, .90f), PanelNavy);
            s2StepsText = CreateText(s2StepPanel.transform, "S2 Steps", "",
                new Vector2(.025f, .06f), new Vector2(.975f, .94f), 20, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            s2ActionControls = CreateImage(parent, "S2 Group Controls", new Vector2(.25f, .75f),
                new Vector2(.635f, .80f), Color.clear);
            s2RestartButton = CreateButton(s2ActionControls.transform, "S2 Restart Group", "重新本组",
                Vector2.zero, new Vector2(.48f, 1), PanelNavyLight, Color.white);
            s2SkipButton = CreateButton(s2ActionControls.transform, "S2 Next Group", "调整 / 下一组",
                new Vector2(.52f, 0), Vector2.one, PanelNavyLight, Color.white);
            s2RestartButton.onClick.AddListener(RestartS2Group);
            s2SkipButton.onClick.AddListener(SkipS2Group);
            s2AlbumPanel = CreateImage(parent, "S2 Combination Album", new Vector2(.25f, .255f),
                new Vector2(.635f, .40f), PanelNavy);
            s2AlbumText = CreateText(s2AlbumPanel.transform, "S2 Album Label", "组合照片：完成步骤后自动保存",
                new Vector2(.02f, .70f), new Vector2(.98f, .98f), 18, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            // 游戏面板在模态窗口下方，不能遮挡本人反馈、继续或保存重试入口。
            s2StepPanel.transform.SetSiblingIndex(modalOverlay.transform.GetSiblingIndex());
            s2ActionControls.transform.SetSiblingIndex(modalOverlay.transform.GetSiblingIndex());
            s2AlbumPanel.transform.SetSiblingIndex(modalOverlay.transform.GetSiblingIndex());
            RefreshS2Controls();
        }

        private void EnsureS2AlbumImages()
        {
            if (s2AlbumFirst != null) return;
            s2AlbumFirst = CreateRawImage(s2AlbumPanel.transform, "S2 First Photo", new Vector2(.23f, .05f),
                new Vector2(.48f, .68f), Color.white);
            s2AlbumSecond = CreateRawImage(s2AlbumPanel.transform, "S2 Second Photo", new Vector2(.52f, .05f),
                new Vector2(.77f, .68f), Color.white);
        }

        private void SelectGameStage(bool s2)
        {
            if (flow.State != TrainingFlowState.Setup || session.State != S1TrainingRunState.Idle || s2Selected == s2) return;
            s2Selected = s2;
            if (s2) { EnsureS2AlbumImages(); PrepareS2Setup(); }
            else
            {
                session.SetTarget(3);
                // 恢复当前单动作入口，继续沿用既有模式启停行为。
                PhotoTrainingMode mode = trainingMode;
                trainingMode = mode == PhotoTrainingMode.SeatedKneeExtension ? PhotoTrainingMode.SitToStand : PhotoTrainingMode.SeatedKneeExtension;
                SelectTrainingMode(mode);
            }
            RefreshAllVisuals();
        }

        private void PrepareS2Setup()
        {
            bool startsWithB = s2Plan == S2TrainingPlan.BOnly;
            trainingMode = startsWithB ? PhotoTrainingMode.SitToStand : PhotoTrainingMode.SeatedKneeExtension;
            training?.SetTrainingModeActive(!startsWithB, "s2_setup");
            training?.SetSessionPaused(true, "s2_setup");
            sitToStandTraining?.SetModeActive(startsWithB, "s2_setup");
            sitToStandTraining?.SetSessionPaused(true, "s2_setup");
            shallowSquatTraining?.SetModeActive(false, "s2_setup");
            shallowSquatTraining?.SetSessionPaused(true, "s2_setup");
            session.SetTarget(S2PlannedTarget());
            activatedS2Step = S2CombinationStep.Completed;
            s2TransitionHint = "";
            s2PhotoError = "";
            if (s2AlbumFirst != null) s2AlbumFirst.texture = null;
            if (s2AlbumSecond != null) s2AlbumSecond.texture = null;
            if (s2AlbumText != null) s2AlbumText.text = "组合照片：完成步骤后自动保存";
            focusSession.Reset();
            SelectActivePhoto();
        }

        private void ChangeS2Target(int delta)
        {
            if (flow.State != TrainingFlowState.Setup) return;
            if (s2Plan != S2TrainingPlan.BOnly)
                s2Settings.groups_a = Mathf.Clamp(s2Settings.groups_a + delta, 1, 5);
            if (s2Plan != S2TrainingPlan.AOnly)
                s2Settings.groups_b = Mathf.Clamp(s2Settings.groups_b + delta, 1, 5);
            session.SetTarget(S2PlannedTarget());
        }

        private void SelectS2Plan(S2TrainingPlan selectedPlan)
        {
            if (!s2Selected || flow.State != TrainingFlowState.Setup || session.State != S1TrainingRunState.Idle) return;
            s2Plan = selectedPlan;
            PrepareS2Setup();
            RefreshAllVisuals();
        }

        private int S2PlannedTarget() => s2Plan == S2TrainingPlan.AOnly ? s2Settings.groups_a :
            s2Plan == S2TrainingPlan.BOnly ? s2Settings.groups_b : s2Settings.groups_a + s2Settings.groups_b;

        private bool S2RuntimeReady()
        {
            if (s2Settings == null || !s2Settings.IsValid) return false;
            bool aReady = training != null && sitToStandTraining != null && distantPhoto != null && AvailableHighPhotoCount() > 0;
            bool bReady = sitToStandTraining != null && shallowSquatTraining != null &&
                SelectS2Resource(S2BHighResources, 0) != "" && SelectS2Resource(S2BLowResources, 0) != "";
            return s2Plan == S2TrainingPlan.AOnly ? aReady :
                s2Plan == S2TrainingPlan.BOnly ? bReady : aReady && bReady;
        }

        private void RefreshS2Setup()
        {
            if (s1StageButton == null) return;
            bool setup = flow.State == TrainingFlowState.Setup;
            s1StageButton.interactable = s2StageButton.interactable = setup;
            StyleSelection(s1StageButton, !s2Selected);
            StyleSelection(s2StageButton, s2Selected);
            kneeModeButton.gameObject.SetActive(!s2Selected);
            sitToStandModeButton.gameObject.SetActive(!s2Selected);
            shallowSquatModeButton.gameObject.SetActive(!s2Selected);
            s2PlanText.gameObject.SetActive(s2Selected);
            s2AOnlyButton.gameObject.SetActive(s2Selected);
            s2BOnlyButton.gameObject.SetActive(s2Selected);
            s2BothButton.gameObject.SetActive(s2Selected);
            s2AOnlyButton.interactable = s2BOnlyButton.interactable = s2BothButton.interactable = setup;
            if (s2Selected)
            {
                StyleSelection(s2AOnlyButton, s2Plan == S2TrainingPlan.AOnly);
                StyleSelection(s2BOnlyButton, s2Plan == S2TrainingPlan.BOnly);
                StyleSelection(s2BothButton, s2Plan == S2TrainingPlan.AThenB);
            }
            targetControls.transform.Find("Target Label").GetComponent<TMP_Text>().text = s2Selected ? "本次组数" : "照片目标";
            if (!s2Selected) { targetValueText.fontSize = 24; return; }
            targetValueText.text = s2Plan == S2TrainingPlan.AOnly ? $"A {s2Settings.groups_a}组" :
                s2Plan == S2TrainingPlan.BOnly ? $"B {s2Settings.groups_b}组" : $"{s2Settings.groups_a}+{s2Settings.groups_b}组";
            targetValueText.fontSize = 20;
            targetMinusButton.interactable = s2Plan == S2TrainingPlan.AOnly ? s2Settings.groups_a > 1 :
                s2Plan == S2TrainingPlan.BOnly ? s2Settings.groups_b > 1 : s2Settings.groups_a > 1 && s2Settings.groups_b > 1;
            targetPlusButton.interactable = s2Plan == S2TrainingPlan.AOnly ? s2Settings.groups_a < 5 :
                s2Plan == S2TrainingPlan.BOnly ? s2Settings.groups_b < 5 : s2Settings.groups_a < 5 && s2Settings.groups_b < 5;
            modalTitleText.text = "S2 组合摄影 · 训练前准备";
            string planLine = s2Plan == S2TrainingPlan.AOnly
                ? $"已选择仅组合 A（{s2Settings.groups_a} 组）：伸膝对焦 → 放下 → 坐站拍高处\n"
                : s2Plan == S2TrainingPlan.BOnly
                    ? $"仅组合 B（{s2Settings.groups_b} 组）：坐稳 → 站起拍高处 → 提示太高 → 浅蹲拍低处 → 站回\n"
                    : $"已选择 A+B：A {s2Settings.groups_a} 组完成后，再训练 B {s2Settings.groups_b} 组\n";
            modalBodyText.text = "请在下方选择本次训练内容，再完成四传感器检查、本人确认和站姿标定。\n" +
                planLine +
                "停顿较久只提示，可按自己的节奏继续。\n" + StartGateSummary();
        }

        private void StartS2Training()
        {
            if (!CanStartTraining() || !BeginFlowSession())
            {
                motionUI?.SetFormalConnectionPanelVisible(true);
                return;
            }
            flow.Result.combinations = null;
            s2PhotoError = "";
            session.Start();
            resultCard.SetActive(false);
            s2TransitionHint = "";
            combination.Begin(s2Settings, s2Plan, flow.Result.active_seconds);
            flow.Result.combinations = combination.Result;
            ActivateS2Step(true);
            motionUI?.SetFormalConnectionPanelVisible(false);
            connectionPanelAutoHidden = true;
        }

        private S2ActionFrame CurrentS2Frame()
        {
            if (combination.RequiredMode == PhotoTrainingMode.SeatedKneeExtension) return S2ActionFrame.From(snapshot);
            if (combination.RequiredMode == PhotoTrainingMode.SitToStand) return S2ActionFrame.From(sitToStandSnapshot);
            return S2ActionFrame.From(shallowSquatSnapshot);
        }

        private void ReadS2Snapshots()
        {
            snapshot = training != null ? training.CurrentSnapshot : default;
            sitToStandSnapshot = sitToStandTraining != null ? sitToStandTraining.CurrentSnapshot : default;
            shallowSquatSnapshot = shallowSquatTraining != null ? shallowSquatTraining.CurrentSnapshot : default;
        }

        private void ActivateS2Step(bool forceReset = false)
        {
            if (!combination.IsRunning) return;
            // A 结束时仍在坐站模式。进入 B 也必须清除旧坐姿参考，不能沿用 A 的返回态。
            forceReset |= combination.Step == S2CombinationStep.SeatedPreparationB && activatedS2Step != combination.Step;
            PhotoTrainingMode mode = combination.RequiredMode;
            bool modeChanged = trainingMode != mode;
            if (forceReset) SuspendGameRuntimes("s2_reprepare");
            if (modeChanged || forceReset)
            {
                // 只调用既有训练模式接口。动作层原有角度、根补偿和坐姿映射均保持不变。
                training?.SetTrainingModeActive(mode == PhotoTrainingMode.SeatedKneeExtension, "s2_ordered_step");
                sitToStandTraining?.SetModeActive(mode == PhotoTrainingMode.SitToStand, "s2_ordered_step");
                shallowSquatTraining?.SetModeActive(mode == PhotoTrainingMode.ShallowSquat, "s2_ordered_step");
                trainingMode = mode;
                if (mode == PhotoTrainingMode.SeatedKneeExtension) training?.SetSessionPaused(false, "s2_step_start");
                else if (mode == PhotoTrainingMode.SitToStand) sitToStandTraining?.SetSessionPaused(false, "s2_step_start");
                else shallowSquatTraining?.SetSessionPaused(false, "s2_step_start");
                ReadS2Snapshots();
                combination.Bind(CurrentS2Frame());
                focusSession.Reset();
            }
            if (activatedS2Step != combination.Step || forceReset)
            {
                activatedS2Step = combination.Step;
                s2TransitionHint = "";
                SpeakS2Step();
            }
            if (forceReset) s2NeedsReset = false;
            SelectS2Photo();
        }

        private void UpdateS2Combination()
        {
            if (session.State != S1TrainingRunState.Running) return;
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
                focusSession.Update(snapshot); // 只用它更新清晰度；S2 不消费单动作快门。
            combination.Observe(CurrentS2Frame(), flow.AllowsCapture, flow.Result.active_seconds);
            if (!combination.IsRunning) return;
            // 同一模式的回退也需重新准备，不能串接旧动作。
            ActivateS2Step(s2NeedsReset);
            s2NeedsReset = false;
        }

        private bool s2NeedsReset;
        private void HandleS2Event(S2CombinationEvent item)
        {
            if (!s2Selected || flow.Result == null) return;
            if (item.event_type == "combination_restarted") s2NeedsReset = true;
            if (item.event_type == "combination_transition_reminder")
                s2TransitionHint = "不用着急，可以按自己的节奏继续。";
            if (item.event_type == "combination_high_shutter")
                ShowS2Capture("高处已拍 · 拍得太高了\n接下来站稳，再浅蹲拍低处", true);
            if (item.event_type == "combination_low_shutter")
                ShowS2Capture("低处已拍，请缓慢站回\n站回后生成组合照片", true);
            if (item.photo != null)
            {
                try
                {
                    byte[] jpg = S2CombinationPhotoWriter.Encode(item.photo);
                    item.photo.image_file = resultStore.SavePhoto(flow.Result.session_id, item.photo.photo_id, jpg);
                    item.photo.image_status = "saved";
                }
                catch (Exception e)
                {
                    item.photo.image_status = "write_error";
                    s2PhotoError = e.Message;
                    Debug.LogWarning("组合照片保存失败：" + e.Message);
                }
                if (flow.RecordCapture(Time.realtimeSinceStartup, false)) session.RecordCapture();
                ShowS2Capture($"组合 {item.photo.combination_type} 第 {item.photo.group_number} 组完成\n" +
                    (item.photo.image_status == "saved" ? "组合照片已保留" : "照片待保存，请重试保存"),
                    item.photo.combination_type == "A");
                s2AlbumFirst.texture = Resources.Load<Texture2D>(item.photo.first_resource);
                s2AlbumSecond.texture = Resources.Load<Texture2D>(item.photo.second_resource);
                s2AlbumFirst.uvRect = S2CombinationPhotoWriter.ViewRect(item.photo.combination_type, false);
                s2AlbumSecond.uvRect = S2CombinationPhotoWriter.ViewRect(item.photo.combination_type, true);
                s2AlbumText.text = $"组合 {item.photo.combination_type} · 第 {item.photo.group_number} 组 · 已收集 {combination.CompletedPhotos} 张";
            }
            WriteFlowEvent(new TrainingFlowEvent
            {
                event_id = Guid.NewGuid().ToString("N"), session_id = flow.Result.session_id,
                timestamp_utc = DateTimeOffset.UtcNow.ToString("O"), timezone_offset = DateTimeOffset.Now.ToString("zzz"),
                event_type = item.event_type, reason_code = item.reason_code, stage = "S2",
                state = flow.State.ToString(), action_type = "s2_combination",
                rule_version = S2CombinationSession.RuleVersion, config_version = s2Settings.config_version,
                app_version = Application.version, captured_photos = combination.CompletedPhotos, combination = item
            });
            if (s2PhotoError != "") writeError = s2PhotoError;
            if (item.event_type == "combination_plan_completed")
            {
                flow.Result.combinations = combination.Result;
                FinishFlowSession("combination_plan_completed");
                session.End();
                SuspendGameRuntimes("s2_plan_completed");
            }
        }

        private void ShowS2Capture(string text, bool shutter)
        {
            resultText.text = text;
            resultCard.SetActive(true);
            resultEndsAt = Time.unscaledTime + 2.5f;
            if (!shutter) return;
            flashEndsAt = Time.unscaledTime + .20f;
            if (audioSource != null && shutterClip != null) audioSource.PlayOneShot(shutterClip);
        }

        private void RestartS2Group()
        {
            TickFlowHealth();
            if (!s2Selected || !flow.AllowsCapture || !combination.IsRunning) return;
            combination.Restart(flow.Result.active_seconds, "user_restart_group");
            ActivateS2Step(true);
            s2NeedsReset = false;
            RefreshAllVisuals();
        }

        private void SkipS2Group()
        {
            TickFlowHealth();
            if (!s2Selected || !flow.AllowsCapture || !combination.Skip(flow.Result.active_seconds)) return;
            ActivateS2Step(true);
            RefreshAllVisuals();
        }

        private void SelectS2Photo()
        {
            int group = combination.IsA ? combination.Result.resolved_a : combination.Result.resolved_b;
            if (combination.IsA)
            {
                string scene = SelectS2Resource(HighPhotoResources, group);
                combination.SetPhotoResources(PhotoResource, scene);
                sourcePhoto = trainingMode == PhotoTrainingMode.SeatedKneeExtension ? distantPhoto : Resources.Load<Texture2D>(scene);
                return;
            }
            string high = SelectS2Resource(S2BHighResources, group);
            string low = SelectS2Resource(S2BLowResources, group);
            combination.SetPhotoResources(high, low);
            sourcePhoto = Resources.Load<Texture2D>(combination.RequiredMode == PhotoTrainingMode.SitToStand ? high : low);
        }

        private static string SelectS2Resource(string[] paths, int group)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[(group + i) % paths.Length];
                if (Resources.Load<Texture2D>(path) != null) return path;
            }
            return ""; // 不拿远景替代高/低处；缺素材时开始门禁会阻止启动。
        }

        private string S2StepInstruction()
        {
            switch (combination.Step)
            {
                case S2CombinationStep.SeatedFocus: return "请坐稳，缓慢伸膝完成远景对焦并保持";
                case S2CombinationStep.LowerLeg: return "对焦完成，请缓慢放下小腿并坐稳";
                case S2CombinationStep.RiseHigh: return "小腿已放下，请双脚着地，缓慢站起拍高处";
                case S2CombinationStep.ReturnSitting: return "高处拍照完成，请缓慢坐回，准备下一组 A";
                case S2CombinationStep.SeatedPreparationB: return "请缓慢坐下并坐稳，双脚着地，准备组合 B";
                case S2CombinationStep.RiseHighB: return "坐位准备完成，请缓慢站起，站稳拍摄高处景物";
                case S2CombinationStep.HighPhotoFeedbackB: return "高处已拍，拍得太高了！接下来站稳，再浅蹲拍低处";
                case S2CombinationStep.StandingPreparation: return "高处已拍，请先站直站稳，再缓慢浅蹲拍低处";
                case S2CombinationStep.LowerLow: return "请缓慢浅蹲，进入低处目标后保持等待拍照";
                case S2CombinationStep.ReturnStanding: return "低处拍照完成，请缓慢站直并站稳，完成组合 B";
                default: return "组合计划完成，请先休息";
            }
        }

        private void SpeakS2Step()
        {
            string text = combination.Step == S2CombinationStep.RiseHigh || combination.Step == S2CombinationStep.RiseHighB ? "请缓慢站起。" :
                combination.Step == S2CombinationStep.ReturnSitting ? "请缓慢坐回。" :
                combination.Step == S2CombinationStep.SeatedPreparationB ? "请先坐稳。" :
                combination.Step == S2CombinationStep.HighPhotoFeedbackB ? "拍得太高了。" :
                combination.Step == S2CombinationStep.LowerLow ? "请缓慢浅蹲。" :
                combination.Step == S2CombinationStep.ReturnStanding ? "请缓慢站回。" : "";
            // 伸膝阶段已有动作层短提示，避免两个语音通道重复播报。
            if (text != "") voiceGuide?.SpeakText("s2_" + combination.Step.ToString().ToLowerInvariant(), text);
        }

        private void RefreshS2Controls()
        {
            if (s2StepPanel == null) return;
            bool show = s2Selected && flow.IsActive && !FreezeGameFeedback;
            s2StepPanel.SetActive(show);
            s2ActionControls.SetActive(show && flow.AllowsCapture);
            s2AlbumPanel.SetActive(show);
            s2RestartButton.interactable = show && flow.AllowsCapture;
            s2SkipButton.interactable = show && flow.AllowsCapture && combination.Step != S2CombinationStep.ReturnSitting;
            if (show)
                s2StepsText.text = (combination.IsA ? "A：伸膝对焦 → 放下 → 坐站高处" : "B：坐稳 → 站起拍高处 → 浅蹲拍低处 → 站回") +
                    $"\n当前：{S2StepLabel()}  ·  第 {combination.GroupNumber} / {(combination.IsA ? s2Settings.groups_a : s2Settings.groups_b)} 组";
        }

        private string S2StepLabel()
        {
            string[] labels = { "伸膝对焦", "放下小腿", "坐站拍高处", "坐回准备", "站稳衔接", "浅蹲拍低处", "返回站立", "完成",
                "坐位准备", "站起拍高处", "拍得太高了" };
            return labels[(int)combination.Step];
        }

        private void RefreshS2Visuals()
        {
            RefreshS2Setup();
            RefreshS2Controls();
            if (!s2Selected) return;
            titleText.text = "公园摄影站 · S2 组合摄影";
            if (!flow.IsActive) return;
            mainInstructionText.text = S2StepInstruction();
            stageText.text = "● " + S2StepLabel();
            string actionHint = trainingMode == PhotoTrainingMode.SitToStand ? sitToStandSnapshot.BlockReason :
                trainingMode == PhotoTrainingMode.ShallowSquat ? shallowSquatSnapshot.BlockReason : snapshot.BlockReason;
            if (combination.Step != S2CombinationStep.HighPhotoFeedbackB && !string.IsNullOrWhiteSpace(actionHint))
                stageText.text += " · " + actionHint;
            if (s2TransitionHint != "") stageText.text += " · " + s2TransitionHint;
            sessionCountText.text = $"<size=16>组合照片 / 已处理组</size>\n<size=27><b>{combination.CompletedPhotos}张 / {combination.ResolvedGroups}组</b></size>";
            selectedLegText.text = combination.IsA ? "组合 A：伸膝 + 坐站" : "组合 B：坐站高处 + 浅蹲低处";
            coachInstructionText.text = combination.IsA
                ? "① 坐稳，伸膝对焦并保持\n② 缓慢放下小腿\n③ 双脚着地，缓慢站起\n④ 站稳拍摄高处景物"
                : "① 坐稳，双脚平放地面\n② 站起保持，拍摄高处\n③ 提示太高后，浅蹲拍低处\n④ 缓慢站回，完成组合";
            if (combination.Step == S2CombinationStep.HighPhotoFeedbackB)
            {
                SetFill(holdBarFill, 0, .025f, .58f);
                holdText.text = "<size=16>高处已拍</size>\n<size=26>等待提示</size>";
            }
            if (combination.Step == S2CombinationStep.LowerLeg || combination.Step == S2CombinationStep.ReturnStanding ||
                combination.Step == S2CombinationStep.ReturnSitting)
            {
                SetFill(holdBarFill, 0, .025f, .58f);
                holdText.text = "<size=16>回位确认</size>\n<size=26>等待回位</size>";
            }
            photoPromptText.text = combination.Step == S2CombinationStep.ReturnStanding
                ? "低处已拍；站回后才生成完整组合照片" : S2StepInstruction();
        }

        private string S2SummaryLine()
        {
            S2CombinationResult r = flow.Result.combinations;
            if (r == null) return "";
            string completed = r.training_plan == "a_only"
                ? $"S2 仅组合 A {r.completed_a}/{r.config_snapshot.groups_a} 组\n"
                : r.training_plan == "b_only"
                    ? $"S2 仅组合 B {r.completed_b}/{r.config_snapshot.groups_b} 组\n"
                    : $"S2 组合 A {r.completed_a}/{r.config_snapshot.groups_a} 组 · B {r.completed_b}/{r.config_snapshot.groups_b} 组\n";
            return completed + $"主动跳过 {r.skipped_groups} 组 · 重新准备 {r.restart_count} 次\n";
        }

        private bool RetryS2Photos()
        {
            if (!s2Selected || flow.Result == null || flow.Result.combinations == null) return true;
            try
            {
                foreach (S2CombinationPhoto photo in flow.Result.combinations.photos)
                {
                    if (photo.image_status == "saved") continue;
                    photo.image_file = resultStore.SavePhoto(flow.Result.session_id, photo.photo_id, S2CombinationPhotoWriter.Encode(photo));
                    photo.image_status = "saved";
                }
                s2PhotoError = "";
                return true;
            }
            catch (Exception e) { writeError = s2PhotoError = e.Message; return false; }
        }
    }
}
