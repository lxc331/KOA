using UnityEngine;

namespace RehabPhotoGame
{
    /// <summary>第1段联调面板。仅在挂载此组件的场景运行，不自动注入动捕测试场景。</summary>
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public sealed class SeatedKneeExtensionPrototype : MonoBehaviour
    {
        [SerializeField] private MotionCaptureController motionCapture;
        [SerializeField] private TrainingLeg trainingLeg = TrainingLeg.Left;
        [SerializeField] private SeatedKneeExtensionSettings settings = new SeatedKneeExtensionSettings();

        private SeatedKneeExtensionEvaluator evaluator;
        private LowerBodyMeasurement sample;
        private float nextDiagnosticAt;
        private string lastDiagnosticKey;
        private GUIStyle labelStyle, titleStyle, buttonStyle;

        private void Start()
        {
            if (motionCapture == null) motionCapture = FindObjectOfType<MotionCaptureController>();
            evaluator = new SeatedKneeExtensionEvaluator(settings, trainingLeg);
            motionCapture?.LogGameDiagnostic("session", JsonUtility.ToJson(settings));
        }

        private void Update()
        {
            if (evaluator == null) return;
            if (evaluator.Leg != trainingLeg) evaluator.Reset(trainingLeg, true);
            sample = motionCapture != null && settings != null
                ? motionCapture.ReadLowerBodyMeasurement(settings.sensorTimeoutSeconds, settings.maxSensorSkewSeconds)
                : new LowerBodyMeasurement { FailureReason = "缺少动捕控制器或技术验证配置" };
            evaluator.Update(sample, Time.realtimeSinceStartup);

            // 每次阶段/错误变化立即记文件，平时 2 Hz 快照；不向屏幕追加日志。
            string key = evaluator.Stage + ":" + trainingLeg + ":" + evaluator.CompletedRepetitions + ":" + evaluator.BlockReason;
            if (key != lastDiagnosticKey || Time.realtimeSinceStartup >= nextDiagnosticAt)
            {
                motionCapture?.LogGameDiagnostic(key != lastDiagnosticKey ? "transition" : "sample",
                    JsonUtility.ToJson(new Diagnostic
                    {
                        leg = trainingLeg.ToString(), stage = evaluator.Stage.ToString(),
                        repetitions = evaluator.CompletedRepetitions, holdSeconds = evaluator.HoldSeconds,
                        blockReason = evaluator.BlockReason, measurement = sample
                    }));
                lastDiagnosticKey = key;
                nextDiagnosticAt = Time.realtimeSinceStartup + 0.5f;
            }
        }

        private void OnDisable()
        {
            evaluator?.Reset(trainingLeg, false);
            motionCapture?.LogGameDiagnostic("disabled", "当前未完成动作已取消");
        }

        private void OnGUI()
        {
            if (evaluator == null) return;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
                titleStyle = new GUIStyle(labelStyle) { fontSize = 24, fontStyle = FontStyle.Bold };
                buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            }
            Matrix4x4 previous = GUI.matrix;
            float scale = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.Scale(Vector3.one * Mathf.Max(0.1f, scale));
            GUILayout.BeginArea(new Rect(Screen.width / Mathf.Max(0.1f, scale) - 484, 20, 464, 600), GUI.skin.box);
            GUILayout.Label("第1段 · 坐位膝伸验证", titleStyle);
            GUILayout.Label("先站姿标定并开始驱动，再坐下准备。", labelStyle);
            int selected = GUILayout.Toolbar((int)trainingLeg, new[] { "左腿", "右腿", "双腿" }, buttonStyle);
            if (selected != (int)trainingLeg)
            {
                trainingLeg = (TrainingLeg)selected;
                evaluator.Reset(trainingLeg, true);
            }
            GUILayout.Label(SensorStatus(), labelStyle);
            GUILayout.Label(sample.IsValid
                ? $"左膝 {sample.LeftKneeDeg:F1}°    右膝 {sample.RightKneeDeg:F1}°\n左大腿 {sample.LeftThighDeg:F1}°    右大腿 {sample.RightThighDeg:F1}°"
                : "左膝 --    右膝 --\n左大腿 --    右大腿 --", labelStyle);
            GUILayout.Label("膝角：0° = 伸直，90° ≈ 坐位弯曲", labelStyle);
            GUILayout.Space(8);
            GUILayout.Label("当前阶段：" + StageName(evaluator.Stage), titleStyle);
            GUILayout.Label(string.IsNullOrEmpty(evaluator.BlockReason) ? Instruction() : evaluator.BlockReason, labelStyle);
            if (settings != null)
            {
                GUILayout.Label($"目标：膝角 ≤ {settings.targetKneeMaxDeg:F0}°\n连续稳定保持：{evaluator.HoldSeconds:F1} / {settings.holdSeconds:F1} 秒", labelStyle);
                GUILayout.Label($"返回范围：{settings.readyKneeMinDeg:F0}～{settings.readyKneeMaxDeg:F0}°", labelStyle);
            }
            GUILayout.Label($"已完成：{evaluator.CompletedRepetitions} 次", titleStyle);
            if (GUILayout.Button("重置本项计数", buttonStyle))
            {
                evaluator.Reset(trainingLeg, true);
                motionCapture?.LogGameDiagnostic("reset", "用户重置技术验证计数");
            }
            GUILayout.Label("保持达标后放回小腿，才计一次。", labelStyle);
            GUILayout.EndArea();
            GUI.matrix = previous;
        }

        private string SensorStatus()
        {
            return $"06 {Status(0)}   07 {Status(1)}   08 {Status(2)}   09 {Status(3)}";
        }

        private string Status(int bit) => (sample.FreshMask & (1 << bit)) != 0 ? "在线" : "等待";

        private string Instruction()
        {
            switch (evaluator.Stage)
            {
                case KneeExtensionStage.Preparing: return "坐好，小腿放下，短暂保持准备姿势。";
                case KneeExtensionStage.Extending: return "缓慢伸直所选小腿，大腿保持坐位。";
                case KneeExtensionStage.Holding: return "保持当前角度；离开目标或晃动将重新计时。";
                default: return "已达到保持要求，请放回小腿。";
            }
        }

        private static string StageName(KneeExtensionStage stage)
        {
            switch (stage)
            {
                case KneeExtensionStage.Preparing: return "准备";
                case KneeExtensionStage.Extending: return "伸膝";
                case KneeExtensionStage.Holding: return "保持";
                default: return "返回";
            }
        }

        [System.Serializable]
        private sealed class Diagnostic
        {
            public string leg, stage, blockReason;
            public int repetitions;
            public float holdSeconds;
            public LowerBodyMeasurement measurement;
        }
    }
}
