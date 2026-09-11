using System;
using System.Diagnostics;
using UnityEngine;

namespace RehabPhotoGame
{
    [DefaultExecutionOrder(900)]
    public static class SeatedKneeExtensionRuntimeFixBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SeatedKneeExtensionPrototype old = UnityEngine.Object.FindObjectOfType<SeatedKneeExtensionPrototype>();
            if (old == null) return;
            old.enabled = false;
            SeatedKneeExtensionRuntimeFix runtime =
                old.gameObject.GetComponent<SeatedKneeExtensionRuntimeFix>();
            if (runtime == null)
                runtime = old.gameObject.AddComponent<SeatedKneeExtensionRuntimeFix>();
            PhotoFocusFeedback legacyPhoto = old.gameObject.GetComponent<PhotoFocusFeedback>();
            if (legacyPhoto != null)
                legacyPhoto.enabled = false;
            if (old.gameObject.GetComponent<S1TrainingCanvasController>() == null)
                old.gameObject.AddComponent<S1TrainingCanvasController>();
        }
    }

    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public sealed class SeatedKneeExtensionRuntimeFix : MonoBehaviour
    {
        private const float SwitchSettleSeconds = 1.6f;
        private const int SwitchRequiredSamples = 2;
        private const float SpeechGapSeconds = 4.5f;
        private const float LeftRawStraightReferenceDeg = 0f;
        private const float RightRawStraightReferenceDeg = 17f;
        private const float SeatedHeightSettleSeconds = 0.75f;
        private const float AutoLegMotionThresholdDeg = 10f;
        private const float AutoLegDominance = 2.5f;
        private const float AutoLegConfirmSeconds = 0.45f;
        private const float StandingExitThighDeg = 35f;
        private const float StandingExitKneeDeg = 30f;
        private const float StandingExitConfirmSeconds = 0.6f;

        private MotionCaptureController motionCapture;
        private MotionCaptureUI motionUI;
        private TrainingLeg trainingLeg = TrainingLeg.Left;
        private SeatedKneeExtensionSettings settings;
        private SeatedKneeExtensionEvaluator leftEvaluator;
        private SeatedKneeExtensionEvaluator rightEvaluator;
        private LowerBodyMeasurement sample;

        private bool switchingLeg;
        private float switchStartedAt;
        private float lastSwitchSampleTime = float.NegativeInfinity;
        private int switchSamples;
        private float switchKneeSum;
        private int switchKneeCount;
        private float switchThighSum;
        private int switchThighCount;

        private bool seatedPoseEstablished;
        private bool seatedHeightLocked;
        private float seatedHeightLockAt = float.PositiveInfinity;
        private float sharedSeatedThighDeg = 90f;
        private float sharedReadyKneeDeg = 90f;
        private float standingCandidateSince = float.NaN;

        private float rightRawReadyDeg = float.NaN;
        private float rawRightKneeDeg = float.NaN;
        private int sessionVersion;
        private bool sessionPaused;
        private bool showTechnicalOverlay = true;

        private bool hasMotionBaseline;
        private Quaternion lastLeftCalfQ;
        private Quaternion lastRightCalfQ;
        private float leftMotionScore;
        private float rightMotionScore;
        private TrainingLeg pendingAutoLeg = TrainingLeg.Both;
        private float pendingAutoLegSince;

        private KneeExtensionStage previousStage;
        private Process speechProcess;
        private float nextSpeechAt;
        private float nextDiagnosticAt;
        private string lastDiagnosticKey = "";

        private Texture2D panelTexture, accentTexture, paleTexture, lightTexture, trackTexture;
        private GUIStyle headerStyle, instructionStyle, bodyStyle, smallStyle;
        private GUIStyle valueStyle, titleStyle, stageStyle;

        private static readonly Color DarkGreen = new Color(0.07f, 0.20f, 0.10f, 1f);
        private static readonly Color Green = new Color(0.18f, 0.42f, 0.20f, 1f);

        private SeatedKneeExtensionEvaluator ActiveEvaluator =>
            trainingLeg == TrainingLeg.Right ? rightEvaluator : leftEvaluator;

        /// <summary>
        /// 摄影玩法只读取这一份训练快照，不重复计算四元数和膝角。
        /// </summary>
        public KneeExtensionTrainingSnapshot CurrentSnapshot
        {
            get
            {
                SeatedKneeExtensionEvaluator evaluator = ActiveEvaluator;
                if (evaluator == null || settings == null)
                {
                    return new KneeExtensionTrainingSnapshot
                    {
                        Leg = trainingLeg,
                        Stage = KneeExtensionStage.Preparing,
                        SessionVersion = sessionVersion,
                        IsSwitchingLeg = true,
                        IsSessionPaused = sessionPaused,
                        BlockReason = "训练模块正在初始化"
                    };
                }

                return new KneeExtensionTrainingSnapshot
                {
                    Leg = trainingLeg,
                    Stage = evaluator.Stage,
                    SessionVersion = sessionVersion,
                    IsSwitchingLeg = switchingLeg,
                    IsSessionPaused = sessionPaused,
                    IsDataValid = sample.IsValid && !switchingLeg && !sessionPaused,
                    HasReadyReference = evaluator.HasReadyReference,
                    CompletedRepetitions = evaluator.CompletedRepetitions,
                    KneeAngleDeg = SelectedKneeAngle(),
                    ReadyReferenceKneeDeg = evaluator.ReadyReferenceKneeDeg,
                    TargetKneeDeg = evaluator.TargetKneeDeg,
                    LiftProgress01 = LiftProgress(),
                    HoldSeconds = evaluator.HoldSeconds,
                    HoldDurationSeconds = settings.holdSeconds,
                    BlockReason = sessionPaused
                        ? "训练已暂停，可以先放松"
                        : switchingLeg ? SwitchPrompt() : evaluator.BlockReason
                };
            }
        }

        /// <summary>供正式界面按同一超时门限显示 06～09 的实时状态。</summary>
        public float SensorTimeoutSeconds => settings != null
            ? settings.sensorTimeoutSeconds
            : 2.2f;

        public void SetTechnicalOverlayVisible(bool visible)
        {
            showTechnicalOverlay = visible;
        }

        /// <summary>由正式训练界面暂停判定；已有完成次数不会被清除。</summary>
        public void SetSessionPaused(bool paused, string source)
        {
            if (sessionPaused == paused) return;
            sessionPaused = paused;
            if (paused)
            {
                ActiveEvaluator?.Reset(trainingLeg, false);
                switchingLeg = false;
                pendingAutoLeg = TrainingLeg.Both;
                leftMotionScore = rightMotionScore = 0f;
                previousStage = ActiveEvaluator != null
                    ? ActiveEvaluator.Stage
                    : KneeExtensionStage.Preparing;
            }
            else if (leftEvaluator != null && rightEvaluator != null)
            {
                BeginLegSession(trainingLeg, false, source ?? "formal_ui_resume");
            }

            lastDiagnosticKey = "";
            motionCapture?.LogGameDiagnostic(
                paused ? "training_session_paused" : "training_session_resumed",
                $"leg={trainingLeg}, source={source ?? "formal_ui"}");
        }

        public void SelectTrainingLeg(TrainingLeg leg, string source)
        {
            TrainingLeg selected = leg == TrainingLeg.Right
                ? TrainingLeg.Right
                : TrainingLeg.Left;
            if (selected == trainingLeg) return;

            if (sessionPaused)
            {
                trainingLeg = selected;
                ActiveEvaluator?.Reset(trainingLeg, false);
                sessionVersion++;
                previousStage = ActiveEvaluator != null
                    ? ActiveEvaluator.Stage
                    : KneeExtensionStage.Preparing;
                lastDiagnosticKey = "";
                motionCapture?.LogGameDiagnostic(
                    "leg_switch",
                    $"leg={trainingLeg}, sensors={SensorPairLabel()}, source={source ?? "formal_ui"}");
            }
            else
            {
                BeginLegSession(selected, true, source ?? "formal_ui");
            }
        }

        private void Start()
        {
            motionCapture = FindObjectOfType<MotionCaptureController>();
            // 坐下后只允许求解一次共同高度；锁定后切腿和抬腿都不会再改根高度。
            motionCapture?.SetRootMotionEnabledForTraining(true);
            motionUI = motionCapture != null ? motionCapture.GetComponent<MotionCaptureUI>() : null;
            if (motionUI != null)
                motionUI.OnResetRequested += HandleFullReset;

            settings = new SeatedKneeExtensionSettings
            {
                readyKneeMinDeg = 55f,
                readyKneeMaxDeg = 130f,
                seatedThighMinDeg = 45f,
                seatedThighMaxDeg = 130f,
                minimumExtensionDeg = 20f,
                targetExitMarginDeg = 10f,
                returnToleranceDeg = 15f,
                stableRangeDeg = 15f,
                minimumConfirmedSamples = 2,
                readySeconds = 0.5f,
                holdSeconds = 3f,
                sensorTimeoutSeconds = 2.2f,
                maxSensorSkewSeconds = 2.2f,
                signalGraceSeconds = 4f
            };

            leftEvaluator = new SeatedKneeExtensionEvaluator(settings, TrainingLeg.Left);
            rightEvaluator = new SeatedKneeExtensionEvaluator(settings, TrainingLeg.Right);

            panelTexture = Solid(new Color(0.97f, 0.99f, 0.96f, 0.48f));
            accentTexture = Solid(new Color(Green.r, Green.g, Green.b, 0.84f));
            paleTexture = Solid(new Color(0.78f, 0.90f, 0.76f, 0.88f));
            lightTexture = Solid(new Color(0.97f, 0.99f, 0.96f, 0.22f));
            trackTexture = Solid(new Color(0.67f, 0.80f, 0.63f, 0.58f));

            BeginLegSession(TrainingLeg.Left, false, "startup");
            motionCapture?.LogGameDiagnostic("runtime_fix_session", JsonUtility.ToJson(settings));
        }

        private void Update()
        {
            if (motionCapture == null || leftEvaluator == null || rightEvaluator == null)
                return;

            float now = Time.realtimeSinceStartup;
            UpdateAutomaticLegSync(now);

            sample = motionCapture.ReadLowerBodyMeasurement(
                settings.sensorTimeoutSeconds,
                settings.maxSensorSkewSeconds,
                trainingLeg);

            rawRightKneeDeg = sample.RightKneeDeg;

            if (sessionPaused)
            {
                UpdateSeatedVisualAndGrounding(now);
                LogDiagnostic(now, "session_paused");
                return;
            }

            if (switchingLeg)
            {
                UpdateSwitching(now);
                UpdateSeatedVisualAndGrounding(now);
                LogDiagnostic(now, "switching");
                return;
            }

            bool transientPoseGap =
                ActiveEvaluator.HasReadyReference &&
                !sample.IsValid &&
                sample.CalibrationVersion > 0 &&
                (sample.FailureReason ?? "").Contains("请先完成站姿标定并开始动捕驱动");

            if (!transientPoseGap)
                ActiveEvaluator.Update(sample, now);

            UpdateSeatedVisualAndGrounding(now);
            HandleActionVoice();
            LogDiagnostic(now,
                transientPoseGap ? "pose_gap" : FailureCategory(ActiveEvaluator.BlockReason));
        }

        private void UpdateAutomaticLegSync(float now)
        {
            Quaternion[] q = motionCapture.TransformedQuaternions;
            if (q == null || q.Length <= LowerBodyPoseDriver.RightCalfIndex) return;

            Quaternion leftQ = q[LowerBodyPoseDriver.LeftCalfIndex];
            Quaternion rightQ = q[LowerBodyPoseDriver.RightCalfIndex];
            if (!hasMotionBaseline)
            {
                lastLeftCalfQ = leftQ;
                lastRightCalfQ = rightQ;
                hasMotionBaseline = true;
                return;
            }

            float leftDelta = Mathf.Min(45f, Quaternion.Angle(lastLeftCalfQ, leftQ));
            float rightDelta = Mathf.Min(45f, Quaternion.Angle(lastRightCalfQ, rightQ));
            lastLeftCalfQ = leftQ;
            lastRightCalfQ = rightQ;

            float decay = Mathf.Exp(-Mathf.Max(0.001f, Time.unscaledDeltaTime) * 3.2f);
            leftMotionScore = leftMotionScore * decay + leftDelta;
            rightMotionScore = rightMotionScore * decay + rightDelta;

            if (ActiveEvaluator != null &&
                (ActiveEvaluator.Stage == KneeExtensionStage.Holding ||
                 ActiveEvaluator.Stage == KneeExtensionStage.Returning))
            {
                pendingAutoLeg = TrainingLeg.Both;
                return;
            }

            TrainingLeg candidate = TrainingLeg.Both;
            if (leftMotionScore >= AutoLegMotionThresholdDeg &&
                leftMotionScore > rightMotionScore * AutoLegDominance)
                candidate = TrainingLeg.Left;
            else if (rightMotionScore >= AutoLegMotionThresholdDeg &&
                     rightMotionScore > leftMotionScore * AutoLegDominance)
                candidate = TrainingLeg.Right;

            if (candidate == TrainingLeg.Both || candidate == trainingLeg)
            {
                pendingAutoLeg = TrainingLeg.Both;
                return;
            }

            if (pendingAutoLeg != candidate)
            {
                pendingAutoLeg = candidate;
                pendingAutoLegSince = now;
                return;
            }

            if (now - pendingAutoLegSince < AutoLegConfirmSeconds) return;

            float leftScore = leftMotionScore;
            float rightScore = rightMotionScore;
            TrainingLeg from = trainingLeg;
            BeginLegSession(candidate, false, "auto_motion");
            motionCapture.LogGameDiagnostic(
                "auto_leg_sync",
                $"from={from}, to={candidate}, 07={leftScore:F1}, 09={rightScore:F1}");
        }

        private void UpdateSwitching(float now)
        {
            bool newSample = sample.IsValid &&
                             sample.SampleTimeSeconds > lastSwitchSampleTime + 0.0001f;
            if (newSample)
            {
                lastSwitchSampleTime = sample.SampleTimeSeconds;
                if (IsNaturalDownCandidate(sample))
                {
                    switchSamples++;
                    switchKneeSum += trainingLeg == TrainingLeg.Right
                        ? sample.RightKneeDeg
                        : sample.LeftKneeDeg;
                    switchKneeCount++;
                    switchThighSum += trainingLeg == TrainingLeg.Right
                        ? sample.RightThighDeg
                        : sample.LeftThighDeg;
                    switchThighCount++;
                }
                else
                {
                    switchSamples = 0;
                    switchKneeSum = 0f;
                    switchKneeCount = 0;
                    switchThighSum = 0f;
                    switchThighCount = 0;
                }
            }

            if (now - switchStartedAt < SwitchSettleSeconds ||
                switchSamples < SwitchRequiredSamples || !sample.IsValid)
                return;

            if (switchKneeCount > 0 && switchThighCount > 0)
            {
                int legIndex = trainingLeg == TrainingLeg.Right ? 1 : 0;
                float rawReadyDeg = switchKneeSum / switchKneeCount;
                float rawSeatedThighDeg = switchThighSum / switchThighCount;
                if (!seatedPoseEstablished)
                {
                    // 第一次真实稳定坐姿就是整场训练唯一的视觉基准。
                    // 两侧骨骼使用同样的髋、膝角，避免左右脚高度不一致。
                    sharedReadyKneeDeg = 90f;
                    sharedSeatedThighDeg = 90f;
                    seatedPoseEstablished = true;
                    seatedHeightLocked = false;
                    seatedHeightLockAt = now + SeatedHeightSettleSeconds;
                }

                float straightReference = trainingLeg == TrainingLeg.Right
                    ? RightRawStraightReferenceDeg
                    : LeftRawStraightReferenceDeg;
                motionCapture.ConfigureSeatedLegCalibration(
                    legIndex,
                    rawReadyDeg,
                    straightReference,
                    rawSeatedThighDeg,
                    sharedReadyKneeDeg,
                    sharedSeatedThighDeg);

                if (trainingLeg == TrainingLeg.Right)
                    rightRawReadyDeg = rawReadyDeg;
                motionCapture.LogGameDiagnostic(
                    "leg_pose_calibrated",
                    $"leg={trainingLeg}, rawReady={rawReadyDeg:F1}, " +
                    $"straightRef={straightReference:F1}, rawThigh={rawSeatedThighDeg:F1}, " +
                    $"sharedReady={sharedReadyKneeDeg:F1}, sharedThigh={sharedSeatedThighDeg:F1}");
            }

            switchingLeg = false;
            ActiveEvaluator.Reset(trainingLeg, false);
            previousStage = ActiveEvaluator.Stage;
            motionCapture.LogGameDiagnostic(
                "leg_ready",
                $"leg={trainingLeg}, sensors={SensorPairLabel()}");
        }

        private void UpdateSeatedVisualAndGrounding(float now)
        {
            if (seatedPoseEstablished && IsStandingPose(sample, trainingLeg))
            {
                if (float.IsNaN(standingCandidateSince))
                {
                    standingCandidateSince = now;
                }
                else if (now - standingCandidateSince >= StandingExitConfirmSeconds)
                {
                    ExitSeatedPoseForStanding();
                    return;
                }
            }
            else
            {
                standingCandidateSince = float.NaN;
            }

            int selectedLeg = trainingLeg == TrainingLeg.Right ? 1 : 0;
            bool holdBothLegs = switchingLeg || sessionPaused;
            motionCapture.SetSeatedTrainingVisual(
                seatedPoseEstablished,
                selectedLeg,
                holdBothLegs,
                sharedSeatedThighDeg,
                sharedReadyKneeDeg);

            if (!seatedPoseEstablished || holdBothLegs)
            {
                motionCapture.SetGroundedFeet(true, true);
            }
            else
            {
                // 训练腿抬起后不再参与高度求解；只以自然垂落的支撑腿为准。
                motionCapture.SetGroundedFeet(
                    trainingLeg == TrainingLeg.Right,
                    trainingLeg == TrainingLeg.Left);
            }

            // Keep solving against the supporting foot through sit/stand and
            // squat transitions. A saved root height cannot follow those poses.
            motionCapture.UnlockSeatedVerticalOffset();
            seatedHeightLocked = false;
        }

        private static bool IsStandingPose(LowerBodyMeasurement measurement, TrainingLeg leg)
        {
            if (!measurement.IsValid) return false;

            float thigh = leg == TrainingLeg.Right
                ? measurement.RightThighDeg
                : measurement.LeftThighDeg;
            float knee = leg == TrainingLeg.Right
                ? measurement.RightKneeDeg
                : measurement.LeftKneeDeg;
            return Mathf.Abs(thigh) <= StandingExitThighDeg &&
                   knee <= StandingExitKneeDeg;
        }

        private void ExitSeatedPoseForStanding()
        {
            seatedPoseEstablished = false;
            seatedHeightLocked = false;
            seatedHeightLockAt = float.PositiveInfinity;
            standingCandidateSince = float.NaN;
            sharedSeatedThighDeg = 90f;
            sharedReadyKneeDeg = 90f;
            motionCapture?.SetSeatedTrainingVisual(false, 0, false, 90f, 90f);
            motionCapture?.ClearSeatedLegCalibration(0);
            motionCapture?.ClearSeatedLegCalibration(1);
            motionCapture?.SetGroundedFeet(true, true);
            motionCapture?.UnlockSeatedVerticalOffset();
            motionCapture?.LogGameDiagnostic(
                "seated_pose_released_for_standing", $"leg={trainingLeg}");
            BeginLegSession(trainingLeg, false, "standing_exit");
        }

        private bool IsNaturalDownCandidate(LowerBodyMeasurement m)
        {
            if (!m.IsValid) return false;
            float thigh = trainingLeg == TrainingLeg.Right ? m.RightThighDeg : m.LeftThighDeg;
            float knee = trainingLeg == TrainingLeg.Right ? m.RightKneeDeg : m.LeftKneeDeg;
            return thigh >= settings.seatedThighMinDeg &&
                   thigh <= settings.seatedThighMaxDeg &&
                   knee >= 55f && knee <= 135f;
        }

        private void BeginLegSession(TrainingLeg leg, bool speak, string source)
        {
            sessionVersion++;
            trainingLeg = leg == TrainingLeg.Right ? TrainingLeg.Right : TrainingLeg.Left;
            ActiveEvaluator.Reset(trainingLeg, false);
            switchingLeg = true;
            switchStartedAt = Time.realtimeSinceStartup;
            lastSwitchSampleTime = float.NegativeInfinity;
            switchSamples = 0;
            switchKneeSum = 0f;
            switchKneeCount = 0;
            switchThighSum = 0f;
            switchThighCount = 0;
            pendingAutoLeg = TrainingLeg.Both;
            leftMotionScore = rightMotionScore = 0f;
            int selectedLeg = trainingLeg == TrainingLeg.Right ? 1 : 0;
            motionCapture?.ClearSeatedLegCalibration(selectedLeg);
            if (trainingLeg == TrainingLeg.Right)
                rightRawReadyDeg = float.NaN;
            previousStage = ActiveEvaluator.Stage;
            lastDiagnosticKey = "";

            motionCapture?.LogGameDiagnostic(
                "leg_switch",
                $"leg={trainingLeg}, sensors={SensorPairLabel()}, source={source}");

            if (speak)
                SpeakAction($"现在训练{LegName()}腿。请坐稳，小腿自然放下。", true);
        }

        private void HandleActionVoice()
        {
            KneeExtensionStage stage = ActiveEvaluator.Stage;
            if (stage == previousStage) return;

            switch (stage)
            {
                case KneeExtensionStage.Extending:
                    SpeakAction($"请慢慢抬起{LegName()}小腿，逐渐伸直膝盖。");
                    break;
                case KneeExtensionStage.Holding:
                    SpeakAction("很好，已经达到目标，请保持一下。");
                    break;
                case KneeExtensionStage.Returning:
                    SpeakAction($"很好，请慢慢放下{LegName()}小腿。");
                    break;
            }
            previousStage = stage;
        }

        private void SpeakAction(string text, bool force = false)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            float now = Time.realtimeSinceStartup;
            if (!force && now < nextSpeechAt) return;
            try
            {
                if (speechProcess != null)
                {
                    bool running = false;
                    try { running = !speechProcess.HasExited; } catch { }
                    if (running && !force) return;
                    if (running) { try { speechProcess.Kill(); } catch { } }
                    try { speechProcess.Dispose(); } catch { }
                    speechProcess = null;
                }

                string escaped = (text ?? "").Replace("'", "''");
                string command =
                    "Add-Type -AssemblyName System.Speech; " +
                    "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                    "$voices=$s.GetInstalledVoices() | Where-Object {$_.Enabled}; " +
                    "$v=$voices | Where-Object {$_.VoiceInfo.Culture.Name -like 'zh-*' -and $_.VoiceInfo.Gender -eq [System.Speech.Synthesis.VoiceGender]::Female} | Select-Object -First 1; " +
                    "if(-not $v){$v=$voices | Where-Object {$_.VoiceInfo.Gender -eq [System.Speech.Synthesis.VoiceGender]::Female} | Select-Object -First 1}; " +
                    "if($v){$s.SelectVoice($v.VoiceInfo.Name)}; " +
                    "$s.Rate=-4; $s.Volume=80; $s.Speak('" + escaped + "');";

                speechProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -WindowStyle Hidden -Command \"" + command + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                nextSpeechAt = now + SpeechGapSeconds;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[KneeGameVoice] " + e.Message);
            }
#endif
        }

        private void StopSpeech()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (speechProcess == null) return;
            try { if (!speechProcess.HasExited) speechProcess.Kill(); } catch { }
            try { speechProcess.Dispose(); } catch { }
            speechProcess = null;
#endif
        }

        private void LogDiagnostic(float now, string category)
        {
            var e = ActiveEvaluator;
            string key = $"{trainingLeg}:{e.Stage}:{e.CompletedRepetitions}:{category}:{switchingLeg}";
            if (key == lastDiagnosticKey && now < nextDiagnosticAt) return;

            motionCapture?.LogGameDiagnostic(
                key != lastDiagnosticKey ? "runtime_fix_transition" : "runtime_fix_sample",
                JsonUtility.ToJson(new Diagnostic
                {
                    leg = trainingLeg.ToString(),
                    stage = e.Stage.ToString(),
                    sensorPair = SensorPairLabel(),
                    repetitions = e.CompletedRepetitions,
                    holdSeconds = e.HoldSeconds,
                    readyReferenceKneeDeg = e.ReadyReferenceKneeDeg,
                    targetKneeDeg = e.TargetKneeDeg,
                    blockReason = switchingLeg ? SwitchPrompt() : e.BlockReason,
                    switching = switchingLeg,
                    sessionPaused = sessionPaused,
                    rawRightKneeDeg = rawRightKneeDeg,
                    rightRawReadyDeg = rightRawReadyDeg,
                    rightStraightReferenceDeg = RightRawStraightReferenceDeg,
                    seatedPoseEstablished = seatedPoseEstablished,
                    seatedHeightLocked = seatedHeightLocked,
                    sharedSeatedThighDeg = sharedSeatedThighDeg,
                    sharedReadyKneeDeg = sharedReadyKneeDeg,
                    measurement = sample
                }));
            lastDiagnosticKey = key;
            nextDiagnosticAt = now + 0.5f;
        }

        private void HandleFullReset()
        {
            leftEvaluator?.Reset(TrainingLeg.Left, true);
            rightEvaluator?.Reset(TrainingLeg.Right, true);
            hasMotionBaseline = false;
            rawRightKneeDeg = float.NaN;
            rightRawReadyDeg = float.NaN;
            seatedPoseEstablished = false;
            seatedHeightLocked = false;
            seatedHeightLockAt = float.PositiveInfinity;
            sharedSeatedThighDeg = 90f;
            sharedReadyKneeDeg = 90f;
            standingCandidateSince = float.NaN;
            motionCapture?.SetSeatedTrainingVisual(false, 0, false, 90f, 90f);
            motionCapture?.ClearSeatedLegCalibration(0);
            motionCapture?.ClearSeatedLegCalibration(1);
            motionCapture?.SetGroundedFeet(true, true);
            motionCapture?.UnlockSeatedVerticalOffset();
            if (leftEvaluator != null && rightEvaluator != null)
                BeginLegSession(trainingLeg, false, "full_reset");
        }

        private void OnDisable()
        {
            motionCapture?.SetSeatedTrainingVisual(false, 0, false, 90f, 90f);
            motionCapture?.SetGroundedFeet(true, true);
            motionCapture?.UnlockSeatedVerticalOffset();
            // 离开 S1 后恢复普通训练的逐帧根节点补偿。
            motionCapture?.SetRootMotionEnabledForTraining(true);
            StopSpeech();
            leftEvaluator?.Reset(TrainingLeg.Left, false);
            rightEvaluator?.Reset(TrainingLeg.Right, false);
        }

        private void OnDestroy()
        {
            if (motionUI != null)
                motionUI.OnResetRequested -= HandleFullReset;
            StopSpeech();
            DestroyTexture(panelTexture);
            DestroyTexture(accentTexture);
            DestroyTexture(paleTexture);
            DestroyTexture(lightTexture);
            DestroyTexture(trackTexture);
        }

        private void OnGUI()
        {
            if (!showTechnicalOverlay) return;
            if (leftEvaluator == null || rightEvaluator == null) return;
            EnsureStyles();

            float scale = Mathf.Max(0.1f,
                Mathf.Min(Screen.width / 1920f, Screen.height / 1080f));
            float w = Screen.width / scale;
            float h = Screen.height / scale;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);

            Rect panel = new Rect(20f, h - 252f, w - 40f, 232f);
            GUI.DrawTexture(panel, panelTexture);
            GUI.BeginGroup(panel);

            GUI.DrawTexture(new Rect(0f, 0f, panel.width, 48f), accentTexture);
            GUI.Label(new Rect(20f, 5f, 245f, 38f), "坐姿伸膝训练", headerStyle);
            GUI.Label(new Rect(270f, 10f, 760f, 28f), RequiredSensorSummary(), smallStyle);

            float buttonX = panel.width - 380f;
            DrawLegButton(new Rect(buttonX, 8f, 180f, 32f),
                TrainingLeg.Left, "左腿  06+07");
            DrawLegButton(new Rect(buttonX + 190f, 8f, 180f, 32f),
                TrainingLeg.Right, "右腿  08+09");

            var evaluator = ActiveEvaluator;
            bool blocked = switchingLeg || !string.IsNullOrEmpty(evaluator.BlockReason);
            GUI.Label(new Rect(30f, 68f, panel.width - 600f, 34f),
                switchingLeg ? SwitchPrompt() : (blocked ? FriendlyBlockReason() : Instruction()),
                instructionStyle);

            float contentWidth = panel.width - 590f;
            GUI.Label(new Rect(22f, 119f, 180f, 24f), "抬腿进度", titleStyle);
            Rect progress = new Rect(22f, 151f, contentWidth - 44f, 18f);
            GUI.DrawTexture(progress, trackTexture);
            float ratio = LiftProgress();
            if (ratio > 0f)
                GUI.DrawTexture(new Rect(progress.x, progress.y,
                    progress.width * ratio, progress.height), accentTexture);

            GUI.Label(new Rect(22f, 174f, contentWidth - 44f, 24f),
                ProgressCaption(), bodyStyle);
            GUI.Label(new Rect(22f, 201f, contentWidth - 170f, 24f),
                StageCaption(), stageStyle);

            if (GUI.Button(new Rect(contentWidth - 126f, 196f, 112f, 28f), "重置次数"))
            {
                evaluator.Reset(trainingLeg, true);
                BeginLegSession(trainingLeg, false, "count_reset");
            }

            float cardsX = panel.width - 548f;
            DrawCard(new Rect(cardsX, 61f, 168f, 155f), "当前膝角", CurrentAngleText());
            DrawCard(new Rect(cardsX + 180f, 61f, 168f, 155f),
                "完成次数", evaluator.CompletedRepetitions + " 次");
            DrawCard(new Rect(cardsX + 360f, 61f, 168f, 155f),
                "保持计时", evaluator.HoldSeconds.ToString("F1") + " / " +
                settings.holdSeconds.ToString("F0") + " 秒");

            GUI.EndGroup();
            GUI.matrix = old;
        }

        private void DrawLegButton(Rect rect, TrainingLeg leg, string text)
        {
            bool selected = trainingLeg == leg;
            GUIStyle style = new GUIStyle(GUI.skin.button)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            // 选中项使用浅绿色；未选中项与标题栏背景融为一体。
            style.normal.background = selected ? paleTexture : accentTexture;
            style.normal.textColor = selected ? DarkGreen : Color.white;
            style.hover.background = selected ? paleTexture : accentTexture;
            style.hover.textColor = selected ? DarkGreen : Color.white;
            style.active.background = paleTexture;
            style.active.textColor = DarkGreen;

            if (GUI.Button(rect, text, style) && !selected)
                BeginLegSession(leg, true, "manual_button");
        }

        private void DrawCard(Rect rect, string title, string value)
        {
            Color old = GUI.color;
            GUI.color = Green;
            GUI.DrawTexture(new Rect(rect.x, rect.y, 6f, rect.height), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(rect.x + 18f, rect.y + 17f,
                rect.width - 28f, 28f), title, titleStyle);
            GUI.Label(new Rect(rect.x + 16f, rect.y + 56f,
                rect.width - 25f, 70f), value, valueStyle);
        }

        private float LiftProgress()
        {
            var e = ActiveEvaluator;
            if (switchingLeg || !sample.IsValid || !e.HasReadyReference) return 0f;
            return Mathf.Clamp01((e.ReadyReferenceKneeDeg - SelectedKneeAngle()) /
                                 Mathf.Max(1f, settings.minimumExtensionDeg));
        }

        private string ProgressCaption()
        {
            if (switchingLeg)
                return $"正在确认{LegName()}腿（{SensorPairLabel()}），请让小腿自然下垂";
            var e = ActiveEvaluator;
            if (!e.HasReadyReference)
                return "小腿自然放下，系统会自动记录准备角度";
            return $"准备角度 {e.ReadyReferenceKneeDeg:F0}°  ·  " +
                   $"训练达标角度 ≤ {e.TargetKneeDeg:F0}°";
        }

        private string StageCaption()
        {
            if (switchingLeg)
                return $"● 当前{LegName()}腿：{SensorPairLabel()}（自动跟随活动腿）";
            switch (ActiveEvaluator.Stage)
            {
                case KneeExtensionStage.Preparing: return "● 准备：坐稳并自然放下小腿";
                case KneeExtensionStage.Extending: return "● 抬腿：慢慢伸直所选小腿";
                case KneeExtensionStage.Holding: return "● 保持：达到目标后保持3秒";
                default: return "● 放下：回到准备姿势后完成一次";
            }
        }

        private string Instruction()
        {
            switch (ActiveEvaluator.Stage)
            {
                case KneeExtensionStage.Preparing:
                    return $"{LegName()}腿：请坐稳，小腿自然放下";
                case KneeExtensionStage.Extending:
                    return $"{LegName()}腿：请慢慢抬起小腿，逐渐伸直膝盖";
                case KneeExtensionStage.Holding:
                    return "很好，已经达到目标，请保持当前姿势";
                default:
                    return $"{LegName()}腿：请慢慢放下小腿";
            }
        }

        private string FriendlyBlockReason()
        {
            string reason = ActiveEvaluator.BlockReason ?? "";
            if (reason.Contains("信号") || reason.Contains("超时") ||
                reason.Contains("时间差") || reason.Contains("未持续更新"))
                return $"等待 {SensorPairLabel()} 数据恢复，可以先放松。";
            return reason;
        }

        private string SwitchPrompt() =>
            $"当前训练：{LegName()}腿（{SensorPairLabel()}）。请坐稳并让小腿自然下垂。";

        private string RequiredSensorSummary()
        {
            if (trainingLeg == TrainingLeg.Left)
                return $"当前：左腿 | 06大腿 + 07小腿 | 自动跟随活动腿 | 06 {Status(0)} 07 {Status(1)}";
            return $"当前：右腿 | 08大腿 + 09小腿 | 自动跟随活动腿 | 08 {Status(2)} 09 {Status(3)}";
        }

        private string SensorPairLabel() => trainingLeg == TrainingLeg.Left
            ? "06大腿 + 07小腿"
            : "08大腿 + 09小腿";

        private string LegName() => trainingLeg == TrainingLeg.Right ? "右" : "左";

        private string Status(int bit)
        {
            if ((sample.FreshMask & (1 << bit)) != 0) return "在线";
            float age = sample.GetSensorAgeSeconds(bit);
            return age < 0f ? "等待" : $"等待 {age:F1}s";
        }

        private float SelectedKneeAngle() => trainingLeg == TrainingLeg.Right
            ? sample.RightKneeDeg
            : sample.LeftKneeDeg;

        private string CurrentAngleText()
        {
            if (switchingLeg) return "确认中";
            if (!sample.IsValid) return "--";
            float angle = SelectedKneeAngle();
            bool reached = ActiveEvaluator.HasReadyReference &&
                           angle <= ActiveEvaluator.TargetKneeDeg;
            return reached ? angle.ToString("F0") + "°\n已达标" : angle.ToString("F0") + "°";
        }

        private static string FailureCategory(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "ok";
            if (reason.Contains("超时")) return "timeout";
            if (reason.Contains("时间差")) return "skew";
            if (reason.Contains("坐稳") || reason.Contains("坐位")) return "seated";
            if (reason.Contains("角度无效") || reason.Contains("数据异常")) return "invalid";
            return reason;
        }

        private void EnsureStyles()
        {
            if (headerStyle != null) return;
            headerStyle = Label(30, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            instructionStyle = Label(25, FontStyle.Bold, DarkGreen, TextAnchor.MiddleLeft);
            bodyStyle = Label(19, FontStyle.Normal, DarkGreen, TextAnchor.MiddleLeft);
            smallStyle = Label(17, FontStyle.Normal, Color.white, TextAnchor.MiddleLeft);
            valueStyle = Label(36, FontStyle.Bold, Green, TextAnchor.MiddleCenter);
            titleStyle = Label(19, FontStyle.Bold, DarkGreen, TextAnchor.MiddleLeft);
            stageStyle = Label(20, FontStyle.Bold, Green, TextAnchor.MiddleLeft);
        }

        private static GUIStyle Label(int size, FontStyle fontStyle, Color color, TextAnchor alignment)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = fontStyle,
                alignment = alignment,
                wordWrap = true
            };
            style.normal.textColor = color;
            return style;
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static void DestroyTexture(Texture2D texture)
        {
            if (texture != null) Destroy(texture);
        }

        [Serializable]
        private sealed class Diagnostic
        {
            public string leg, stage, sensorPair, blockReason;
            public int repetitions;
            public bool switching, sessionPaused, seatedPoseEstablished, seatedHeightLocked;
            public float holdSeconds, readyReferenceKneeDeg, targetKneeDeg;
            public float rawRightKneeDeg, rightRawReadyDeg, rightStraightReferenceDeg;
            public float sharedSeatedThighDeg, sharedReadyKneeDeg;
            public LowerBodyMeasurement measurement;
        }
    }
}
