using System;
using UnityEngine;

namespace RehabPhotoGame
{
    [DefaultExecutionOrder(960)]
    internal static class ShallowSquatRuntimeBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SeatedKneeExtensionPrototype host =
                UnityEngine.Object.FindObjectOfType<SeatedKneeExtensionPrototype>();
            if (host != null && host.GetComponent<ShallowSquatRuntime>() == null)
                host.gameObject.AddComponent<ShallowSquatRuntime>();
        }
    }

    /// <summary>
    /// 第4段浅蹲运行时适配层：复用四传感器采样、健康状态、人物驱动、
    /// 根节点贴地和统一 JSONL 诊断日志。
    /// </summary>
    [DefaultExecutionOrder(1120)]
    [DisallowMultipleComponent]
    public sealed class ShallowSquatRuntime : MonoBehaviour
    {
        [SerializeField, Tooltip("为空时自动加载 Resources/RehabPhotoGame/ShallowSquatGameTuning。")]
        private ShallowSquatGameTuning gameTuning;
        private ShallowSquatSettings settings = new ShallowSquatSettings();

        private MotionCaptureController motionCapture;
        private MotionCaptureUI motionUI;
        private ShallowSquatEvaluator evaluator;
        private LowerBodyMeasurement sample;
        private bool modeActive;
        private bool sessionPaused = true;
        private int sessionVersion;
        private float nextDiagnosticAt;
        private string lastDiagnosticKey = "";

        public float SensorTimeoutSeconds => settings != null
            ? settings.sensorTimeoutSeconds
            : 2.2f;

        public ShallowSquatTrainingSnapshot CurrentSnapshot
        {
            get
            {
                if (evaluator == null || settings == null)
                {
                    return new ShallowSquatTrainingSnapshot
                    {
                        Stage = ShallowSquatStage.Preparing,
                        SessionVersion = sessionVersion,
                        IsModeActive = modeActive,
                        IsSessionPaused = true,
                        BlockReason = evaluator == null ? "浅蹲训练模块正在初始化"
                            : "浅蹲参数无效，请检查游戏联调配置"
                    };
                }

                return new ShallowSquatTrainingSnapshot
                {
                    Stage = evaluator.Stage,
                    SessionVersion = sessionVersion,
                    IsModeActive = modeActive,
                    IsSessionPaused = sessionPaused,
                    IsDataValid = modeActive && !sessionPaused && sample.IsValid,
                    HasStandingReference = evaluator.HasStandingReference,
                    SignalPaused = evaluator.SignalPaused,
                    CoordinationWarning = evaluator.CoordinationWarning,
                    LeftKneeFlexionDeg = evaluator.LeftKneeFlexionDeg,
                    RightKneeFlexionDeg = evaluator.RightKneeFlexionDeg,
                    CompletedRepetitions = evaluator.CompletedRepetitions,
                    CaptureSequence = evaluator.CaptureSequence,
                    DropProgress01 = evaluator.DropProgress01,
                    LeftProgress01 = evaluator.LeftProgress01,
                    RightProgress01 = evaluator.RightProgress01,
                    HoldSeconds = evaluator.HoldSeconds,
                    HoldDurationSeconds = settings.holdSeconds,
                    LeftThighDeg = sample.LeftThighDeg,
                    LeftKneeDeg = sample.LeftKneeDeg,
                    RightThighDeg = sample.RightThighDeg,
                    RightKneeDeg = sample.RightKneeDeg,
                    BlockReason = !modeActive
                        ? "浅蹲训练未启用"
                        : sessionPaused ? "训练已暂停，可以先站稳休息" : evaluator.BlockReason
                };
            }
        }

        private void Start()
        {
            motionCapture = FindObjectOfType<MotionCaptureController>();
            motionUI = motionCapture != null
                ? motionCapture.GetComponent<MotionCaptureUI>()
                : null;
            if (gameTuning == null)
                gameTuning = Resources.Load<ShallowSquatGameTuning>(
                    ShallowSquatGameTuning.ResourcePath);
            settings = gameTuning != null
                ? gameTuning.CreateRuntimeSettings()
                : new ShallowSquatSettings();
            evaluator = new ShallowSquatEvaluator(settings);
            evaluator.Reset(true);
            motionCapture?.LogTrainingDiagnostic("shallow_squat", "game_tuning",
                settings != null ? JsonUtility.ToJson(settings) : "invalid_null_settings");
            if (motionUI != null)
                motionUI.OnResetRequested += HandleFullReset;
        }

        private void Update()
        {
            if (!modeActive || motionCapture == null || evaluator == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (settings == null || !settings.IsValid)
            {
                sample = new LowerBodyMeasurement
                {
                    IsValid = false,
                    FailureReason = "浅蹲参数无效，请检查游戏联调配置"
                };
                if (!sessionPaused) evaluator.Update(sample, now);
                LogDiagnostic(now);
                return;
            }
            sample = motionCapture.ReadLowerBodyMeasurement(
                settings.sensorTimeoutSeconds,
                settings.maxSensorSkewSeconds,
                TrainingLeg.Both);
            if (!sessionPaused)
                evaluator.Update(sample, now);
            LogDiagnostic(now);
        }

        public void SetModeActive(bool active, string source)
        {
            if (modeActive == active) return;
            modeActive = active;
            sessionPaused = true;
            sessionVersion++;
            evaluator?.Reset(true);
            lastDiagnosticKey = "";

            if (active)
                PrepareMotionForShallowSquat();

            motionCapture?.LogTrainingDiagnostic(
                "shallow_squat",
                active ? "mode_enter" : "mode_exit",
                $"session={sessionVersion}, source={source ?? "formal_ui"}");
        }

        public void SetSessionPaused(bool paused, string source)
        {
            if (!modeActive || sessionPaused == paused) return;
            sessionPaused = paused;
            sessionVersion++;
            evaluator?.Reset(!paused);
            lastDiagnosticKey = "";
            if (!paused)
                PrepareMotionForShallowSquat();

            motionCapture?.LogTrainingDiagnostic(
                "shallow_squat",
                paused ? "session_paused" : "session_resumed",
                $"session={sessionVersion}, source={source ?? "formal_ui"}");
        }

        private void PrepareMotionForShallowSquat()
        {
            // 浅蹲使用真实双腿姿态和双脚贴地，不能继承 S1 的坐姿视觉映射。
            motionCapture?.SetSeatedTrainingVisual(false, 0, false, 90f, 90f);
            motionCapture?.ClearSeatedLegCalibration(0);
            motionCapture?.ClearSeatedLegCalibration(1);
            motionCapture?.SetGroundedFeet(true, true);
            motionCapture?.UnlockSeatedVerticalOffset();
            motionCapture?.SetRootMotionEnabledForTraining(true);
        }

        private void HandleFullReset()
        {
            evaluator?.Reset(true);
            sessionVersion++;
            lastDiagnosticKey = "";
            if (modeActive)
                PrepareMotionForShallowSquat();
        }

        private void LogDiagnostic(float now)
        {
            if (motionCapture == null || evaluator == null)
                return;

            string key = $"{evaluator.Stage}|{sessionPaused}|{sample.IsValid}|" +
                         $"{evaluator.SignalPaused}|{evaluator.CaptureSequence}|{evaluator.BlockReason}";
            if (key != lastDiagnosticKey || now >= nextDiagnosticAt)
            {
                ShallowSquatTrainingSnapshot snapshot = CurrentSnapshot;
                motionCapture.LogTrainingDiagnostic(
                    "shallow_squat",
                    "snapshot",
                    JsonUtility.ToJson(new ShallowSquatDiagnostic
                    {
                        stage = snapshot.Stage.ToString(),
                        session = snapshot.SessionVersion,
                        sessionPaused = snapshot.IsSessionPaused,
                        repetitions = snapshot.CompletedRepetitions,
                        captureSequence = snapshot.CaptureSequence,
                        progress = snapshot.DropProgress01,
                        leftProgress = snapshot.LeftProgress01,
                        rightProgress = snapshot.RightProgress01,
                        holdSeconds = snapshot.HoldSeconds,
                        hasStandingReference = snapshot.HasStandingReference,
                        coordinationWarning = snapshot.CoordinationWarning,
                        leftKneeFlexion = snapshot.LeftKneeFlexionDeg,
                        rightKneeFlexion = snapshot.RightKneeFlexionDeg,
                        blockReason = snapshot.BlockReason,
                        measurement = sample
                    }));
                lastDiagnosticKey = key;
                nextDiagnosticAt = now + 0.5f;
            }
        }

        private void OnDisable()
        {
            if (motionUI != null)
                motionUI.OnResetRequested -= HandleFullReset;
            modeActive = false;
            sessionPaused = true;
        }

        [Serializable]
        private sealed class ShallowSquatDiagnostic
        {
            public string stage, blockReason;
            public int session, repetitions, captureSequence;
            public bool sessionPaused;
            public bool hasStandingReference, coordinationWarning;
            public float leftKneeFlexion, rightKneeFlexion;
            public float progress, leftProgress, rightProgress, holdSeconds;
            public LowerBodyMeasurement measurement;
        }
    }
}
