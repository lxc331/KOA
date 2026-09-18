using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RehabPhotoGame
{
    /// <summary>
    /// 正式摄影训练界面。消费坐姿伸膝、坐站或浅蹲快照，
    /// 不读取或展示四元数、欧拉角及串口帧。
    /// </summary>
    [DefaultExecutionOrder(1200)]
    [DisallowMultipleComponent]
    public sealed partial class S1TrainingCanvasController : MonoBehaviour
    {
        private const string PhotoResource =
            "RehabPhotoGame/Photos/A_Distant/spring_01";
        private static readonly string[] HighPhotoResources =
        {
            "RehabPhotoGame/Photos/B_High/pavilion_01",
            "RehabPhotoGame/Photos/B_High/birds_01",
            "RehabPhotoGame/Photos/B_High/lanterns_01"
        };
        private static readonly string[] LowPhotoResources =
        {
            "RehabPhotoGame/Photos/C_Low/grass_mushrooms_01",
            "RehabPhotoGame/Photos/C_Low/butterflies_01",
            "RehabPhotoGame/Photos/C_Low/lotus_01"
        };
        private const string ShaderResource =
            "RehabPhotoGame/Shaders/FocusBlur";
        private const string BundledFontResource =
            "RehabPhotoGame/Fonts/NotoSansSC-Regular";
        private const string RequiredChineseGlyphs =
            "中文公园摄影站坐姿伸膝等待设备连接开始训练暂停继续结束左右腿教练示范目标次数完成保持缓慢抬起放下照片清晰坐站高处低处浅蹲双脚着地同步起身屈曲回取景高度模式选择" +
            "信号异常冻结波动自动恢复回位正在检查传感器佩戴持续中断站直站稳稳定准备坐回已按屏幕提示" +
            "组合练习步骤顺序重新本组调整下一组不用着急可以自己的节奏远景高处低处放下小腿完成返回保存" +
            "请选择本次内容仅连续已选择再训练拍得太高了接下来坐位";

        // 第三段客户验收界面采用深色认知训练风格；动作算法不依赖这些颜色。
        private static readonly Color DarkGreen = Hex("081A2A");
        private static readonly Color Green = Hex("2A9A62");
        private static readonly Color BrightGreen = Hex("8ED45A");
        private static readonly Color PaleGreen = Hex("DCE9F0");
        private static readonly Color WarmWhite = Hex("F1F6F8");
        private static readonly Color Ink = Hex("EAF2F6");
        private static readonly Color Muted = Hex("9DB0BD");
        private static readonly Color PanelNavy = Hex("102B42");
        private static readonly Color PanelNavyLight = Hex("173851");
        private static readonly Color TrackNavy = Hex("24455C");
        private static readonly Color Warning = Hex("D49742");

        private readonly S1TrainingSession session = new S1TrainingSession();
        private readonly PhotoFocusSession focusSession = new PhotoFocusSession();

        private SeatedKneeExtensionRuntimeFix training;
        private SitToStandRuntime sitToStandTraining;
        private ShallowSquatRuntime shallowSquatTraining;
        private MotionCaptureController motionCapture;
        private MotionCaptureUI motionUI;
        private KneeExtensionTrainingSnapshot snapshot;
        private SitToStandTrainingSnapshot sitToStandSnapshot;
        private ShallowSquatTrainingSnapshot shallowSquatSnapshot;
        private PhotoTrainingMode trainingMode = PhotoTrainingMode.SeatedKneeExtension;

        private Canvas canvas;
        private TMP_FontAsset runtimeFont;
        private Font sourceSystemFont;
        private bool ownsRuntimeFont;

        private TMP_Text connectionText;
        private TMP_Text sensorText;
        private TMP_Text titleText;
        private TMP_Text photoTitleText;
        private TMP_Text selectedLegText;
        private TMP_Text coachInstructionText;
        private TMP_Text photoFocusText;
        private TMP_Text photoPromptText;
        private TMP_Text mainInstructionText;
        private TMP_Text stageText;
        private TMP_Text sessionCountText;
        private TMP_Text holdText;
        private TMP_Text angleMetricText;
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
        private GameObject photoViewport;
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
        private Button kneeModeButton;
        private Button sitToStandModeButton;
        private Button shallowSquatModeButton;

        private Texture2D sourcePhoto;
        private Texture2D distantPhoto;
        private Texture2D[] highPhotos;
        private Texture2D[] lowPhotos;
        private RenderTexture focusedPhoto;
        private Material blurMaterial;
        private AudioSource audioSource;
        private AudioClip shutterClip;
        private float flashEndsAt;
        private float resultEndsAt;
        private bool connectionPanelAutoHidden;
        private int lastSitToStandCaptureSequence;
        private int lastShallowSquatCaptureSequence;
        private Vector2 photoBaseAnchoredPosition;
        private const float PhotoContentOverscan = 0.14f;
        private const float PhotoScrollRange = 0.22f;

        private void Start()
        {
            training = GetComponent<SeatedKneeExtensionRuntimeFix>();
            if (training == null)
                training = FindObjectOfType<SeatedKneeExtensionRuntimeFix>();
            sitToStandTraining = GetComponent<SitToStandRuntime>();
            if (sitToStandTraining == null)
                sitToStandTraining = FindObjectOfType<SitToStandRuntime>();
            shallowSquatTraining = GetComponent<ShallowSquatRuntime>();
            if (shallowSquatTraining == null)
                shallowSquatTraining = FindObjectOfType<ShallowSquatRuntime>();
            motionCapture = FindObjectOfType<MotionCaptureController>();
            motionUI = motionCapture != null
                ? motionCapture.GetComponent<MotionCaptureUI>()
                : FindObjectOfType<MotionCaptureUI>();

            PhotoFocusFeedback legacy = GetComponent<PhotoFocusFeedback>();
            if (legacy != null) legacy.enabled = false;

            training?.SetTechnicalOverlayVisible(false);
            training?.SetTrainingModeActive(true, "formal_ui_ready");
            training?.SetSessionPaused(true, "formal_ui_ready");
            sitToStandTraining?.SetModeActive(false, "formal_ui_ready");
            shallowSquatTraining?.SetModeActive(false, "formal_ui_ready");
            if (motionUI != null)
            {
                motionUI.SetFormalTrainingMode(true);
                motionUI.SetFormalConnectionPanelVisible(
                    motionCapture == null || motionCapture.State == null ||
                    !motionCapture.State.IsDriving);
            }

            runtimeFont = CreateRuntimeFont();
            InitializeStageFive();
            BuildCanvas();
            LoadPhotoResources();
            CreateShutterAudio();
            RefreshAllVisuals();

            motionCapture?.LogGameDiagnostic(
                "s1_formal_ui_ready",
                "canvas=ready, raw_telemetry_visible=false, target=3");
            motionCapture?.LogTrainingDiagnostic(
                "sit_to_stand", "stage3_ui_ready",
                $"highPhotos={AvailableHighPhotoCount()}, defaultMode={trainingMode}");
            motionCapture?.LogTrainingDiagnostic(
                "shallow_squat", "stage4_ui_ready",
                $"lowPhotos={AvailableLowPhotoCount()}, defaultMode={trainingMode}");
        }

        private void LateUpdate()
        {
            UpdateStageFiveFlow();
            if (FreezeGameFeedback)
            {
                UpdateFlash();
                RefreshTopBar();
                RefreshModal();
                RefreshFlowControls();
                return;
            }
            if (s2Selected && combination.IsRunning && flow.State == TrainingFlowState.SignalRecovery)
            {
                ReadS2Snapshots();
                combination.Observe(CurrentS2Frame(), false, flow.Result.active_seconds);
                UpdateFlash();
                RefreshTopBar();
                RefreshModal();
                RefreshFlowControls();
                return;
            }
            if (training != null)
                snapshot = training.CurrentSnapshot;
            if (sitToStandTraining != null)
                sitToStandSnapshot = sitToStandTraining.CurrentSnapshot;
            if (shallowSquatTraining != null)
                shallowSquatSnapshot = shallowSquatTraining.CurrentSnapshot;
            if (s2Selected && combination.IsRunning)
            {
                UpdateS2Combination();
                UpdatePhotoTexture();
                UpdateFlash();
                AutoHideConnectionPanel();
                RefreshAllVisuals();
                RefreshFlowControls();
                return;
            }
            UpdateReturnPreparation();

            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
            {
                bool capture = focusSession.Update(snapshot);
                if (capture && session.State == S1TrainingRunState.Running)
                    HandleCapture();
            }
            else if (trainingMode == PhotoTrainingMode.SitToStand)
                DetectSitToStandCapture();
            else
                DetectShallowSquatCapture();

            UpdatePhotoTexture();
            UpdateFlash();
            AutoHideConnectionPanel();
            RefreshAllVisuals();
            RefreshFlowControls();
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
            BuildFlowControls(root.transform);
            BuildS2Controls(root.transform);
        }

        private void BuildTopBar(Transform parent)
        {
            Transform bar = CreateImage(parent, "Top Bar",
                new Vector2(0f, 0.935f), Vector2.one, DarkGreen).transform;

            titleText = CreateText(bar, "Title", "公园摄影站  ·  认知阶段 · 单动作学习",
                new Vector2(0.018f, 0.10f), new Vector2(0.42f, 0.90f),
                26, FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft);

            connectionText = CreateText(bar, "Connection Status", "等待设备",
                new Vector2(0.42f, 0.53f), new Vector2(0.68f, 0.92f),
                16, FontStyles.Bold, BrightGreen, TextAlignmentOptions.MidlineLeft);
            sensorText = CreateText(bar, "Sensor Status", "06  07  08  09",
                new Vector2(0.42f, 0.08f), new Vector2(0.68f, 0.50f),
                14, FontStyles.Normal, Muted, TextAlignmentOptions.MidlineLeft);

            settingsButton = CreateButton(bar, "Device Settings", "设备设置",
                new Vector2(0.70f, 0.16f), new Vector2(0.79f, 0.84f),
                PanelNavyLight, Color.white);
            settingsButton.onClick.AddListener(ToggleConnectionPanel);

            pauseButton = CreateButton(bar, "Pause", "暂停",
                new Vector2(0.80f, 0.16f), new Vector2(0.89f, 0.84f),
                PanelNavyLight, Color.white);
            pauseButton.onClick.AddListener(TogglePause);

            endButton = CreateButton(bar, "End", "结束训练",
                new Vector2(0.90f, 0.16f), new Vector2(0.985f, 0.84f),
                new Color(0.70f, 0.23f, 0.24f, 1f), Color.white);
            endButton.onClick.AddListener(EndTraining);
        }

        private void BuildCoachPanel(Transform parent)
        {
            coachPanel = CreateImage(parent, "Coach Panel",
                new Vector2(0.015f, 0.255f), new Vector2(0.235f, 0.90f),
                new Color(PanelNavy.r, PanelNavy.g, PanelNavy.b, 0.97f));
            Transform panel = coachPanel.transform;

            CreateText(panel, "Coach Title", "教练示范",
                new Vector2(0.07f, 0.90f), new Vector2(0.93f, 0.985f),
                22, FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft);

            Transform placeholder = CreateImage(panel, "Coach Placeholder",
                new Vector2(0.07f, 0.48f), new Vector2(0.93f, 0.88f),
                PanelNavyLight).transform;
            CreateText(placeholder, "Coach Icon", "教练\n示范",
                new Vector2(0.22f, 0.22f), new Vector2(0.78f, 0.78f),
                42, FontStyles.Bold, Green, TextAlignmentOptions.Center);
            CreateText(placeholder, "Placeholder Note", "动作视频占位",
                new Vector2(0.08f, 0.04f), new Vector2(0.92f, 0.20f),
                15, FontStyles.Italic, Muted, TextAlignmentOptions.Center);

            coachInstructionText = CreateText(panel, "Coach Instructions",
                "1. 坐稳并让小腿自然下垂\n2. 缓慢抬起小腿\n3. 画面清晰后保持\n4. 快门后缓慢放下",
                new Vector2(0.07f, 0.18f), new Vector2(0.93f, 0.46f),
                16, FontStyles.Normal, Ink, TextAlignmentOptions.TopLeft);

            selectedLegText = CreateText(panel, "Selected Leg", "当前训练：左腿",
                new Vector2(0.07f, 0.115f), new Vector2(0.93f, 0.18f),
                16, FontStyles.Bold, BrightGreen, TextAlignmentOptions.MidlineLeft);

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
                new Color(PanelNavy.r, PanelNavy.g, PanelNavy.b, 0.97f));
            Transform panel = photoPanel.transform;

            photoTitleText = CreateText(panel, "Photo Title", "实时取景",
                new Vector2(0.04f, 0.91f), new Vector2(0.57f, 0.985f),
                22, FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft);
            photoFocusText = CreateText(panel, "Focus Value", "清晰度 0%",
                new Vector2(0.58f, 0.91f), new Vector2(0.96f, 0.985f),
                18, FontStyles.Bold, BrightGreen, TextAlignmentOptions.MidlineRight);

            // 固定画框，图片作为被裁切内容在画框内滚动；不再移动整个取景面板。
            photoViewport = CreateImage(panel, "Photo Viewport",
                new Vector2(0.04f, 0.35f), new Vector2(0.96f, 0.827f),
                TrackNavy);
            photoViewport.AddComponent<RectMask2D>();
            photoImage = CreateRawImage(photoViewport.transform, "Photo Content",
                new Vector2(-PhotoContentOverscan, -PhotoContentOverscan),
                new Vector2(1f + PhotoContentOverscan, 1f + PhotoContentOverscan),
                Color.white);
            photoBaseAnchoredPosition = photoImage.rectTransform.anchoredPosition;

            photoPromptText = CreateText(panel, "Photo Prompt",
                "开始训练后，抬腿会让照片逐渐清晰",
                new Vector2(0.04f, 0.24f), new Vector2(0.96f, 0.33f),
                16, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);

            CreateImage(panel, "Focus Track",
                new Vector2(0.04f, 0.18f), new Vector2(0.96f, 0.215f),
                TrackNavy);
            focusBarFill = CreateImage(panel, "Focus Fill",
                new Vector2(0.04f, 0.18f), new Vector2(0.04f, 0.215f),
                BrightGreen).GetComponent<Image>();

            resultCard = CreateImage(panel, "Single Result Card",
                new Vector2(0.14f, 0.40f), new Vector2(0.86f, 0.74f),
                new Color(PanelNavyLight.r, PanelNavyLight.g, PanelNavyLight.b, 0.96f));
            resultText = CreateText(resultCard.transform, "Result Text",
                "拍摄成功",
                new Vector2(0.06f, 0.08f), new Vector2(0.94f, 0.92f),
                28, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            resultCard.SetActive(false);
        }

        private void BuildBottomPanel(Transform parent)
        {
            trainingPanel = CreateImage(parent, "Training Panel",
                new Vector2(0.015f, 0.065f), new Vector2(0.985f, 0.23f),
                new Color(PanelNavy.r, PanelNavy.g, PanelNavy.b, 0.98f));
            Transform panel = trainingPanel.transform;

            mainInstructionText = CreateText(panel, "Main Instruction",
                "请先完成设备连接和站姿标定",
                new Vector2(0.025f, 0.57f), new Vector2(0.58f, 0.94f),
                22, FontStyles.Bold, Color.white, TextAlignmentOptions.MidlineLeft);

            CreateImage(panel, "Hold Track",
                new Vector2(0.025f, 0.37f), new Vector2(0.58f, 0.48f),
                TrackNavy);
            holdBarFill = CreateImage(panel, "Hold Fill",
                new Vector2(0.025f, 0.37f), new Vector2(0.025f, 0.48f),
                BrightGreen).GetComponent<Image>();

            stageText = CreateText(panel, "Stage", "● 等待开始",
                new Vector2(0.025f, 0.08f), new Vector2(0.58f, 0.32f),
                17, FontStyles.Bold, BrightGreen, TextAlignmentOptions.MidlineLeft);

            angleMetricText = CreateMetric(panel, "Angle Metric", "当前角度", "--",
                new Vector2(0.60f, 0.10f), new Vector2(0.725f, 0.90f));
            sessionCountText = CreateMetric(panel, "Session Count", "重复次数", "0 / 3",
                new Vector2(0.735f, 0.10f), new Vector2(0.855f, 0.90f));
            holdText = CreateMetric(panel, "Hold Time", "保持计时", "0.0 / 3 秒",
                new Vector2(0.865f, 0.10f), new Vector2(0.975f, 0.90f));
        }

        private void BuildModal(Transform parent)
        {
            modalOverlay = CreateImage(parent, "Session Modal Backdrop",
                Vector2.zero, Vector2.one, new Color(0f, 0f, 0f, 0.45f));
            Transform card = CreateImage(modalOverlay.transform, "Session Card",
                new Vector2(0.25f, 0.10f), new Vector2(0.75f, 0.90f),
                WarmWhite).transform;

            modalTitleText = CreateText(card, "Modal Title", "准备开始",
                new Vector2(0.08f, 0.88f), new Vector2(0.92f, 0.97f),
                32, FontStyles.Bold, DarkGreen, TextAlignmentOptions.Center);
            modalBodyText = CreateText(card, "Modal Body",
                "连接四个传感器并完成站姿标定后开始训练。",
                new Vector2(0.07f, 0.57f), new Vector2(0.93f, 0.87f),
                22, FontStyles.Normal, DarkGreen, TextAlignmentOptions.Center);

            targetControls = new GameObject("Target Controls", typeof(RectTransform));
            targetControls.layer = 5;
            targetControls.transform.SetParent(card, false);
            SetRect((RectTransform)targetControls.transform,
                new Vector2(0.08f, 0.16f), new Vector2(0.92f, 0.32f));
            BuildStageSelection(targetControls.transform);

            kneeModeButton = CreateButton(
                targetControls.transform, "Knee Extension Mode", "坐姿伸膝",
                new Vector2(0f, 0.36f), new Vector2(0.32f, 0.68f),
                Green, Color.white);
            sitToStandModeButton = CreateButton(
                targetControls.transform, "Sit To Stand Mode", "坐站摄影",
                new Vector2(0.34f, 0.36f), new Vector2(0.66f, 0.68f),
                PaleGreen, DarkGreen);
            shallowSquatModeButton = CreateButton(
                targetControls.transform, "Shallow Squat Mode", "浅蹲摄影",
                new Vector2(0.68f, 0.36f), new Vector2(1f, 0.68f),
                PaleGreen, DarkGreen);
            kneeModeButton.onClick.AddListener(() =>
                SelectTrainingMode(PhotoTrainingMode.SeatedKneeExtension));
            sitToStandModeButton.onClick.AddListener(() =>
                SelectTrainingMode(PhotoTrainingMode.SitToStand));
            shallowSquatModeButton.onClick.AddListener(() =>
                SelectTrainingMode(PhotoTrainingMode.ShallowSquat));

            CreateText(targetControls.transform, "Target Label", "照片目标",
                new Vector2(0f, 0f), new Vector2(0.34f, 0.32f),
                20, FontStyles.Bold, DarkGreen, TextAlignmentOptions.Center);
            targetMinusButton = CreateButton(targetControls.transform, "Target Minus", "－",
                new Vector2(0.36f, 0f), new Vector2(0.51f, 0.32f),
                PaleGreen, DarkGreen);
            targetValueText = CreateText(targetControls.transform, "Target Value", "3 次",
                new Vector2(0.52f, 0f), new Vector2(0.71f, 0.32f),
                24, FontStyles.Bold, Green, TextAlignmentOptions.Center);
            targetPlusButton = CreateButton(targetControls.transform, "Target Plus", "＋",
                new Vector2(0.72f, 0f), new Vector2(0.87f, 0.32f),
                PaleGreen, DarkGreen);
            targetMinusButton.onClick.AddListener(() => ChangeTarget(-1));
            targetPlusButton.onClick.AddListener(() => ChangeTarget(1));

            modalPrimaryButton = CreateButton(card, "Modal Primary", "开始训练",
                new Vector2(0.08f, 0.035f), new Vector2(0.59f, 0.13f),
                Green, Color.white);
            modalSecondaryButton = CreateButton(card, "Modal Secondary", "结束训练",
                new Vector2(0.63f, 0.035f), new Vector2(0.92f, 0.13f),
                PaleGreen, DarkGreen);
            modalPrimaryButton.onClick.AddListener(HandleModalPrimary);
            modalSecondaryButton.onClick.AddListener(HandleModalSecondary);
            BuildPreflightControls(card);
        }

        private void StartTraining()
        {
            if (s2Selected)
            {
                StartS2Training();
                return;
            }
            if (!CanStartTraining() || !BeginFlowSession())
            {
                motionUI?.SetFormalConnectionPanelVisible(true);
                modalBodyText.text = StartGateSummary();
                return;
            }

            session.Start();
            focusSession.Reset();
            resultCard.SetActive(false);
            resultEndsAt = 0f;
            voiceGuide?.StopPlayback();
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
            {
                training?.SetSessionPaused(false, "s1_session_start");
            }
            else if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                sitToStandTraining?.SetSessionPaused(false, "stage3_session_start");
                lastSitToStandCaptureSequence =
                    sitToStandTraining != null
                        ? sitToStandTraining.CurrentSnapshot.CaptureSequence
                        : 0;
            }
            else
            {
                shallowSquatTraining?.SetSessionPaused(false, "stage4_session_start");
                lastShallowSquatCaptureSequence =
                    shallowSquatTraining != null
                        ? shallowSquatTraining.CurrentSnapshot.CaptureSequence
                        : 0;
            }
            motionUI?.SetFormalConnectionPanelVisible(false);
            connectionPanelAutoHidden = true;
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
            {
                motionCapture?.LogGameDiagnostic(
                    "s1_session_start",
                    $"target={session.TargetRepetitions}, leg={LegName(snapshot.Leg)}");
            }
            else if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                motionCapture?.LogTrainingDiagnostic(
                    "sit_to_stand", "session_start",
                    $"target={session.TargetRepetitions}, sensors=06+07+08+09");
            }
            else
            {
                motionCapture?.LogTrainingDiagnostic(
                    "shallow_squat", "session_start",
                    $"target={session.TargetRepetitions}, sensors=06+07+08+09");
            }
        }

        private void PauseTraining()
        {
            if (!flow.Pause(Time.realtimeSinceStartup)) return;
            if (!session.Pause()) return;
            if (s2Selected) combination.Restart(flow.Result.active_seconds, "pause_reprepare_not_error");
            SuspendGameRuntimes("stage5_user_pause");
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
            {
                training?.SetSessionPaused(true, "s1_pause_button");
                motionCapture?.LogGameDiagnostic(
                    "s1_session_pause",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
            else if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                sitToStandTraining?.SetSessionPaused(true, "stage3_pause_button");
                motionCapture?.LogTrainingDiagnostic(
                    "sit_to_stand", "session_pause",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
            else
            {
                shallowSquatTraining?.SetSessionPaused(true, "stage4_pause_button");
                motionCapture?.LogTrainingDiagnostic(
                    "shallow_squat", "session_pause",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
        }

        private void ResumeTraining()
        {
            // 按钮点击时重新读健康，不能用上一帧的可恢复状态放行。
            TickFlowHealth();
            if (!flow.Resume(Time.realtimeSinceStartup)) return;
            helpOpen = false;
            if (!session.Resume()) return;
            focusSession.Reset();
            if (s2Selected)
            {
                ActivateS2Step(true);
                return;
            }
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
            {
                training?.SetSessionPaused(false, "s1_resume_button");
                motionCapture?.LogGameDiagnostic(
                    "s1_session_resume",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
            else if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                sitToStandTraining?.SetSessionPaused(false, "stage3_resume_button");
                lastSitToStandCaptureSequence = sitToStandTraining != null
                    ? sitToStandTraining.CurrentSnapshot.CaptureSequence
                    : 0;
                motionCapture?.LogTrainingDiagnostic(
                    "sit_to_stand", "session_resume",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
            else
            {
                shallowSquatTraining?.SetSessionPaused(false, "stage4_resume_button");
                lastShallowSquatCaptureSequence = shallowSquatTraining != null
                    ? shallowSquatTraining.CurrentSnapshot.CaptureSequence
                    : 0;
                motionCapture?.LogTrainingDiagnostic(
                    "shallow_squat", "session_resume",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
        }

        private void EndTraining()
        {
            FinishFlowSession("user_end");
            if (!session.End()) return;
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
                training?.SetSessionPaused(true, "s1_end_button");
            else if (trainingMode == PhotoTrainingMode.SitToStand)
                sitToStandTraining?.SetSessionPaused(true, "stage3_end_button");
            else
                shallowSquatTraining?.SetSessionPaused(true, "stage4_end_button");
            resultCard.SetActive(false);
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
                motionCapture?.LogGameDiagnostic(
                    "s1_session_end",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            else if (trainingMode == PhotoTrainingMode.SitToStand)
                motionCapture?.LogTrainingDiagnostic(
                    "sit_to_stand", "session_end",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            else
                motionCapture?.LogTrainingDiagnostic(
                    "shallow_squat", "session_end",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
        }

        private void HandleCapture()
        {
            if (s2Selected) return; // S2 只能由有序组合事件计数，不能走单动作快门入口。
            if (!flow.RecordCapture(Time.realtimeSinceStartup)) return;
            bool completed = session.RecordCapture();
            flashEndsAt = Time.unscaledTime + 0.20f;
            resultEndsAt = Time.unscaledTime + 2.5f;
            resultCard.SetActive(true);
            resultText.text = trainingMode == PhotoTrainingMode.SitToStand
                ? $"高处拍摄成功\n第 {session.CompletedRepetitions} 张照片"
                : trainingMode == PhotoTrainingMode.ShallowSquat
                    ? $"低处拍摄成功\n第 {session.CompletedRepetitions} 张照片"
                    : $"拍摄成功\n第 {session.CompletedRepetitions} 张照片";
            if (audioSource != null && shutterClip != null)
                audioSource.PlayOneShot(shutterClip);
            // 坐姿伸膝动作层已有回位语音；坐站和浅蹲由独立游戏流程语音补齐。
            if (trainingMode != PhotoTrainingMode.SeatedKneeExtension)
                SpeakFlowCue(TrainingVoiceCue.PhotoCompletedReturn);

            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
            {
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
            }
            else if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                motionCapture?.LogTrainingDiagnostic(
                    "sit_to_stand", "photo_capture",
                    JsonUtility.ToJson(new SitToStandCaptureDiagnostic
                    {
                        sessionCompleted = session.CompletedRepetitions,
                        sessionTarget = session.TargetRepetitions,
                        progress = sitToStandSnapshot.RaiseProgress01,
                        holdSeconds = sitToStandSnapshot.HoldSeconds,
                        leftThighDeg = sitToStandSnapshot.LeftThighDeg,
                        leftKneeDeg = sitToStandSnapshot.LeftKneeDeg,
                        rightThighDeg = sitToStandSnapshot.RightThighDeg,
                        rightKneeDeg = sitToStandSnapshot.RightKneeDeg
                    }));
                SelectActivePhoto();
            }
            else
            {
                motionCapture?.LogTrainingDiagnostic(
                    "shallow_squat", "photo_capture",
                    JsonUtility.ToJson(new ShallowSquatCaptureDiagnostic
                    {
                        sessionCompleted = session.CompletedRepetitions,
                        sessionTarget = session.TargetRepetitions,
                        progress = shallowSquatSnapshot.DropProgress01,
                        holdSeconds = shallowSquatSnapshot.HoldSeconds,
                        leftThighDeg = shallowSquatSnapshot.LeftThighDeg,
                        leftKneeDeg = shallowSquatSnapshot.LeftKneeDeg,
                        rightThighDeg = shallowSquatSnapshot.RightThighDeg,
                        rightKneeDeg = shallowSquatSnapshot.RightKneeDeg
                    }));
                SelectActivePhoto();
            }

            if (!completed) return;
            FinishFlowSession("target_completed");
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
            {
                training?.SetSessionPaused(true, "s1_target_complete");
                motionCapture?.LogGameDiagnostic(
                    "s1_session_complete",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
            else if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                sitToStandTraining?.SetSessionPaused(true, "stage3_target_complete");
                motionCapture?.LogTrainingDiagnostic(
                    "sit_to_stand", "session_complete",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
            else
            {
                shallowSquatTraining?.SetSessionPaused(true, "stage4_target_complete");
                motionCapture?.LogTrainingDiagnostic(
                    "shallow_squat", "session_complete",
                    $"completed={session.CompletedRepetitions}, target={session.TargetRepetitions}");
            }
        }

        private void TogglePause()
        {
            if (flow.State == TrainingFlowState.SensorError)
            {
                ResumeTraining();
                return;
            }
            if (session.State == S1TrainingRunState.Running)
                PauseTraining();
            else if (session.State == S1TrainingRunState.Paused)
                ResumeTraining();
        }

        private void HandleModalPrimary()
        {
            if (flow.State == TrainingFlowState.Review || flow.State == TrainingFlowState.SafetyStop)
            {
                ReturnFlowToSetup();
                return;
            }
            if (flow.State == TrainingFlowState.SensorError)
            {
                ResumeTraining();
                return;
            }
            switch (session.State)
            {
                case S1TrainingRunState.Idle:
                    StartTraining();
                    break;
                case S1TrainingRunState.Paused:
                    ResumeTraining();
                    break;
                case S1TrainingRunState.Completed:
                    ReturnFlowToSetup();
                    break;
                case S1TrainingRunState.Ended:
                    ReturnFlowToSetup();
                    break;
            }
        }

        private void HandleModalSecondary()
        {
            if (flow.State == TrainingFlowState.SensorError)
            {
                EndTraining();
                return;
            }
            if (session.State == S1TrainingRunState.Paused ||
                session.State == S1TrainingRunState.Completed)
                EndTraining();
        }

        private void ChangeTarget(int delta)
        {
            if (s2Selected)
            {
                ChangeS2Target(delta);
                return;
            }
            session.SetTarget(session.TargetRepetitions + delta);
        }

        private void SelectLeg(TrainingLeg leg)
        {
            if (s2Selected && flow.IsActive) return;
            if (trainingMode != PhotoTrainingMode.SeatedKneeExtension) return;
            if (flow.IsActive) PauseTraining();
            if (flow.State == TrainingFlowState.SensorError ||
                flow.State == TrainingFlowState.SafetyStop) return;
            training?.SelectTrainingLeg(leg, "s1_canvas_button");
            focusSession.Reset();
            resultCard.SetActive(false);
        }

        private void SelectTrainingMode(PhotoTrainingMode mode)
        {
            if (s2Selected) return;
            if (session.State != S1TrainingRunState.Idle || trainingMode == mode)
                return;

            trainingMode = mode;
            focusSession.Reset();
            resultCard.SetActive(false);
            resultEndsAt = 0f;

            bool kneeExtension = mode == PhotoTrainingMode.SeatedKneeExtension;
            bool sitToStand = mode == PhotoTrainingMode.SitToStand;
            bool shallowSquat = mode == PhotoTrainingMode.ShallowSquat;
            training?.SetTrainingModeActive(kneeExtension, "formal_ui_mode_switch");
            sitToStandTraining?.SetModeActive(sitToStand, "formal_ui_mode_switch");
            shallowSquatTraining?.SetModeActive(shallowSquat, "formal_ui_mode_switch");
            if (kneeExtension)
            {
                training?.SetSessionPaused(true, "formal_ui_mode_switch");
            }
            else if (sitToStand)
            {
                lastSitToStandCaptureSequence = sitToStandTraining != null
                    ? sitToStandTraining.CurrentSnapshot.CaptureSequence
                    : 0;
            }
            else
            {
                lastShallowSquatCaptureSequence = shallowSquatTraining != null
                    ? shallowSquatTraining.CurrentSnapshot.CaptureSequence
                    : 0;
            }

            SelectActivePhoto();
            RefreshAllVisuals();
        }

        private void DetectSitToStandCapture()
        {
            if (sitToStandSnapshot.CaptureSequence <= lastSitToStandCaptureSequence)
                return;
            lastSitToStandCaptureSequence = sitToStandSnapshot.CaptureSequence;
            if (session.State == S1TrainingRunState.Running)
                HandleCapture();
        }

        private void DetectShallowSquatCapture()
        {
            if (shallowSquatSnapshot.CaptureSequence <= lastShallowSquatCaptureSequence)
                return;
            lastShallowSquatCaptureSequence = shallowSquatSnapshot.CaptureSequence;
            if (session.State == S1TrainingRunState.Running)
                HandleCapture();
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
            RefreshModeButtons();
            RefreshS2Visuals();
        }

        private void RefreshTopBar()
        {
            if (titleText != null)
                titleText.text = trainingMode == PhotoTrainingMode.SitToStand
                    ? "公园摄影站  ·  认知阶段 · 坐站摄影"
                    : trainingMode == PhotoTrainingMode.ShallowSquat
                        ? "公园摄影站  ·  认知阶段 · 浅蹲摄影"
                        : "公园摄影站  ·  认知阶段 · 坐姿伸膝";
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
            float holdSeconds = trainingMode == PhotoTrainingMode.SitToStand
                ? sitToStandSnapshot.HoldSeconds
                : trainingMode == PhotoTrainingMode.ShallowSquat
                    ? shallowSquatSnapshot.HoldSeconds
                    : snapshot.HoldSeconds;
            float holdDuration = trainingMode == PhotoTrainingMode.SitToStand
                ? sitToStandSnapshot.HoldDurationSeconds
                : trainingMode == PhotoTrainingMode.ShallowSquat
                    ? shallowSquatSnapshot.HoldDurationSeconds
                    : snapshot.HoldDurationSeconds;
            float holdRatio = holdDuration > 0f
                ? Mathf.Clamp01(holdSeconds / holdDuration)
                : 0f;
            SetFill(holdBarFill, holdRatio, 0.025f, 0.58f);

            sessionCountText.text =
                $"<size=16><color=#9DB0BD>已拍照片</color></size>\n" +
                $"<size=34><b>{session.CompletedRepetitions} / {session.TargetRepetitions}</b></size>";
            holdText.text =
                $"<size=16><color=#9DB0BD>保持计时</color></size>\n" +
                $"<size=30><b>{holdSeconds:F1} / " +
                $"{Mathf.Max(0f, holdDuration):F0} 秒</b></size>";
            if (angleMetricText != null)
                angleMetricText.text =
                    $"<size=16><color=#9DB0BD>当前角度</color></size>\n" +
                    $"<size=26><b>{CurrentAngleMetric()}</b></size>";

            mainInstructionText.text = FriendlyInstruction();
            stageText.text = StageCaption();
            if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                selectedLegText.text = "当前训练：双腿坐站";
                coachInstructionText.text =
                    "① 坐稳，双脚平放地面\n② 左右腿同步缓慢站起\n" +
                    "③ 站稳后保持等待快门\n④ 拍摄后缓慢坐回";
            }
            else if (trainingMode == PhotoTrainingMode.ShallowSquat)
            {
                selectedLegText.text = "当前训练：双腿浅蹲";
                coachInstructionText.text =
                    "① 站稳，双脚平行着地\n② 左右腿同步缓慢屈曲\n" +
                    "③ 浅蹲到目标后保持\n④ 拍摄后缓慢站直";
            }
            else
            {
                selectedLegText.text = "当前训练：" + LegName(snapshot.Leg) + "腿";
                coachInstructionText.text = snapshot.Leg == TrainingLeg.Right
                    ? "① 坐稳，右小腿自然下垂\n② 缓慢抬起右小腿\n③ 画面清晰后保持\n④ 快门后缓慢放下"
                    : "① 坐稳，左小腿自然下垂\n② 缓慢抬起左小腿\n③ 画面清晰后保持\n④ 快门后缓慢放下";
            }
        }

        private void RefreshPhotoPanel()
        {
            float progress = trainingMode == PhotoTrainingMode.SitToStand
                ? sitToStandSnapshot.RaiseProgress01
                : trainingMode == PhotoTrainingMode.ShallowSquat
                    ? shallowSquatSnapshot.DropProgress01
                    : focusSession.Focus01;
            if (photoTitleText != null)
                photoTitleText.text = trainingMode == PhotoTrainingMode.SitToStand
                    ? "● 实时取景 · 高处目标"
                    : trainingMode == PhotoTrainingMode.ShallowSquat
                        ? "● 实时取景 · 低处目标"
                        : "● 实时取景 · 远景对焦";
            photoFocusText.text = trainingMode == PhotoTrainingMode.SitToStand
                ? $"取景高度 {progress * 100f:F0}%"
                : trainingMode == PhotoTrainingMode.ShallowSquat
                    ? $"降低进度 {progress * 100f:F0}%"
                    : $"清晰度 {progress * 100f:F0}%";
            SetFill(focusBarFill, progress, 0.04f, 0.96f);
            photoPromptText.text = PhotoPrompt();

            if (photoImage != null)
            {
                // 画框的 RectTransform 始终不动，只移动其中被 RectMask2D 裁切的图片内容。
                float viewportHeight = photoViewport != null
                    ? photoViewport.GetComponent<RectTransform>().rect.height
                    : 0f;
                float scroll = viewportHeight * PhotoScrollRange * progress;
                Vector2 scrollDirection = trainingMode == PhotoTrainingMode.SitToStand
                    ? Vector2.down
                    : trainingMode == PhotoTrainingMode.ShallowSquat
                        ? Vector2.up
                        : Vector2.zero;
                photoImage.rectTransform.anchoredPosition =
                    photoBaseAnchoredPosition + scrollDirection * scroll;
            }

            if (resultCard.activeSelf &&
                session.State != S1TrainingRunState.Completed &&
                Time.unscaledTime >= resultEndsAt)
                resultCard.SetActive(false);
        }

        private void RefreshModal()
        {
            if (RefreshFlowModal()) return;
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
                        ? trainingMode == PhotoTrainingMode.SitToStand
                            ? "请选择训练模式和目标次数。双腿同步站起会让取景框逐渐升高。"
                            : trainingMode == PhotoTrainingMode.ShallowSquat
                                ? "请选择训练模式和目标次数。双腿同步浅蹲会让取景画面逐渐降低。"
                                : "请选择训练模式和目标次数。照片会随伸膝动作逐渐变清晰。"
                        : "请先打开“设备设置”，连接四个传感器并完成站姿标定。";
                    SetButtonLabel(modalPrimaryButton, "开始训练");
                    modalPrimaryButton.interactable = CanStartTraining();
                    modalSecondaryButton.gameObject.SetActive(false);
                    break;

                case S1TrainingRunState.Paused:
                    modalTitleText.text = "训练已暂停";
                    modalBodyText.text =
                        $"已经拍摄 {session.CompletedRepetitions} / " +
                        $"{session.TargetRepetitions} 张。" +
                        (trainingMode == PhotoTrainingMode.SitToStand
                            ? "可以先休息，继续时请重新坐稳并让双脚着地。"
                            : trainingMode == PhotoTrainingMode.ShallowSquat
                                ? "可以先休息，继续时请重新站稳并让双脚平行着地。"
                                : "可以先放松，继续时请让小腿自然下垂。");
                    SetButtonLabel(modalPrimaryButton, "继续训练");
                    modalPrimaryButton.interactable = MotionReady();
                    modalSecondaryButton.gameObject.SetActive(true);
                    SetButtonLabel(modalSecondaryButton, "结束训练");
                    break;

                case S1TrainingRunState.Completed:
                    modalTitleText.text = "本组训练完成";
                    modalBodyText.text =
                        $"已完成 {session.CompletedRepetitions} 次" +
                        (trainingMode == PhotoTrainingMode.SitToStand
                            ? "高处拍摄。请缓慢坐回并休息一下。"
                            : trainingMode == PhotoTrainingMode.ShallowSquat
                                ? "低处拍摄。请缓慢站直并休息一下。"
                                : "远景拍摄。请慢慢放下小腿并休息一下。");
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
            bool showLegSelection =
                trainingMode == PhotoTrainingMode.SeatedKneeExtension && (!s2Selected || !flow.IsActive);
            leftLegButton.gameObject.SetActive(showLegSelection);
            rightLegButton.gameObject.SetActive(showLegSelection);
            if (!showLegSelection) return;
            bool right = snapshot.Leg == TrainingLeg.Right;
            StyleSelection(leftLegButton, !right);
            StyleSelection(rightLegButton, right);
        }

        private void RefreshModeButtons()
        {
            bool kneeExtension =
                trainingMode == PhotoTrainingMode.SeatedKneeExtension;
            StyleSelection(kneeModeButton, kneeExtension);
            StyleSelection(sitToStandModeButton,
                trainingMode == PhotoTrainingMode.SitToStand);
            StyleSelection(shallowSquatModeButton,
                trainingMode == PhotoTrainingMode.ShallowSquat);
        }

        private string FriendlyInstruction()
        {
            if (!MotionReady()) return "请先连接设备并完成站姿标定";
            switch (session.State)
            {
                case S1TrainingRunState.Idle: return "设置目标次数，然后开始训练";
                case S1TrainingRunState.Paused: return "训练已暂停，可以先放松";
                case S1TrainingRunState.Completed:
                    return trainingMode == PhotoTrainingMode.SitToStand
                        ? "本组完成，请缓慢坐回并休息"
                        : trainingMode == PhotoTrainingMode.ShallowSquat
                            ? "本组完成，请缓慢站直并休息"
                            : "本组完成，请慢慢放下小腿";
                case S1TrainingRunState.Ended: return "训练已结束";
            }

            if (trainingMode == PhotoTrainingMode.SitToStand)
                return SitToStandInstruction();
            if (trainingMode == PhotoTrainingMode.ShallowSquat)
                return ShallowSquatInstruction();

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
            if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                switch (sitToStandSnapshot.Stage)
                {
                    case SitToStandStage.Preparing: return "● 准备：坐稳并让双脚着地";
                    case SitToStandStage.Rising: return "● 起身：双腿同步缓慢伸直";
                    case SitToStandStage.Holding: return "● 保持：站稳后自动拍摄";
                    default: return "● 返回：缓慢坐回准备姿势";
                }
            }
            if (trainingMode == PhotoTrainingMode.ShallowSquat)
            {
                switch (shallowSquatSnapshot.Stage)
                {
                    case ShallowSquatStage.Preparing: return "● 准备：站稳并让双脚平行";
                    case ShallowSquatStage.Lowering: return "● 下蹲：双腿同步缓慢屈曲";
                    case ShallowSquatStage.Holding: return "● 保持：浅蹲稳定后自动拍摄";
                    case ShallowSquatStage.TooDeep: return "● 调整：回浅一点后重新保持";
                    default: return "● 返回：双腿同步缓慢站直";
                }
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
                return trainingMode == PhotoTrainingMode.SitToStand
                    ? "开始训练后，双腿站起会让取景框逐渐升高"
                    : trainingMode == PhotoTrainingMode.ShallowSquat
                        ? "开始训练后，双腿浅蹲会让取景画面逐渐降低"
                        : "开始训练后，抬腿会让照片逐渐清晰";
            if (session.State == S1TrainingRunState.Paused)
                return trainingMode == PhotoTrainingMode.SitToStand
                    ? "训练暂停，当前取景高度不会继续"
                    : trainingMode == PhotoTrainingMode.ShallowSquat
                        ? "训练暂停，当前降低进度不会继续"
                        : "训练暂停，当前对焦进度不会继续";
            if (session.State == S1TrainingRunState.Completed)
                return "本组拍摄完成";
            if (session.State == S1TrainingRunState.Ended)
                return "训练已结束";
            if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                if (!sitToStandSnapshot.IsDataValid)
                    return "信号暂时中断，取景高度已冻结";
                if (sitToStandSnapshot.Stage == SitToStandStage.Holding)
                    return "高处目标已进入画面，请站稳等待快门";
                if (sitToStandSnapshot.Stage == SitToStandStage.Returning)
                    return "高处照片已拍好，请缓慢坐回";
                return "双腿同步伸直，让高处目标进入取景框";
            }
            if (trainingMode == PhotoTrainingMode.ShallowSquat)
            {
                if (!shallowSquatSnapshot.IsDataValid)
                    return ShallowSquatInstruction();
                if (shallowSquatSnapshot.Stage == ShallowSquatStage.TooDeep)
                    return "已超出浅蹲范围，请回浅一点；保持计时已清零";
                if (shallowSquatSnapshot.Stage == ShallowSquatStage.Holding)
                    return "低处目标已进入画面，请保持浅蹲等待快门";
                if (shallowSquatSnapshot.Stage == ShallowSquatStage.Returning)
                    return "低处照片已拍好，请双腿同步站直";
                return "双腿同步屈曲，让低处目标进入取景框";
            }
            if (!snapshot.IsDataValid)
                return "信号暂时中断，对焦进度已冻结";
            if (snapshot.Stage == KneeExtensionStage.Holding)
                return "对焦完成，保持姿势等待快门";
            if (snapshot.Stage == KneeExtensionStage.Returning)
                return "照片已拍好，请慢慢放下小腿";
            return "缓慢伸膝，让远景逐渐清晰";
        }

        private string SitToStandInstruction()
        {
            if (!sitToStandSnapshot.IsDataValid)
                return FriendlyBlockReason(sitToStandSnapshot.BlockReason);
            switch (sitToStandSnapshot.Stage)
            {
                case SitToStandStage.Preparing:
                    return "请坐稳，双脚平放地面，等待系统确认";
                case SitToStandStage.Rising:
                    return "左右腿同步缓慢站起，让取景框升高";
                case SitToStandStage.Holding:
                    return "很好，已经站稳，请保持等待快门";
                default:
                    return "拍摄完成，请缓慢坐回准备姿势";
            }
        }

        private string ShallowSquatInstruction()
        {
            // 浅蹲直接展示动作层的具体原因，避免过深、单腿、辅助提示被阶段文案覆盖。
            if (!string.IsNullOrEmpty(shallowSquatSnapshot.BlockReason))
                return shallowSquatSnapshot.BlockReason;
            switch (shallowSquatSnapshot.Stage)
            {
                case ShallowSquatStage.Preparing:
                    return "请站稳，双脚平行着地，等待系统确认";
                case ShallowSquatStage.Lowering:
                    return "左右腿同步缓慢屈曲，让取景画面降低";
                case ShallowSquatStage.Holding:
                    return "很好，已到浅蹲目标，请保持等待快门";
                case ShallowSquatStage.TooDeep:
                    return "已超出浅蹲范围，请回浅一点后重新保持";
                default:
                    return "拍摄完成，请双腿同步缓慢站直";
            }
        }

        private string CurrentAngleMetric()
        {
            if (trainingMode == PhotoTrainingMode.SitToStand)
            {
                if (!sitToStandSnapshot.IsDataValid)
                    return "--";
                return $"{sitToStandSnapshot.LeftKneeDeg:F0}° /\n" +
                       $"{sitToStandSnapshot.RightKneeDeg:F0}°";
            }
            if (trainingMode == PhotoTrainingMode.ShallowSquat)
            {
                if (!shallowSquatSnapshot.IsDataValid)
                    return "--";
                return $"{shallowSquatSnapshot.LeftKneeDeg:F0}° /\n" +
                       $"{shallowSquatSnapshot.RightKneeDeg:F0}°";
            }
            if (!snapshot.IsDataValid)
                return "--";
            return $"{snapshot.KneeAngleDeg:F0}°";
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
            if (value.Contains("站直") || value.Contains("浅蹲") ||
                value.Contains("同步") || value.Contains("过深"))
                return value;
            if (value.Contains("坐稳") || value.Contains("坐位") ||
                value.Contains("大腿"))
                return "请坐稳，让大腿保持自然水平";
            return "请坐稳，让小腿自然下垂后重新准备";
        }

        private void LoadPhotoResources()
        {
            distantPhoto = Resources.Load<Texture2D>(PhotoResource);
            highPhotos = new Texture2D[HighPhotoResources.Length];
            for (int i = 0; i < HighPhotoResources.Length; i++)
                highPhotos[i] = Resources.Load<Texture2D>(HighPhotoResources[i]);
            lowPhotos = new Texture2D[LowPhotoResources.Length];
            for (int i = 0; i < LowPhotoResources.Length; i++)
                lowPhotos[i] = Resources.Load<Texture2D>(LowPhotoResources[i]);
            SelectActivePhoto();
            Shader shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null)
                shader = Shader.Find("Hidden/RehabPhotoGame/FocusBlur");
            if (shader != null && shader.isSupported)
                blurMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            if (sourcePhoto == null)
            {
                photoPromptText.text = "摄影素材未加载，请联系工作人员";
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
            if (blurMaterial == null ||
                trainingMode != PhotoTrainingMode.SeatedKneeExtension)
            {
                Graphics.Blit(sourcePhoto, focusedPhoto);
                return;
            }
            blurMaterial.SetFloat(
                "_BlurPixels", Mathf.Lerp(18f, 0f, focusSession.Focus01));
            Graphics.Blit(sourcePhoto, focusedPhoto, blurMaterial);
        }

        private void SelectActivePhoto()
        {
            if (s2Selected && combination.IsRunning)
            {
                SelectS2Photo();
                return;
            }
            if (trainingMode == PhotoTrainingMode.SeatedKneeExtension)
            {
                sourcePhoto = distantPhoto;
                return;
            }

            if (trainingMode == PhotoTrainingMode.ShallowSquat)
            {
                SelectFromPhotoSet(lowPhotos, AvailableLowPhotoCount());
                return;
            }

            SelectFromPhotoSet(highPhotos, AvailableHighPhotoCount());
        }

        private void SelectFromPhotoSet(Texture2D[] photos, int available)
        {
            if (available <= 0)
            {
                sourcePhoto = distantPhoto;
                return;
            }

            int selected = session.CompletedRepetitions % available;
            for (int i = 0, seen = 0; i < photos.Length; i++)
            {
                if (photos[i] == null) continue;
                if (seen == selected)
                {
                    sourcePhoto = photos[i];
                    return;
                }
                seen++;
            }
        }

        private int AvailableHighPhotoCount()
        {
            if (highPhotos == null) return 0;
            int count = 0;
            for (int i = 0; i < highPhotos.Length; i++)
                if (highPhotos[i] != null) count++;
            return count;
        }

        private int AvailableLowPhotoCount()
        {
            if (lowPhotos == null) return 0;
            int count = 0;
            for (int i = 0; i < lowPhotos.Length; i++)
                if (lowPhotos[i] != null) count++;
            return count;
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
            bool runtimeReady = s2Selected
                ? S2RuntimeReady()
                : trainingMode == PhotoTrainingMode.SitToStand
                ? sitToStandTraining != null
                : trainingMode == PhotoTrainingMode.ShallowSquat
                    ? shallowSquatTraining != null
                    : training != null;
            return runtimeReady && MotionReady() && PreflightConfirmed &&
                   AllFourReady() && flowSettings != null && flowSettings.IsValid;
        }

        private float ActiveSensorTimeout()
        {
            if (trainingMode == PhotoTrainingMode.SitToStand && sitToStandTraining != null)
                return sitToStandTraining.SensorTimeoutSeconds;
            if (trainingMode == PhotoTrainingMode.ShallowSquat && shallowSquatTraining != null)
                return shallowSquatTraining.SensorTimeoutSeconds;
            return training != null ? training.SensorTimeoutSeconds : 2.2f;
        }

        private string DeviceChip(MotionCaptureState state, int index, string label)
        {
            if (state == null || !state.GetDeviceHasData(index) || motionCapture == null)
                return $"<color=#C7D1CB>{label} ○</color>";

            float age = motionCapture.GetDeviceFrameAgeSeconds(index);
            if (age < 0f)
                return $"<color=#C7D1CB>{label} ○</color>";

            float timeout = ActiveSensorTimeout();
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
            float timeout = ActiveSensorTimeout();
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
                new Color(PanelNavyLight.r, PanelNavyLight.g, PanelNavyLight.b, 0.96f));
            CreateImage(panel.transform, "Accent",
                new Vector2(0f, 0f), new Vector2(0.025f, 1f), Green);
            return CreateText(panel.transform, "Value",
                $"<size=16><color=#9DB0BD>{title}</color></size>\n" +
                $"<size=32><b>{value}</b></size>",
                new Vector2(0.08f, 0.08f), new Vector2(0.94f, 0.92f),
                22, FontStyles.Normal, BrightGreen, TextAlignmentOptions.Center);
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
            DisposeStageFive();
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

        [Serializable]
        private sealed class SitToStandCaptureDiagnostic
        {
            public int sessionCompleted, sessionTarget;
            public float progress, holdSeconds;
            public float leftThighDeg, leftKneeDeg, rightThighDeg, rightKneeDeg;
        }

        [Serializable]
        private sealed class ShallowSquatCaptureDiagnostic
        {
            public int sessionCompleted, sessionTarget;
            public float progress, holdSeconds;
            public float leftThighDeg, leftKneeDeg, rightThighDeg, rightKneeDeg;
        }
    }
}
