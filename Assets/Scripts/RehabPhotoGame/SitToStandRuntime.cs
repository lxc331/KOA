using System;
using UnityEngine;

namespace RehabPhotoGame
{
    [DefaultExecutionOrder(950)]
    internal static class SitToStandRuntimeBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SeatedKneeExtensionPrototype host =
                UnityEngine.Object.FindObjectOfType<SeatedKneeExtensionPrototype>();
            if (host != null && host.GetComponent<SitToStandRuntime>() == null)
                host.gameObject.AddComponent<SitToStandRuntime>();
        }
    }

    /// <summary>
    /// 第3段坐站运行时适配层：复用 MotionCaptureController 的四传感器采样、
    /// 帧龄健康状态、人物驱动、根节点贴地和 JSONL 日志。
    /// </summary>
    [DefaultExecutionOrder(1100)]
    [DisallowMultipleComponent]
    public sealed class SitToStandRuntime : MonoBehaviour
    {
        [SerializeField] private SitToStandSettings settings = new SitToStandSettings();

        private MotionCaptureController motionCapture;
        private MotionCaptureUI motionUI;
        private SitToStandEvaluator evaluator;
        private LowerBodyMeasurement sample;
        private bool modeActive;
        private bool sessionPaused = true;
        private int sessionVersion;
        private float nextDiagnosticAt;
        private string lastDiagnosticKey = "";

        public float SensorTimeoutSeconds => settings != null
            ? settings.sensorTimeoutSeconds
            : 2.2f;

        public SitToStandTrainingSnapshot CurrentSnapshot
        {
            get
            {
                if (evaluator == null || settings == null)
                {
                    return new SitToStandTrainingSnapshot
                    {
                        Stage = SitToStandStage.Preparing,
                        SessionVersion = sessionVersion,
                        IsModeActive = modeActive,
                        IsSessionPaused = true,
                        BlockReason = "坐站训练模块正在初始化"
                    };
                }

                return new SitToStandTrainingSnapshot
                {
                    Stage = evaluator.Stage,
                    SessionVersion = sessionVersion,
                    IsModeActive = modeActive,
                    IsSessionPaused = sessionPaused,
                    IsDataValid = modeActive && !sessionPaused && sample.IsValid,
                    HasSeatedReference = evaluator.HasSeatedReference,
                    SignalPaused = evaluator.SignalPaused,
                    CompletedRepetitions = evaluator.CompletedRepetitions,
                    CaptureSequence = evaluator.CaptureSequence,
                    RaiseProgress01 = evaluator.RaiseProgress01,
                    LeftProgress01 = evaluator.LeftProgress01,
                    RightProgress01 = evaluator.RightProgress01,
                    HoldSeconds = evaluator.HoldSeconds,
                    HoldDurationSeconds = settings.holdSeconds,
                    LeftThighDeg = sample.LeftThighDeg,
                    LeftKneeDeg = sample.LeftKneeDeg,
                    RightThighDeg = sample.RightThighDeg,
                    RightKneeDeg = sample.RightKneeDeg,
                    BlockReason = !modeActive
                        ? "坐站训练未启用"
                        : sessionPaused ? "训练已暂停，可以先坐稳休息" : evaluator.BlockReason
                };
            }
        }

        private void Start()
        {
            motionCapture = FindObjectOfType<MotionCaptureController>();
            motionUI = motionCapture != null
                ? motionCapture.GetComponent<MotionCaptureUI>()
                : null;
            evaluator = new SitToStandEvaluator(settings);
            evaluator.Reset(true);
            if (motionUI != null)
                motionUI.OnResetRequested += HandleFullReset;
        }

        private void Update()
        {
            if (!modeActive || motionCapture == null || evaluator == null)
                return;

            float now = Time.realtimeSinceStartup;
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
                PrepareMotionForSitToStand();

            motionCapture?.LogTrainingDiagnostic(
                "sit_to_stand",
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
                PrepareMotionForSitToStand();

            motionCapture?.LogTrainingDiagnostic(
                "sit_to_stand",
                paused ? "session_paused" : "session_resumed",
                $"session={sessionVersion}, source={source ?? "formal_ui"}");
        }

        private void PrepareMotionForSitToStand()
        {
            // 释放 S1 的坐姿视觉映射，改回真实双腿姿态；双脚共同参与贴地。
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
                PrepareMotionForSitToStand();
        }

        private void LogDiagnostic(float now)
        {
            if (motionCapture == null || evaluator == null || now < nextDiagnosticAt)
                return;

            string key = $"{evaluator.Stage}|{sessionPaused}|{sample.IsValid}|" +
                         $"{evaluator.SignalPaused}|{evaluator.CaptureSequence}|{evaluator.BlockReason}";
            if (key != lastDiagnosticKey || now >= nextDiagnosticAt)
            {
                SitToStandTrainingSnapshot snapshot = CurrentSnapshot;
                motionCapture.LogTrainingDiagnostic(
                    "sit_to_stand",
                    "snapshot",
                    JsonUtility.ToJson(new SitToStandDiagnostic
                    {
                        stage = snapshot.Stage.ToString(),
                        session = snapshot.SessionVersion,
                        sessionPaused = snapshot.IsSessionPaused,
                        repetitions = snapshot.CompletedRepetitions,
                        captureSequence = snapshot.CaptureSequence,
                        progress = snapshot.RaiseProgress01,
                        leftProgress = snapshot.LeftProgress01,
                        rightProgress = snapshot.RightProgress01,
                        holdSeconds = snapshot.HoldSeconds,
                        blockReason = snapshot.BlockReason,
                        measurement = sample
                    }));
                lastDiagnosticKey = key;
            }
            nextDiagnosticAt = now + 0.5f;
        }

        private void OnDisable()
        {
            if (motionUI != null)
                motionUI.OnResetRequested -= HandleFullReset;
            modeActive = false;
            sessionPaused = true;
        }

        [Serializable]
        private sealed class SitToStandDiagnostic
        {
            public string stage, blockReason;
            public int session, repetitions, captureSequence;
            public bool sessionPaused;
            public float progress, leftProgress, rightProgress, holdSeconds;
            public LowerBodyMeasurement measurement;
        }
    }
}
