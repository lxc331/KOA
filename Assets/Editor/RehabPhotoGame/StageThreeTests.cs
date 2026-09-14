using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RehabPhotoGame.Editor
{
    /// <summary>第3段坐站摄影的纯逻辑、资源和正式界面回归测试。</summary>
    public static class StageThreeTests
    {
        [MenuItem("Tools/Rehab Photo Game/Run Stage 3 Regression Tests")]
        public static void RunAll()
        {
            StageOneTests.RunAll();
            RunLogicTests();
            Run("B_高处素材可由 Resources 加载", HighPhotoResources);
            Run("正式界面可切换到坐站摄影模式", FormalModeSmoke);
            Debug.Log("[Stage3 tests] PASS: 10 Stage 3 scenarios after 34 baseline scenarios.");
        }

        public static void RunLogicTests()
        {
            Run("坐站配置默认值有效", SettingsValidation);
            Run("未经坐姿准备不能直接拍摄", MustPrepareSeated);
            Run("单腿前踢不能抬高取景框或触发快门", SingleLegCannotRaiseOrCapture);
            Run("双腿协同站起只拍一次且必须坐回", CoordinatedRiseCapturesOnceAndRequiresReturn);
            Run("短时断流冻结坐站进度和保持计时", DropoutFreezesProgressAndHold);
            Run("长时间断流取消当前坐站动作", LongDropoutResetsCycle);
            Run("重复旧样本不能累计站稳时间", RepeatedSampleCannotCompleteHold);
            Run("重标定会取消旧坐站动作", RecalibrationCancelsCycle);
        }

        private static void SettingsValidation()
        {
            var settings = new SitToStandSettings();
            Check(settings.IsValid, "第3段默认配置无效");
            settings.maxLegProgressDifference = 1.1f;
            Check(!settings.IsValid, "双腿同步差阈值超出范围仍被接受");
        }

        private static void MustPrepareSeated()
        {
            var trial = new Trial();
            trial.Stand(20);
            Equal(SitToStandStage.Preparing, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(0f, trial.Evaluator.RaiseProgress01);
        }

        private static void SingleLegCannotRaiseOrCapture()
        {
            var trial = new Trial();
            trial.Prepare();
            Equal(SitToStandStage.Rising, trial.Evaluator.Stage);

            // 最接近客户旧问题的场景：只有左小腿前踢，大腿保持坐姿。
            trial.Feed(90f, 15f, 90f, 90f, 10);
            Equal(SitToStandStage.Rising, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(0f, trial.Evaluator.RaiseProgress01);

            // 即使整条左腿变化，右腿没有跟随时仍不能推进总进度。
            trial.Feed(20f, 15f, 90f, 90f, 30);
            Equal(SitToStandStage.Rising, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(0f, trial.Evaluator.RaiseProgress01);
            Near(1f, trial.Evaluator.LeftProgress01);
            Near(0f, trial.Evaluator.RightProgress01);
            Check(trial.Evaluator.BlockReason.Contains("同步"),
                "单腿动作没有给出双腿同步提示");
        }

        private static void CoordinatedRiseCapturesOnceAndRequiresReturn()
        {
            var trial = new Trial();
            trial.Prepare();
            trial.Stand(12);
            Equal(SitToStandStage.Returning, trial.Evaluator.Stage);
            Equal(1, trial.Evaluator.CaptureSequence);
            Near(1f, trial.Evaluator.RaiseProgress01);

            trial.Stand(30);
            Equal(1, trial.Evaluator.CaptureSequence);
            Equal(0, trial.Evaluator.CompletedRepetitions);

            trial.Sit(5);
            Equal(1, trial.Evaluator.CompletedRepetitions);
            Equal(SitToStandStage.Preparing, trial.Evaluator.Stage);

            trial.Prepare();
            trial.Stand(12);
            Equal(2, trial.Evaluator.CaptureSequence);
        }

        private static void DropoutFreezesProgressAndHold()
        {
            var trial = new Trial();
            trial.Prepare();
            trial.Stand(6);
            Equal(SitToStandStage.Holding, trial.Evaluator.Stage);
            float progress = trial.Evaluator.RaiseProgress01;
            float hold = trial.Evaluator.HoldSeconds;

            trial.Dropout(7, "传感器数据超时");
            trial.Dropout(7, "传感器数据超时");
            Equal(SitToStandStage.Holding, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(progress, trial.Evaluator.RaiseProgress01);
            Near(hold, trial.Evaluator.HoldSeconds);
            Check(trial.Evaluator.SignalPaused, "短时断流没有进入冻结状态");

            trial.Stand(10);
            Equal(1, trial.Evaluator.CaptureSequence);
        }

        private static void LongDropoutResetsCycle()
        {
            var trial = new Trial();
            trial.Prepare();
            trial.Feed(55f, 55f, 55f, 55f, 2);
            Check(trial.Evaluator.HasSeatedReference, "准备完成后没有坐姿参考");

            trial.Dropout(7, "传感器数据超时", 22);
            Equal(SitToStandStage.Preparing, trial.Evaluator.Stage);
            Check(!trial.Evaluator.HasSeatedReference, "长断流后仍保留旧坐姿参考");
            Equal(0, trial.Evaluator.CaptureSequence);

            trial.Stand(20);
            Equal(0, trial.Evaluator.CaptureSequence);
        }

        private static void RepeatedSampleCannotCompleteHold()
        {
            var trial = new Trial();
            trial.Prepare();
            trial.Stand(2);
            Equal(SitToStandStage.Holding, trial.Evaluator.Stage);
            float hold = trial.Evaluator.HoldSeconds;
            LowerBodyMeasurement repeated = trial.Last;
            for (int i = 0; i < 100; i++)
                trial.Evaluator.Update(repeated, repeated.SampleTimeSeconds);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(hold, trial.Evaluator.HoldSeconds);
        }

        private static void RecalibrationCancelsCycle()
        {
            var trial = new Trial();
            trial.Prepare();
            trial.Feed(55f, 55f, 55f, 55f, 2);
            trial.Version++;
            trial.Stand(20);
            Equal(SitToStandStage.Preparing, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Check(!trial.Evaluator.HasSeatedReference, "重标定后仍保留旧坐姿参考");
        }

        private static void HighPhotoResources()
        {
            string[] paths =
            {
                "RehabPhotoGame/Photos/B_High/pavilion_01",
                "RehabPhotoGame/Photos/B_High/birds_01",
                "RehabPhotoGame/Photos/B_High/lanterns_01"
            };
            for (int i = 0; i < paths.Length; i++)
            {
                Texture2D photo = Resources.Load<Texture2D>(paths[i]);
                Check(photo != null, "未找到第3段高处素材：" + paths[i]);
                Equal(2048, photo.width);
                Equal(1152, photo.height);
            }
        }

        private static void FormalModeSmoke()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/City park/Scenes/RehabPhotoGame.unity");
            var host = new GameObject("Stage3 Formal UI Smoke Host");
            GameObject canvasRoot = null;
            try
            {
                SeatedKneeExtensionRuntimeFix seatedRuntime =
                    host.AddComponent<SeatedKneeExtensionRuntimeFix>();
                SitToStandRuntime runtime = host.AddComponent<SitToStandRuntime>();
                InvokePrivate(runtime, "Start");
                S1TrainingCanvasController controller =
                    host.AddComponent<S1TrainingCanvasController>();
                InvokePrivate(controller, "Start");

                canvasRoot = GameObject.Find("S1 Formal Training Canvas");
                Check(canvasRoot != null, "没有创建正式摄影训练 Canvas");
                Check(canvasRoot.GetComponentsInChildren<UnityEngine.UI.Button>(true).Length >= 11,
                    "正式界面缺少动作模式切换按钮");
                string allText = "";
                TMPro.TMP_Text[] labels =
                    canvasRoot.GetComponentsInChildren<TMPro.TMP_Text>(true);
                for (int i = 0; i < labels.Length; i++)
                    allText += labels[i].text + "\n";
                Check(allText.Contains("坐站摄影") && allText.Contains("坐姿伸膝"),
                    "正式界面没有提供两个动作模式");
                GameObject viewport = Field<GameObject>(controller, "photoViewport");
                RawImage photo = Field<RawImage>(controller, "photoImage");
                Check(viewport != null && viewport.GetComponent<RectMask2D>() != null,
                    "高处取景缺少固定裁切视口");
                Check(photo != null && photo.transform.parent == viewport.transform,
                    "图片内容没有放入固定取景视口");

                MethodInfo selectMode = typeof(S1TrainingCanvasController).GetMethod(
                    "SelectTrainingMode", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(selectMode != null, "找不到训练模式切换入口");
                selectMode.Invoke(controller, new object[] { PhotoTrainingMode.SitToStand });
                Equal(PhotoTrainingMode.SitToStand,
                    Field<PhotoTrainingMode>(controller, "trainingMode"));
                Check(runtime.CurrentSnapshot.IsModeActive,
                    "切到坐站摄影后坐站运行时没有启用");
                Check(!seatedRuntime.IsTrainingModeActive,
                    "切到坐站摄影后坐姿伸膝仍在写人物姿态");
                Texture2D selected = Field<Texture2D>(controller, "sourcePhoto");
                Check(selected != null && selected.name != "spring_01",
                    "坐站模式没有切换到 B_高处素材");

                selectMode.Invoke(controller,
                    new object[] { PhotoTrainingMode.SeatedKneeExtension });
                Check(seatedRuntime.IsTrainingModeActive,
                    "切回坐姿伸膝后 S1 运行时没有恢复");
                Check(!runtime.CurrentSnapshot.IsModeActive,
                    "切回坐姿伸膝后坐站运行时仍在写人物姿态");
            }
            finally
            {
                if (canvasRoot != null) UnityEngine.Object.DestroyImmediate(canvasRoot);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        private sealed class Trial
        {
            public readonly SitToStandSettings Settings = new SitToStandSettings();
            public readonly SitToStandEvaluator Evaluator;
            public float Time;
            public int Version = 1;
            public LowerBodyMeasurement Last;

            public Trial()
            {
                Evaluator = new SitToStandEvaluator(Settings);
                Evaluator.Reset(true);
            }

            public void Feed(
                float leftThigh, float leftKnee,
                float rightThigh, float rightKnee, int frames = 1)
            {
                for (int i = 0; i < frames; i++)
                {
                    Time += 0.25f;
                    Last = Sample(Time, Version, leftThigh, leftKnee,
                        rightThigh, rightKnee, true, 15, "");
                    Evaluator.Update(Last, Time);
                }
            }

            public void Prepare() => Sit(4);
            public void Sit(int frames) => Feed(90f, 90f, 90f, 90f, frames);
            public void Stand(int frames) => Feed(20f, 15f, 20f, 15f, frames);

            public void Dropout(int freshMask, string reason, int frames = 1)
            {
                for (int i = 0; i < frames; i++)
                {
                    Time += 0.25f;
                    Last = Sample(Time, Version, 20f, 15f, 20f, 15f,
                        false, freshMask, reason);
                    Evaluator.Update(Last, Time);
                }
            }
        }

        private static LowerBodyMeasurement Sample(
            float time, int version,
            float leftThigh, float leftKnee,
            float rightThigh, float rightKnee,
            bool valid, int freshMask, string reason)
        {
            return new LowerBodyMeasurement
            {
                IsValid = valid,
                FailureReason = reason,
                FreshMask = freshMask,
                CalibrationVersion = version,
                SampleTimeSeconds = time,
                LeftThighDeg = leftThigh,
                LeftKneeDeg = leftKnee,
                RightThighDeg = rightThigh,
                RightKneeDeg = rightKnee
            };
        }

        private static void InvokePrivate(object instance, string method)
        {
            MethodInfo value = instance.GetType().GetMethod(
                method, BindingFlags.Instance | BindingFlags.NonPublic);
            Check(value != null, "找不到私有入口：" + method);
            value.Invoke(instance, null);
        }

        private static T Field<T>(object instance, string name)
        {
            FieldInfo value = instance.GetType().GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);
            Check(value != null, "找不到字段：" + name);
            return (T)value.GetValue(instance);
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("[Stage3 tests] PASS " + name);
            }
            catch (Exception exception)
            {
                throw new Exception("[Stage3 tests] FAIL " + name, exception);
            }
        }

        private static void Equal<T>(T expected, T actual) =>
            Check(Equals(expected, actual), $"expected={expected}; actual={actual}");

        private static void Near(float expected, float actual) =>
            Check(Math.Abs(expected - actual) < 0.02f,
                $"expected={expected}; actual={actual}");

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
