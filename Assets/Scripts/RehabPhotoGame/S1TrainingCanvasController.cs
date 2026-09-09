using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RehabPhotoGame
{
    /// <summary>
    /// S1 正式训练界面。只消费坐姿伸膝快照，不读取或展示四元数、欧拉角及串口帧。
    /// </summary>
    [DefaultExecutionOrder(1200)]
    [DisallowMultipleComponent]
    public sealed class S1TrainingCanvasController : MonoBehaviour
    {
        private const string PhotoResource =
            "RehabPhotoGame/Photos/A_Distant/spring_01";
        private const string ShaderResource =
            "RehabPhotoGame/Shaders/FocusBlur";
        private const string BundledFontResource =
            "RehabPhotoGame/Fonts/NotoSansSC-Regular";
        private const string RequiredChineseGlyphs =
            "中文公园摄影站坐姿伸膝等待设备连接开始训练暂停继续结束左右腿教练示范目标次数完成保持缓慢抬起放下照片清晰";

        private static readonly Color DarkGreen = Hex("173E2B");
        private static readonly Color Green = Hex("2F7A4C");
        private static readonly Color BrightGreen = Hex("55A96F");
        private static readonly Color PaleGreen = Hex("E8F3E9");
        private static readonly Color WarmWhite = Hex("F8FBF7");
        private static readonly Color Ink = Hex("173126");
        private static readonly Color Muted = Hex("64776D");
        private static readonly Color Warning = Hex("D49742");

        private readonly S1TrainingSession session = new S1TrainingSession();
        private readonly PhotoFocusSession focusSession = new PhotoFocusSession();

        private SeatedKneeExtensionRuntimeFix training;
        private MotionCaptureController motionCapture;
        private MotionCaptureUI motionUI;
        private KneeExtensionTrainingSnapshot snapshot;

        private Canvas canvas;
        private TMP_FontAsset runtimeFont;
        private Font sourceSystemFont;
        private bool ownsRuntimeFont;

        private TMP_Text connectionText;
        private TMP_Text sensorText;
        private TMP_Text selectedLegText;
        private TMP_Text coachInstructionText;
        private TMP_Text photoFocusText;
        private TMP_Text photoPromptText;
        private TMP_Text mainInstructionText;
        private TMP_Text stageText;
        private TMP_Text sessionCountText;
        private TMP_Text holdText;
        private TMP_Text targetValueText;
        private TMP_Text modalTitleText;
        private TMP_Text modalBodyText;
        private TMP_Text resultText;

        private Image focusBarFill;
        private Image holdBarFill;
        private Image flashImage;
        private RawImage photoImage;
        private GameObject resultCard;
        private GameObject modalOverlay;
        private GameObject targetControls;
        private GameObject coachPanel;
        private GameObject photoPanel;
        private GameObject trainingPanel;

        private Button settingsButton;
        private Button pauseButton;
        private Button endButton;
        private Button leftLegButton;
        private Button rightLegButton;
        private Button targetMinusButton;
        private Button targetPlusButton;
        private Button modalPrimaryButton;
        private Button modalSecondaryButton;

        private Texture2D sourcePhoto;
        private RenderTexture focusedPhoto;
        private Material blurMaterial;
        private AudioSource audioSource;
        private AudioClip shutterClip;
        private float flashEndsAt;
        private float resultEndsAt;
        private bool connectionPanelAutoHidden;

        private void Start()
        {
            training = GetComponent<SeatedKneeExtensionRuntimeFix>();
            if (training == null)
                training = FindObjectOfType<SeatedKneeExtensionRuntimeFix>();
            motionCapture = FindObjectOfType<MotionCaptureController>();
            motionUI = motionCapture != null
                ? motionCapture.GetComponent<MotionCaptureUI>()
                : FindObjectOfType<MotionCaptureUI>();

            PhotoFocusFeedback legacy = GetComponent<PhotoFocusFeedback>();
            if (legacy != null) legacy.enabled = false;

            training?.SetTechnicalOverlayVisible(false);
            training?.SetSessionPaused(true, "formal_ui_ready");
            if (motionUI != null)
            {
                motionUI.SetFormalTrainingMode(true);
                motionUI.SetFormalConnectionPanelVisible(
                    motionCapture == null || motionCapture.State == null ||
                    !motionCapture.State.IsDriving);
            }

            runtimeFont = CreateRuntimeFont();
            BuildCanvas();
            LoadPhotoResources();
            CreateShutterAudio();
            RefreshAllVisuals();

            motionCapture?.LogGameDiagnostic(
                "s1_formal_ui_ready",
                "canvas=ready, raw_telemetry_visible=false, target=3");
        }

        private void LateUpdate()
        {
            if (training != null)
                snapshot = training.CurrentSnapshot;

            bool capture = focusSession.Update(snapshot);
            if (capture && session.State == S1TrainingRunState.Running)
                HandleCapture();

            UpdatePhotoTexture();
            UpdateFlash();
            AutoHideConnectionPanel();
            RefreshAllVisuals();
        }

        private void BuildCanvas()
        {
            GameObject root = new GameObject(
                "S1 Formal Training Canvas",
                typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.layer = 5;
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            BuildTopBar(root.transform);
            BuildCoachPanel(root.transform);
            BuildPhotoPanel(root.transform);
            BuildBottomPanel(root.transform);
            BuildModal(root.transform);

            flashImage = CreateImage(root.transform, "Shutter Flash",
                Vector2.zero, Vector2.one, Color.clear).GetComponent<Image>();
            flashImage.raycastTarget = false;
            flashImage.transform.SetAsLastSibling();
        }

        private void BuildTopBar(Transform parent)
        {
            Transform bar = CreateImage(parent, "Top Bar",
                new Vector2(0f, 0.915f), Vector2.one, DarkGreen).transform;

            CreateText(bar, "Title", "公园摄影站  ·  S1 坐姿伸膝",
                new Vector2(0.018f, 0.12f), new Vector2(0.37f, 0.88f),
                30, FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft);

            connectionText = CreateText(bar, "Connection Status", "等待设备",
                new Vector2(0.37f, 0.52f), new Vector2(0.69f, 0.90f),
                18, FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft);
            sensorText = CreateText(bar, "Sensor Status", "06  07  08  09",
                new Vector2(0.37f, 0.08f), new Vector2(0.69f, 0.48f),
                16, FontStyles.Normal, Color.white, TextAlignmentOptions.MidlineLeft);

            settingsButton = CreateButton(bar, "Device Settings", "设备设置",
                new Vector2(0.70f, 0.19f), new Vector2(0.79f, 0.81f),
                BrightGreen, Color.white);
            settingsButton.onClick.AddListener(ToggleConnectionPanel);

            pauseButton = CreateButton(bar, "Pause", "暂停",
                new Vector2(0.80f, 0.19f), new Vector2(0.89f, 0.81f),
                WarmWhite, DarkGreen);
            pauseButton.onClick.AddListener(TogglePause);

            endButton = CreateButton(bar, "End", "结束训练",
                new Vector2(0.90f, 0.19f), new Vector2(0.985f, 0.81f),
                new Color(0.78f, 0.35f, 0.31f, 1f), Color.white);
            endButton.onClick.AddListener(EndTraining);
        }

        private void BuildCoachPanel(Transform parent)
        {
            coachPanel = CreateImage(parent, "Coach Panel",
                new Vector2(0.015f, 0.255f), new Vector2(0.235f, 0.90f),
                new Color(WarmWhite.r, WarmWhite.g, WarmWhite.b, 0.96f));
            Transform panel = coachPanel.transform;

            CreateText(panel, "Coach Title", "教练示范",
                new Vector2(0.07f, 0.90f), new Vector2(0.93f, 0.985f),
                26, FontStyles.Bold, DarkGreen, TextAlignmentOptions.MidlineLeft);

            Transform placeholder = CreateImage(panel, "Coach Placeholder",
                new Vector2(0.07f, 0.48f), new Vector2(0.93f, 0.88f),
                PaleGreen).transform;
            CreateText(placeholder, "Coach Icon", "教练\n示范",
                new Vector2(0.22f, 0.22f), new Vector2(0.78f, 0.78f),
                42, FontStyles.Bold, Green, TextAlignmentOptions.Center);
            CreateText(placeholder, "Placeholder Note", "动作视频占位",
                new Vector2(0.08f, 0.04f), new Vector2(0.92f, 0.20f),
                17, FontStyles.Italic, Muted, TextAlignmentOptions.Center);

            coachInstructionText = CreateText(panel, "Coach Instructions",
                "1. 坐稳并让小腿自然下垂\n2. 缓慢抬起小腿\n3. 画面清晰后保持\n4. 快门后缓慢放下",
                new Vector2(0.07f, 0.18f), new Vector2(0.93f, 0.46f),
                19, FontStyles.Normal, Ink, TextAlignmentOptions.TopLeft);

            selectedLegText = CreateText(panel, "Selected Leg", "当前训练：左腿",
                new Vector2(0.07f, 0.115f), new Vector2(0.93f, 0.18f),
                19, FontStyles.Bold, DarkGreen, TextAlignmentOptions.MidlineLeft);

            leftLegButton = CreateButton(panel, "Left Leg", "左腿  06+07",
                new Vector2(0.07f, 0.035f), new Vector2(0.48f, 0.105f),
                Green, Color.white);
            rightLegButton = CreateButton(panel, "Right Leg", "右腿  08+09",
                new Vector2(0.52f, 0.035f), new Vector2(0.93f, 0.105f),
                PaleGreen, DarkGreen);
            leftLegButton.onClick.AddListener(() => SelectLeg(TrainingLeg.Left));
            rightLegButton.onClick.AddListener(() => SelectLeg(TrainingLeg.Right));
        }

        private void BuildPhotoPanel(Transform parent)
        {
            photoPanel = CreateImage(parent, "Photo Panel",
                new Vector2(0.65f, 0.255f), new Vector2(0.985f, 0.90f),
                new Color(WarmWhite.r, WarmWhite.g, WarmWhite.b, 0.97f));
            Transform panel = photoPanel.transform;

            CreateText(panel, "Photo Title", "远景对焦",
                new Vector2(0.04f, 0.91f), new Vector2(0.57f, 0.985f),
                26, FontStyles.Bold, DarkGreen, TextAlignmentOptions.MidlineLeft);
            photoFocusText = CreateText(panel, "Focus Value", "清晰度 0%",
                new Vector2(0.58f, 0.91f), new Vector2(0.96f, 0.985f),
                22, FontStyles.Bold, Green, TextAlignmentOptions.MidlineRight);

            photoImage = CreateRawImage(panel, "Distant Photo",
                new Vector2(0.04f, 0.35f), new Vector2(0.96f, 0.827f),
                Color.white);

            photoPromptText = CreateText(panel, "Photo Prompt",
                "开始训练后，抬腿会让照片逐渐清晰",
                new Vector2(0.04f, 0.24f), new Vector2(0.96f, 0.33f),
                19, FontStyles.Bold, Ink, TextAlignmentOptions.Center);

            CreateImage(panel, "Focus Track",
                new Vector2(0.04f, 0.18f), new Vector2(0.96f, 0.215f),
                new Color(0.76f, 0.84f, 0.78f, 1f));
            focusBarFill = CreateImage(panel, "Focus Fill",
                new Vector2(0.04f, 0.18f), new Vector2(0.04f, 0.215f),
                BrightGreen).GetComponent<Image>();

            resultCard = CreateImage(panel, "Single Result Card",
                new Vector2(0.14f, 0.40f), new Vector2(0.86f, 0.74f),
                new Color(DarkGreen.r, DarkGreen.g, DarkGreen.b, 0.93f));
            resultText = CreateText(resultCard.transform, "Result Text",
                "拍摄成功",
                new Vector2(0.06f, 0.08f), new Vector2(0.94f, 0.92f),
                28, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            resultCard.SetActive(false);
        }

        private void BuildBottomPanel(Transform parent)
        {
            trainingPanel = CreateImage(parent, "Training Panel",
                new Vector2(0.015f, 0.02f), new Vector2(0.985f, 0.23f),
                new Color(WarmWhite.r, WarmWhite.g, WarmWhite.b, 0.97f));
            Transform panel = trainingPanel.transform;

            mainInstructionText = CreateText(panel, "Main Instruction",
                "请先完成设备连接和站姿标定",
                new Vector2(0.025f, 0.57f), new Vector2(0.58f, 0.94f),
                27, FontStyles.Bold, DarkGreen, TextAlignmentOptions.MidlineLeft);

            CreateImage(panel, "Hold Track",
                new Vector2(0.025f, 0.37f), new Vector2(0.58f, 0.48f),
                new Color(0.76f, 0.84f, 0.78f, 1f));
            holdBarFill = CreateImage(panel, "Hold Fill",
                new Vector2(0.025f, 0.37f), new Vector2(0.025f, 0.48f),
                BrightGreen).GetComponent<Image>();

            stageText = CreateText(panel, "Stage", "● 等待开始",
                new Vector2(0.025f, 0.08f), new Vector2(0.58f, 0.32f),
                20, FontStyles.Bold, Green, TextAlignmentOptions.MidlineLeft);

            sessionCountText = CreateMetric(panel, "Session Count", "本组进度", "0 / 3",
                new Vector2(0.62f, 0.10f), new Vector2(0.79f, 0.90f));
            holdText = CreateMetric(panel, "Hold Time", "保持时间", "0.0 / 3 秒",
                new Vector2(0.81f, 0.10f), new Vector2(0.975f, 0.90f));
        }

        private void BuildModal(Transform parent)
        {
            modalOverlay = CreateImage(parent, "Session Modal Backdrop",
                Vector2.zero, Vector2.one, new Color(0f, 0f, 0f, 0.45f));
            Transform card = CreateImage(modalOverlay.transform, "Session Card",
                new Vector2(0.34f, 0.27f), new Vector2(0.66f, 0.76f),
                WarmWhite).transform;

            modalTitleText = CreateText(card, "Modal Title", "准备开始",
                new Vector2(0.08f, 0.78f), new Vector2(0.92f, 0.94f),
                32, FontStyles.Bold, DarkGreen, TextAlignmentOptions.Center);
            modalBodyText = CreateText(card, "Modal Body",
                "连接四个传感器并完成站姿标定后开始训练。",
                new Vector2(0.10f, 0.48f), new Vector2(0.90f, 0.76f),
                20, FontStyles.Normal, Ink, TextAlignmentOptions.Center);

            targetControls = new GameObject("Target Controls", typeof(RectTransform));
            targetControls.layer = 5;
            targetControls.transform.SetParent(card, false);
            SetRect((RectTransform)targetControls.transform,
                new Vector2(0.12f, 0.28f), new Vector2(0.88f, 0.47f));
            CreateText(targetControls.transform, "Target Label", "目标次数",
                new Vector2(0f, 0f), new Vector2(0.34f, 1f),
                20, FontStyles.Bold, Ink, TextAlignmentOptions.Center);
            targetMinusButton = CreateButton(targetControls.transform, "Target Minus", "－",
                new Vector2(0.36f, 0.10f), new Vector2(0.51f, 0.90f),
                PaleGreen, DarkGreen);
            targetValueText = CreateText(targetControls.transform, "Target Value", "3 次",
                new Vector2(0.52f, 0f), new Vector2(0.71f, 1f),
                24, FontStyles.Bold, Green, TextAlignmentOptions.Center);
            targetPlusButton = CreateButton(targetControls.transform, "Target Plus", "＋",
                new Vector2(0.72f, 0.10f), new Vector2(0.87f, 0.90f),
                PaleGreen, DarkGreen);
            targetMinusButton.onClick.AddListener(() => ChangeTarget(-1));
            targetPlusButton.onClick.AddListener(() => ChangeTarget(1));

            modalPrimaryButton = CreateButton(card, "Modal Primary", "开始训练",
                new Vector2(0.12f, 0.08f), new Vector2(0.58f, 0.23f),
                Green, Color.white);
            modalSecondaryButton = CreateButton(card, "Modal Secondary", "结束训练",
                new Vector2(0.62f, 0.08f), new Vector2(0.88f, 0.23f),
                PaleGreen, DarkGreen);
            modalPrimaryButton.onClick.AddListener(HandleModalPrimary);
            modalSecondaryButton.onClick.AddListener(HandleModalSecondary);
        }

        private void StartTraining()
        {
            if (!MotionReady() && motionUI != null)
                motionUI.TryBeginDrivingFromFormalUI();
            if (!MotionReady())
            {
                motionUI?.SetFormalConnectionPanelVisible(true);
                modalBodyText.text = "请先连接四个传感器，保持站姿完成标定并开始动捕。";
                return;
            }

            session.Start();
            focusSession.Reset();
            resultCard.SetActive(false);
            resultEndsAt = 0f;
            training?.SetSessionPaused(false, "s1_session_start");
            motionUI?.SetFormalConnectionPanelVisible(false);
            connectionPanelAutoHidden = true;
            motionCapture?.LogGameDiagnostic(
                "s1_session_start",
                $"target={session.TargetRepetitions}, leg={LegName(snapshot.Leg)}");
        }

        private void PauseTraining()
        {
            if (!session.Pause()) return;
            training?.SetSessionPaused(true, "s1_pause_button");
            motionCapture?.LogGameDiagnostic(
                "s1_session_pause",
                $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
        }

        private void ResumeTraining()
        {
            if (!session.Resume()) return;
            focusSession.Reset();
            training?.SetSessionPaused(false, "s1_resume_button");
            motionCapture?.LogGameDiagnostic(
                "s1_session_resume",
                $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
        }

        private void EndTraining()
        {
            if (!session.End()) return;
            training?.SetSessionPaused(true, "s1_end_button");
            resultCard.SetActive(false);
            motionCapture?.LogGameDiagnostic(
                "s1_session_end",
                $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
        }

        private void HandleCapture()
        {
            bool completed = session.RecordCapture();
            flashEndsAt = Time.unscaledTime + 0.20f;
            resultEndsAt = Time.unscaledTime + 2.5f;
            resultCard.SetActive(true);
            resultText.text = $"拍摄成功\n第 {session.CompletedRepetitions} 张照片";
            if (audioSource != null && shutterClip != null)
                audioSource.PlayOneShot(shutterClip);

            motionCapture?.LogGameDiagnostic(
                "photo_capture",
                JsonUtility.ToJson(new FormalCaptureDiagnostic
                {
                    leg = snapshot.Leg.ToString(),
                    sessionCompleted = session.CompletedRepetitions,
                    sessionTarget = session.TargetRepetitions,
                    kneeAngleDeg = snapshot.KneeAngleDeg,
                    targetKneeDeg = snapshot.TargetKneeDeg,
                    holdSeconds = snapshot.HoldSeconds,
                    focus01 = focusSession.Focus01
                }));

            if (!completed) return;
            training?.SetSessionPaused(true, "s1_target_complete");
            motionCapture?.LogGameDiagnostic(
                "s1_session_complete",
                $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
        }

        private void TogglePause()
        {
            if (session.State == S1TrainingRunState.Running)
                PauseTraining();
            else if (session.State == S1TrainingRunState.Paused)
                ResumeTraining();
        }

        private void HandleModalPrimary()
        {
            switch (session.State)
            {
                case S1TrainingRunState.Idle:
                    StartTraining();
                    break;
                case S1TrainingRunState.Paused:
                    ResumeTraining();
                    break;
                case S1TrainingRunState.Completed:
                    session.ReturnToSetup();
                    StartTraining();
                    break;
                case S1TrainingRunState.Ended:
                    session.ReturnToSetup();
                    focusSession.Reset();
                    break;
            }
        }

        private void HandleModalSecondary()
        {
            if (session.State == S1TrainingRunState.Paused ||
                session.State == S1TrainingRunState.Completed)
                EndTraining();
        }

        private void ChangeTarget(int delta)
        {
            session.SetTarget(session.TargetRepetitions + delta);
        }

        private void SelectLeg(TrainingLeg leg)
        {
            training?.SelectTrainingLeg(leg, "s1_canvas_button");
            focusSession.Reset();
            resultCard.SetActive(false);
        }

        private void ToggleConnectionPanel()
        {
            if (motionUI == null) return;
            motionUI.SetFormalConnectionPanelVisible(
                !motionUI.IsFormalConnectionPanelVisible);
            connectionPanelAutoHidden = motionUI.IsFormalConnectionPanelVisible;
        }

        private void AutoHideConnectionPanel()
        {
            if (connectionPanelAutoHidden || motionUI == null || !MotionReady()) return;
            motionUI.SetFormalConnectionPanelVisible(false);
            connectionPanelAutoHidden = true;
        }

        private void RefreshAllVisuals()
        {
            if (canvas == null) return;
            bool showTraining = MotionReady();
            coachPanel.SetActive(showTraining);
            photoPanel.SetActive(showTraining);
            trainingPanel.SetActive(showTraining);
            RefreshTopBar();
            if (showTraining)
            {
                RefreshTrainingPanel();
                RefreshPhotoPanel();
            }
            RefreshModal();
            RefreshLegButtons();
        }

        private void RefreshTopBar()
        {
            MotionCaptureState state = motionCapture != null ? motionCapture.State : null;
            if (state == null)
            {
                connectionText.text = "设备状态：等待系统初始化";
                sensorText.text = "06 ○   07 ○   08 ○   09 ○";
            }
            else
            {
                string status = !state.IsConnected ? "未连接" :
                    !state.HasAnyData ? "等待传感器数据" :
                    !state.IsCalibrated ? "正在准备标定" :
                    !state.IsDriving ? "等待开始动捕" :
                    HasStaleLowerBodySensor(state) ? "传感器信号异常" : "动捕已就绪";
                connectionText.text = "设备状态：" + status;
                sensorText.text = "传感器：" + DeviceChip(state, 5, "06") + "   " +
                                  DeviceChip(state, 6, "07") + "   " +
                                  DeviceChip(state, 7, "08") + "   " +
                                  DeviceChip(state, 8, "09");
            }

            bool active = session.State == S1TrainingRunState.Running ||
                          session.State == S1TrainingRunState.Paused;
            pauseButton.gameObject.SetActive(active);
            endButton.gameObject.SetActive(active);
            SetButtonLabel(pauseButton,
                session.State == S1TrainingRunState.Paused ? "继续" : "暂停");
        }

        private void RefreshTrainingPanel()
        {
            float holdRatio = snapshot.HoldDurationSeconds > 0f
                ? Mathf.Clamp01(snapshot.HoldSeconds / snapshot.HoldDurationSeconds)
                : 0f;
            SetFill(holdBarFill, holdRatio, 0.025f, 0.58f);

            sessionCountText.text =
                $"<size=18><color=#64776D>本组进度</color></size>\n" +
                $"<size=34><b>{session.CompletedRepetitions} / {session.TargetRepetitions}</b></size>";
            holdText.text =
                $"<size=18><color=#64776D>保持时间</color></size>\n" +
                $"<size=30><b>{snapshot.HoldSeconds:F1} / " +
                $"{Mathf.Max(3f, snapshot.HoldDurationSeconds):F0} 秒</b></size>";

            mainInstructionText.text = FriendlyInstruction();
            stageText.text = StageCaption();
            selectedLegText.text = "当前训练：" + LegName(snapshot.Leg) + "腿";
            coachInstructionText.text = snapshot.Leg == TrainingLeg.Right
                ? "1. 坐稳，右小腿自然下垂\n2. 缓慢抬起右小腿\n3. 画面清晰后保持\n4. 快门后缓慢放下"
                : "1. 坐稳，左小腿自然下垂\n2. 缓慢抬起左小腿\n3. 画面清晰后保持\n4. 快门后缓慢放下";
        }

        private void RefreshPhotoPanel()
        {
            float focus = focusSession.Focus01;
            photoFocusText.text = $"清晰度 {focus * 100f:F0}%";
            SetFill(focusBarFill, focus, 0.04f, 0.96f);
            photoPromptText.text = PhotoPrompt();

            if (resultCard.activeSelf &&
                session.State != S1TrainingRunState.Completed &&
                Time.unscaledTime >= resultEndsAt)
                resultCard.SetActive(false);
        }

        private void RefreshModal()
        {
            S1TrainingRunState state = session.State;
            bool show = state != S1TrainingRunState.Running;
            modalOverlay.SetActive(show);
            if (!show) return;

            targetControls.SetActive(state == S1TrainingRunState.Idle);
            targetValueText.text = session.TargetRepetitions + " 次";
            targetMinusButton.interactable =
                session.TargetRepetitions > S1TrainingSession.MinimumTarget;
            targetPlusButton.interactable =
                session.TargetRepetitions < S1TrainingSession.MaximumTarget;

            switch (state)
            {
                case S1TrainingRunState.Idle:
                    modalTitleText.text = "准备开始训练";
                    modalBodyText.text = CanStartTraining()
                        ? "请选择目标次数。训练时照片会随伸膝动作逐渐变清晰。"
                        : "请先打开“设备设置”，连接四个传感器并完成站姿标定。";
                    SetButtonLabel(modalPrimaryButton, "开始训练");
                    modalPrimaryButton.interactable = CanStartTraining();
                    modalSecondaryButton.gameObject.SetActive(false);
                    break;

                case S1TrainingRunState.Paused:
                    modalTitleText.text = "训练已暂停";
                    modalBodyText.text =
                        $"已经拍摄 {session.CompletedRepetitions} / " +
                        $"{session.TargetRepetitions} 张。可以先放松，继续时请让小腿自然下垂。";
                    SetButtonLabel(modalPrimaryButton, "继续训练");
                    modalPrimaryButton.interactable = MotionReady();
                    modalSecondaryButton.gameObject.SetActive(true);
                    SetButtonLabel(modalSecondaryButton, "结束训练");
                    break;

                case S1TrainingRunState.Completed:
                    modalTitleText.text = "本组训练完成";
                    modalBodyText.text =
                        $"已完成 {session.CompletedRepetitions} 次远景拍摄。" +
                        "请慢慢放下小腿并休息一下。";
                    SetButtonLabel(modalPrimaryButton, "再练一组");
                    modalPrimaryButton.interactable = MotionReady();
                    modalSecondaryButton.gameObject.SetActive(true);
                    SetButtonLabel(modalSecondaryButton, "结束训练");
                    break;

                default:
                    modalTitleText.text = "训练已结束";
                    modalBodyText.text =
                        $"本次共拍摄 {session.CompletedRepetitions} 张照片。" +
                        "感谢完成本次训练。";
                    SetButtonLabel(modalPrimaryButton, "返回设置");
                    modalPrimaryButton.interactable = true;
                    modalSecondaryButton.gameObject.SetActive(false);
                    break;
            }
        }

        private void RefreshLegButtons()
        {
            bool right = snapshot.Leg == TrainingLeg.Right;
            StyleSelection(leftLegButton, !right);
            StyleSelection(rightLegButton, right);
        }

        private string FriendlyInstruction()
        {
            if (!MotionReady()) return "请先连接设备并完成站姿标定";
            switch (session.State)
            {
                case S1TrainingRunState.Idle: return "设置目标次数，然后开始训练";
                case S1TrainingRunState.Paused: return "训练已暂停，可以先放松";
                case S1TrainingRunState.Completed: return "本组完成，请慢慢放下小腿";
                case S1TrainingRunState.Ended: return "训练已结束";
            }

            if (snapshot.IsSwitchingLeg)
                return $"请坐稳，让{LegName(snapshot.Leg)}小腿自然下垂";
            if (!snapshot.IsDataValid)
                return FriendlyBlockReason(snapshot.BlockReason);

            switch (snapshot.Stage)
            {
                case KneeExtensionStage.Preparing:
                    return $"坐稳，让{LegName(snapshot.Leg)}小腿自然下垂";
                case KneeExtensionStage.Extending:
                    return $"慢慢抬起{LegName(snapshot.Leg)}小腿，让照片逐渐清晰";
                case KneeExtensionStage.Holding:
                    return "很好，请保持当前姿势，等待快门";
                default:
                    return "拍摄完成，请慢慢放下小腿";
            }
        }

        private string StageCaption()
        {
            switch (session.State)
            {
                case S1TrainingRunState.Idle: return "● 等待开始";
                case S1TrainingRunState.Paused: return "● 已暂停";
                case S1TrainingRunState.Completed: return "● 本组完成";
                case S1TrainingRunState.Ended: return "● 已结束";
            }
            if (snapshot.IsSwitchingLeg) return "● 正在确认准备姿势";
            switch (snapshot.Stage)
            {
                case KneeExtensionStage.Preparing: return "● 准备：小腿自然下垂";
                case KneeExtensionStage.Extending: return "● 对焦：缓慢抬起小腿";
                case KneeExtensionStage.Holding: return "● 保持：稳定后自动拍摄";
                default: return "● 返回：缓慢放下小腿";
            }
        }

        private string PhotoPrompt()
        {
            if (session.State == S1TrainingRunState.Idle)
                return "开始训练后，抬腿会让照片逐渐清晰";
            if (session.State == S1TrainingRunState.Paused)
                return "训练暂停，当前对焦进度不会继续";
            if (session.State == S1TrainingRunState.Completed)
                return "本组拍摄完成";
            if (session.State == S1TrainingRunState.Ended)
                return "训练已结束";
            if (!snapshot.IsDataValid)
                return "信号暂时中断，对焦进度已冻结";
            if (snapshot.Stage == KneeExtensionStage.Holding)
                return "对焦完成，保持姿势等待快门";
            if (snapshot.Stage == KneeExtensionStage.Returning)
                return "照片已拍好，请慢慢放下小腿";
            return "缓慢伸膝，让远景逐渐清晰";
        }

        private static string FriendlyBlockReason(string reason)
        {
            string value = reason ?? "";
            if (value.Contains("信号") || value.Contains("超时") ||
                value.Contains("时间差") || value.Contains("未持续更新"))
                return "传感器信号暂时中断，请放松等待恢复";
            if (value.Contains("标定") || value.Contains("驱动") ||
                value.Contains("串口"))
                return "请先完成设备连接和站姿标定";
            if (value.Contains("坐稳") || value.Contains("坐位") ||
                value.Contains("大腿"))
                return "请坐稳，让大腿保持自然水平";
            return "请坐稳，让小腿自然下垂后重新准备";
        }

        private void LoadPhotoResources()
        {
            sourcePhoto = Resources.Load<Texture2D>(PhotoResource);
            Shader shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null)
                shader = Shader.Find("Hidden/RehabPhotoGame/FocusBlur");
            if (shader != null && shader.isSupported)
                blurMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            if (sourcePhoto == null)
            {
                photoPromptText.text = "远景素材未加载，请联系工作人员";
                return;
            }

            focusedPhoto = new RenderTexture(640, 360, 0, RenderTextureFormat.ARGB32)
            {
                name = "S1 Formal Photo Preview",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            focusedPhoto.Create();
            photoImage.texture = focusedPhoto;
        }

        private void UpdatePhotoTexture()
        {
            if (sourcePhoto == null || focusedPhoto == null) return;
            if (blurMaterial == null)
            {
                Graphics.Blit(sourcePhoto, focusedPhoto);
                return;
            }
            blurMaterial.SetFloat(
                "_BlurPixels", Mathf.Lerp(18f, 0f, focusSession.Focus01));
            Graphics.Blit(sourcePhoto, focusedPhoto, blurMaterial);
        }

        private void CreateShutterAudio()
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.volume = 0.78f;
            shutterClip = ShutterSound.CreateClip();
        }

        private void UpdateFlash()
        {
            if (flashImage == null) return;
            float remaining = flashEndsAt - Time.unscaledTime;
            float alpha = remaining > 0f
                ? Mathf.Clamp01(remaining / 0.20f) * 0.82f
                : 0f;
            flashImage.color = new Color(1f, 1f, 1f, alpha);
        }

        private bool MotionReady()
        {
            MotionCaptureState state = motionCapture != null ? motionCapture.State : null;
            return state != null && state.IsConnected && state.IsCalibrated && state.IsDriving;
        }

        private bool CanStartTraining()
        {
            return MotionReady() ||
                   (motionUI != null && motionUI.CanBeginDrivingFromFormalUI);
        }

        private string DeviceChip(MotionCaptureState state, int index, string label)
        {
            if (state == null || !state.GetDeviceHasData(index) || motionCapture == null)
                return $"<color=#C7D1CB>{label} ○</color>";

            float age = motionCapture.GetDeviceFrameAgeSeconds(index);
            if (age < 0f)
                return $"<color=#C7D1CB>{label} ○</color>";

            float timeout = training != null ? training.SensorTimeoutSeconds : 2.2f;
            float slowThreshold = Mathf.Max(1f, timeout * 0.6f);
            if (age <= slowThreshold)
                return $"<color=#55A96F>{label} ●</color>";
            if (age <= timeout)
                return $"<color=#D9A441>{label} ●</color>";
            return $"<color=#D45B50>{label} ○</color>";
        }

        private bool HasStaleLowerBodySensor(MotionCaptureState state)
        {
            if (state == null || motionCapture == null) return true;
            float timeout = training != null ? training.SensorTimeoutSeconds : 2.2f;
            for (int index = LowerBodyPoseDriver.LeftThighIndex;
                 index <= LowerBodyPoseDriver.RightCalfIndex;
                 index++)
            {
                float age = motionCapture.GetDeviceFrameAgeSeconds(index);
                if (!state.GetDeviceHasData(index) || age < 0f || age > timeout)
                    return true;
            }
            return false;
        }

        private static string LegName(TrainingLeg leg) =>
            leg == TrainingLeg.Right ? "右" : "左";

        private void SetFill(Image image, float ratio, float start, float end)
        {
            if (image == null) return;
            RectTransform rect = image.rectTransform;
            Vector2 maximum = rect.anchorMax;
            maximum.x = Mathf.Lerp(start, end, Mathf.Clamp01(ratio));
            rect.anchorMax = maximum;
        }

        private void StyleSelection(Button button, bool selected)
        {
            if (button == null) return;
            Image image = button.GetComponent<Image>();
            if (image != null) image.color = selected ? Green : PaleGreen;
            TMP_Text label = button.GetComponentInChildren<TMP_Text>();
            if (label != null) label.color = selected ? Color.white : DarkGreen;
        }

        private static void SetButtonLabel(Button button, string value)
        {
            if (button == null) return;
            TMP_Text label = button.GetComponentInChildren<TMP_Text>();
            if (label != null) label.text = value;
        }

        private TMP_FontAsset CreateRuntimeFont()
        {
            Font bundledFont = Resources.Load<Font>(BundledFontResource);
            TMP_FontAsset bundledAsset = CreateDynamicFontAsset(
                bundledFont, "S1 Bundled Noto Sans SC");
            if (bundledAsset != null)
                return bundledAsset;

            Debug.LogError(
                $"[S1 UI] 项目内置中文字体加载失败：Resources/{BundledFontResource}。" +
                "将尝试系统字体，但客户电脑上可能显示方框。");

            string[] candidates =
            {
                "Microsoft YaHei UI", "Microsoft YaHei",
                "SimHei", "Arial Unicode MS", "Arial"
            };
            sourceSystemFont = Font.CreateDynamicFontFromOSFont(candidates, 32);
            if (sourceSystemFont != null)
            {
                sourceSystemFont.hideFlags = HideFlags.HideAndDontSave;
                TMP_FontAsset systemAsset = CreateDynamicFontAsset(
                    sourceSystemFont, "S1 System Font Fallback");
                if (systemAsset != null)
                    return systemAsset;
            }
            return TMP_Settings.defaultFontAsset;
        }

        private TMP_FontAsset CreateDynamicFontAsset(Font sourceFont, string assetName)
        {
            if (sourceFont == null)
                return null;

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(sourceFont);
            if (asset == null)
                return null;

            asset.name = assetName;
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            asset.isMultiAtlasTexturesEnabled = true;

            if (!asset.TryAddCharacters(RequiredChineseGlyphs, out string missingCharacters))
            {
                Debug.LogError(
                    $"[S1 UI] 字体 {sourceFont.name} 缺少中文字符：{missingCharacters}");
                Destroy(asset);
                return null;
            }

            ownsRuntimeFont = true;
            return asset;
        }

        private GameObject CreateImage(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            GameObject node = new GameObject(
                name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            node.layer = 5;
            node.transform.SetParent(parent, false);
            SetRect(node.GetComponent<RectTransform>(), anchorMin, anchorMax);
            Image image = node.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return node;
        }

        private RawImage CreateRawImage(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            GameObject node = new GameObject(
                name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            node.layer = 5;
            node.transform.SetParent(parent, false);
            SetRect(node.GetComponent<RectTransform>(), anchorMin, anchorMax);
            RawImage image = node.GetComponent<RawImage>();
            image.color = color;
            image.raycastTarget = false;
            image.uvRect = new Rect(0f, 0f, 1f, 1f);
            return image;
        }

        private TMP_Text CreateText(Transform parent, string name, string value,
            Vector2 anchorMin, Vector2 anchorMax, float size,
            FontStyles style, Color color, TextAlignmentOptions alignment)
        {
            GameObject node = new GameObject(
                name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            node.layer = 5;
            node.transform.SetParent(parent, false);
            SetRect(node.GetComponent<RectTransform>(), anchorMin, anchorMax);
            TextMeshProUGUI text = node.GetComponent<TextMeshProUGUI>();
            text.text = value;
            text.font = runtimeFont;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }

        private Button CreateButton(Transform parent, string name, string label,
            Vector2 anchorMin, Vector2 anchorMax, Color background, Color foreground)
        {
            GameObject node = new GameObject(
                name, typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(Button));
            node.layer = 5;
            node.transform.SetParent(parent, false);
            SetRect(node.GetComponent<RectTransform>(), anchorMin, anchorMax);
            Image image = node.GetComponent<Image>();
            image.color = background;
            Button button = node.GetComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.94f, 1f, 0.95f, 1f);
            colors.pressedColor = new Color(0.80f, 0.90f, 0.82f, 1f);
            colors.disabledColor = new Color(0.78f, 0.82f, 0.79f, 0.65f);
            button.colors = colors;

            CreateText(node.transform, "Label", label,
                new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f),
                18, FontStyles.Bold, foreground, TextAlignmentOptions.Center);
            return button;
        }

        private TMP_Text CreateMetric(Transform parent, string name,
            string title, string value, Vector2 anchorMin, Vector2 anchorMax)
        {
            GameObject panel = CreateImage(parent, name, anchorMin, anchorMax,
                new Color(PaleGreen.r, PaleGreen.g, PaleGreen.b, 0.58f));
            CreateImage(panel.transform, "Accent",
                new Vector2(0f, 0f), new Vector2(0.025f, 1f), Green);
            return CreateText(panel.transform, "Value",
                $"<size=18><color=#64776D>{title}</color></size>\n" +
                $"<size=32><b>{value}</b></size>",
                new Vector2(0.08f, 0.08f), new Vector2(0.94f, 0.92f),
                24, FontStyles.Normal, Green, TextAlignmentOptions.Center);
        }

        private static void SetRect(RectTransform rect,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        private static Color Hex(string value)
        {
            return ColorUtility.TryParseHtmlString("#" + value, out Color color)
                ? color
                : Color.white;
        }

        private void OnDestroy()
        {
            if (motionUI != null)
                motionUI.SetFormalTrainingMode(false);
            if (training != null)
                training.SetTechnicalOverlayVisible(true);

            if (focusedPhoto != null)
            {
                focusedPhoto.Release();
                Destroy(focusedPhoto);
            }
            if (blurMaterial != null) Destroy(blurMaterial);
            if (shutterClip != null) Destroy(shutterClip);
            if (ownsRuntimeFont && runtimeFont != null) Destroy(runtimeFont);
            if (sourceSystemFont != null) Destroy(sourceSystemFont);
        }

        [Serializable]
        private sealed class FormalCaptureDiagnostic
        {
            public string leg;
            public int sessionCompleted, sessionTarget;
            public float kneeAngleDeg, targetKneeDeg, holdSeconds, focus01;
        }
    }
}
