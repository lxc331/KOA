using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RehabPhotoGame.Editor
{
    /// <summary>第4段浅蹲摄影的动作隔离、资源和正式界面回归测试。</summary>
    public static class StageFourTests
    {
        [MenuItem("Tools/Rehab Photo Game/Run Stage 4 Regression Tests")]
        public static void RunAll()
        {
            StageThreeTests.RunAll();
            RunLogicTests();
            Run("C_低处素材可由 Resources 加载", LowPhotoResources);
            Run("正式界面浅蹲模式互斥且画面向低处滚动", FormalModeSmoke);
            Run("持久联调配置加载并复制到运行时", GameTuningResource);
            Debug.Log("[Stage4 tests] PASS: 20 Stage 4 scenarios after 44 baseline scenarios.");
        }

        public static void RunLogicTests()
        {
            Run("浅蹲配置默认值有效", SettingsValidation);
            Run("未经站姿准备不能直接拍摄", MustPrepareStanding);
            Run("双腿同步浅蹲只拍一次且必须站回", CoordinatedSquatCapturesOnceAndRequiresReturn);
            Run("单腿屈曲不能降低取景或触发快门", SingleLegCannotLowerOrCapture);
            Run("过深不拍照且保留本轮站姿参考", DeepSittingIsRejected);
            Run("坐姿到站姿不会误判成浅蹲", SitToStandDoesNotTriggerSquat);
            Run("站姿浅蹲不会误判成坐站", SquatDoesNotTriggerSitToStand);
            Run("短时断流冻结浅蹲进度和保持计时", DropoutFreezesProgressAndHold);
            Run("长时间断流取消当前浅蹲动作", LongDropoutResetsCycle);
            Run("重复旧样本不能累计浅蹲保持时间", RepeatedSampleCannotCompleteHold);
            Run("客户第一次角度组合持续保持可拍摄（合成时序）", FirstCustomerAnglesCanHold);
            Run("客户第二次角度组合持续保持可拍摄（合成时序）", SecondCustomerAnglesCanHold);
            Run("大腿辅助变化不再阻断双膝稳定保持", ThighVariationIsAdvisory);
            Run("双膝必须各自达标且按各自站姿计量", BothKneesMustReachRelativeTarget);
            Run("过深回浅需重计保持且边缘不反复切换", DeepRecoveryRestartsFullHold);
            Run("过深后重标定或主动重置清除参考", ResetDuringDeepRequiresStanding);
            Run("过深中断流宽限结束仍需重新站稳", LongDropoutDuringDeepRequiresStanding);
        }

        private static void SettingsValidation()
        {
            var settings = new ShallowSquatSettings();
            Check(settings.IsValid, "第4段默认配置无效");
            settings.maximumShallowKneeDeg = settings.standingKneeMaxDeg;
            Check(!settings.IsValid, "浅蹲上限低于站姿阈值仍被接受");
            settings = new ShallowSquatSettings { minimumKneeFlexionDeg = 90f };
            Check(!settings.IsValid, "无法在过深恢复范围达到的目标仍被接受");
            settings = new ShallowSquatSettings { singleLegMinimumRatio = float.NaN };
            Check(!settings.IsValid, "无效单腿比例仍被接受");
        }

        private static void MustPrepareStanding()
        {
            var trial = new SquatTrial();
            trial.Sit(20);
            Equal(ShallowSquatStage.Preparing, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Check(!trial.Evaluator.HasStandingReference,
                "从坐姿开始时错误记录了站姿参考");
        }

        private static void CoordinatedSquatCapturesOnceAndRequiresReturn()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            Equal(ShallowSquatStage.Lowering, trial.Evaluator.Stage);
            trial.Squat(10);
            Equal(ShallowSquatStage.Returning, trial.Evaluator.Stage);
            Equal(1, trial.Evaluator.CaptureSequence);
            Near(1f, trial.Evaluator.DropProgress01);

            trial.Squat(20);
            Equal(1, trial.Evaluator.CaptureSequence);
            Equal(0, trial.Evaluator.CompletedRepetitions);

            trial.Stand(4);
            Equal(1, trial.Evaluator.CompletedRepetitions);
            Equal(ShallowSquatStage.Preparing, trial.Evaluator.Stage);
        }

        private static void SingleLegCannotLowerOrCapture()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            trial.Feed(40f, 45f, 10f, 5f, 20);
            Equal(ShallowSquatStage.Lowering, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(0f, trial.Evaluator.DropProgress01);
            Near(1f, trial.Evaluator.LeftProgress01);
            Near(0f, trial.Evaluator.RightProgress01);
            Check(trial.Evaluator.BlockReason.Contains("同步"),
                "单腿动作没有给出双腿同步提示");
        }

        private static void DeepSittingIsRejected()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            trial.Sit(1);
            Equal(ShallowSquatStage.TooDeep, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Check(trial.Evaluator.HasStandingReference,
                "过深后意外清除了本轮站姿参考");
            Check(trial.Evaluator.BlockReason.Contains("回浅"),
                "过深后没有提示回浅");
            trial.Sit(20);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(0f, trial.Evaluator.HoldSeconds);
        }

        private static void FirstCustomerAnglesCanHold()
        {
            var trial = new SquatTrial();
            trial.Feed(-1.91055f, 7f, 0f, 9.0295f, 4);
            // 来自 205956 日志的最高进度角度；仅合成稳定的新样本时序，非真人回放。
            trial.Feed(22.7203f, 65.8482f, 35.8533f, 29.1880f, 3);
            Equal(ShallowSquatStage.Holding, trial.Evaluator.Stage);
            Check(trial.Evaluator.CoordinationWarning, "普通幅度差异没有辅助提示");
            trial.Feed(22.7203f, 65.8482f, 35.8533f, 29.1880f, 7);
            Equal(1, trial.Evaluator.CaptureSequence);
        }

        private static void SecondCustomerAnglesCanHold()
        {
            var trial = new SquatTrial();
            trial.Feed(-0.16915f, 7f, 1.9f, 9f, 4);
            // 210312 日志中曾被左大腿卡在 38.35% 的角度组合。
            trial.Feed(9.4193f, 43.7187f, 36.4834f, 73.9428f, 3);
            Equal(ShallowSquatStage.Holding, trial.Evaluator.Stage);
            Near(1f, trial.Evaluator.DropProgress01);
            trial.Feed(9.4193f, 43.7187f, 36.4834f, 73.9428f, 7);
            Equal(1, trial.Evaluator.CaptureSequence);

            // 同份日志的 80.3/84.0 双膝也能进入联调目标，不再被旧 75°上限取消。
            trial = new SquatTrial();
            trial.Prepare();
            trial.Feed(40f, 80.3f, 39.8f, 84f, 10);
            Equal(1, trial.Evaluator.CaptureSequence);
        }

        private static void ThighVariationIsAdvisory()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            for (int i = 0; i < 10; i++)
                trial.Feed(i % 2 == 0 ? 0f : 30f, 35f, 1f, 35f);
            Equal(1, trial.Evaluator.CaptureSequence);
        }

        private static void BothKneesMustReachRelativeTarget()
        {
            var trial = new SquatTrial();
            trial.Feed(10f, 5f, 10f, 20f, 4);
            trial.Feed(30f, 35f, 30f, 39f, 12);
            Equal(0, trial.Evaluator.CaptureSequence);
            Check(trial.Evaluator.BlockReason.Contains("右膝"), "缺少未达标侧提示");
            trial.Feed(30f, 35f, 30f, 42f, 10);
            Equal(1, trial.Evaluator.CaptureSequence);

            trial = new SquatTrial();
            trial.Prepare();
            trial.Feed(30f, 65f, 10f, 12f, 12);
            Equal(0, trial.Evaluator.CaptureSequence);
            Check(trial.Evaluator.BlockReason.Contains("单腿"), "明显单腿动作未阻止");

            trial = new SquatTrial(new ShallowSquatSettings { minimumKneeFlexionDeg = 10f });
            trial.Prepare();
            trial.Feed(30f, 85f, 10f, 16f, 12);
            // 两侧虽然都超过低目标，但 80:11 的相对屈曲比例仍是明显单侧主导。
            Equal(0, trial.Evaluator.CaptureSequence);
        }

        private static void DeepRecoveryRestartsFullHold()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            trial.Squat(5);
            Near(1f, trial.Evaluator.HoldSeconds);
            trial.Feed(40f, 105f, 40f, 105f, 10);
            Equal(ShallowSquatStage.TooDeep, trial.Evaluator.Stage);
            Near(0f, trial.Evaluator.HoldSeconds);
            Check(trial.Evaluator.HasStandingReference, "过深清除了参考");
            trial.Feed(40f, 98f, 40f, 98f, 5);
            Equal(ShallowSquatStage.TooDeep, trial.Evaluator.Stage);
            trial.Squat(1);
            Equal(ShallowSquatStage.Holding, trial.Evaluator.Stage);
            Near(0f, trial.Evaluator.HoldSeconds);
            trial.Squat(7);
            Equal(0, trial.Evaluator.CaptureSequence);
            trial.Squat(1);
            Equal(1, trial.Evaluator.CaptureSequence);
            trial.Squat(12);
            Equal(1, trial.Evaluator.CaptureSequence);
            trial.Stand(4);
            Equal(1, trial.Evaluator.CompletedRepetitions);
        }

        private static void ResetDuringDeepRequiresStanding()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            trial.Sit(1);
            trial.Version++;
            trial.Squat(12);
            Equal(ShallowSquatStage.Preparing, trial.Evaluator.Stage);
            Check(!trial.Evaluator.HasStandingReference, "重标定未清除参考");
            trial.Stand(4);
            trial.Sit(1);
            trial.Evaluator.Reset(false);
            trial.Squat(12);
            Equal(ShallowSquatStage.Preparing, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
        }

        private static void LongDropoutDuringDeepRequiresStanding()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            trial.Sit(1);
            trial.Dropout(7, "传感器数据超时", 2);
            Equal(ShallowSquatStage.TooDeep, trial.Evaluator.Stage);
            Check(trial.Evaluator.HasStandingReference, "短时断流意外清除参考");
            trial.Dropout(7, "传感器数据超时", 20);
            trial.Squat(12);
            Equal(ShallowSquatStage.Preparing, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Check(!trial.Evaluator.HasStandingReference, "长断流没有清除参考");
        }

        private static void SitToStandDoesNotTriggerSquat()
        {
            var squat = new SquatTrial();
            squat.Sit(4);
            squat.Stand(12);
            Equal(0, squat.Evaluator.CaptureSequence);
            Check(squat.Evaluator.Stage != ShallowSquatStage.Holding &&
                  squat.Evaluator.Stage != ShallowSquatStage.Returning,
                "坐站返回过程错误进入浅蹲拍摄阶段");
        }

        private static void SquatDoesNotTriggerSitToStand()
        {
            var sit = new SitTrial();
            sit.Stand(4);
            sit.Squat(20);
            Equal(SitToStandStage.Preparing, sit.Evaluator.Stage);
            Equal(0, sit.Evaluator.CaptureSequence);
            Check(!sit.Evaluator.HasSeatedReference,
                "浅蹲动作错误建立了坐姿参考");
        }

        private static void DropoutFreezesProgressAndHold()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            trial.Squat(3);
            Equal(ShallowSquatStage.Holding, trial.Evaluator.Stage);
            float progress = trial.Evaluator.DropProgress01;
            float hold = trial.Evaluator.HoldSeconds;

            trial.Dropout(7, "传感器数据超时");
            trial.Dropout(7, "传感器数据超时");
            Equal(ShallowSquatStage.Holding, trial.Evaluator.Stage);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(progress, trial.Evaluator.DropProgress01);
            Near(hold, trial.Evaluator.HoldSeconds);
            Check(trial.Evaluator.SignalPaused, "短时断流没有进入冻结状态");

            trial.Squat(10);
            Equal(1, trial.Evaluator.CaptureSequence);
        }

        private static void LongDropoutResetsCycle()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            trial.Feed(28f, 30f, 28f, 30f, 2);
            Check(trial.Evaluator.HasStandingReference, "准备后没有站姿参考");
            trial.Dropout(7, "传感器数据超时", 22);
            Equal(ShallowSquatStage.Preparing, trial.Evaluator.Stage);
            Check(!trial.Evaluator.HasStandingReference,
                "长断流后仍保留旧站姿参考");
            Equal(0, trial.Evaluator.CaptureSequence);
        }

        private static void RepeatedSampleCannotCompleteHold()
        {
            var trial = new SquatTrial();
            trial.Prepare();
            trial.Squat(2);
            Equal(ShallowSquatStage.Holding, trial.Evaluator.Stage);
            float hold = trial.Evaluator.HoldSeconds;
            LowerBodyMeasurement repeated = trial.Last;
            for (int i = 0; i < 100; i++)
                trial.Evaluator.Update(repeated, repeated.SampleTimeSeconds);
            Equal(0, trial.Evaluator.CaptureSequence);
            Near(hold, trial.Evaluator.HoldSeconds);
        }

        private static void LowPhotoResources()
        {
            string[] paths =
            {
                "RehabPhotoGame/Photos/C_Low/grass_mushrooms_01",
                "RehabPhotoGame/Photos/C_Low/butterflies_01",
                "RehabPhotoGame/Photos/C_Low/lotus_01"
            };
            for (int i = 0; i < paths.Length; i++)
            {
                Texture2D photo = Resources.Load<Texture2D>(paths[i]);
                Check(photo != null, "未找到第4段低处素材：" + paths[i]);
                Equal(2048, photo.width);
                Equal(1152, photo.height);
            }
        }

        private static void GameTuningResource()
        {
            ShallowSquatGameTuning asset = Resources.Load<ShallowSquatGameTuning>(
                ShallowSquatGameTuning.ResourcePath);
            Check(asset != null && asset.settings != null && asset.settings.IsValid,
                "缺少可保存的默认联调配置，或配置参数无效");
            var host = new GameObject("Stage4 Tuning Smoke Host");
            ShallowSquatGameTuning custom = ScriptableObject.CreateInstance<ShallowSquatGameTuning>();
            try
            {
                ShallowSquatRuntime runtime = host.AddComponent<ShallowSquatRuntime>();
                InvokePrivate(runtime, "Start");
                ShallowSquatSettings effective = Field<ShallowSquatSettings>(runtime, "settings");
                Near(asset.settings.minimumKneeFlexionDeg, effective.minimumKneeFlexionDeg);
                Check(!ReferenceEquals(asset.settings, effective), "运行时直接引用了共享配置对象");

                // 非默认配置应改变判定结果；之后修改源配置不应改变已启动的会话。
                custom.settings.minimumKneeFlexionDeg = 40f;
                var copy = custom.CreateRuntimeSettings();
                custom.settings.minimumKneeFlexionDeg = 20f;
                var trial = new SquatTrial(copy);
                trial.Prepare();
                trial.Feed(30f, 35f, 30f, 35f, 12);
                Equal(0, trial.Evaluator.CaptureSequence);
                trial.Feed(30f, 50f, 30f, 50f, 10);
                Equal(1, trial.Evaluator.CaptureSequence);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(custom);
            }
        }

        private static void FormalModeSmoke()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/City park/Scenes/RehabPhotoGame.unity");
            var host = new GameObject("Stage4 Formal UI Smoke Host");
            GameObject canvasRoot = null;
            try
            {
                SeatedKneeExtensionRuntimeFix seatedRuntime =
                    host.AddComponent<SeatedKneeExtensionRuntimeFix>();
                SitToStandRuntime sitRuntime = host.AddComponent<SitToStandRuntime>();
                ShallowSquatRuntime squatRuntime = host.AddComponent<ShallowSquatRuntime>();
                InvokePrivate(sitRuntime, "Start");
                InvokePrivate(squatRuntime, "Start");
                S1TrainingCanvasController controller =
                    host.AddComponent<S1TrainingCanvasController>();
                InvokePrivate(controller, "Start");

                canvasRoot = GameObject.Find("S1 Formal Training Canvas");
                Check(canvasRoot != null, "没有创建正式摄影训练 Canvas");
                string allText = "";
                TMPro.TMP_Text[] labels =
                    canvasRoot.GetComponentsInChildren<TMPro.TMP_Text>(true);
                for (int i = 0; i < labels.Length; i++)
                    allText += labels[i].text + "\n";
                Check(allText.Contains("坐姿伸膝") && allText.Contains("坐站摄影") &&
                      allText.Contains("浅蹲摄影"), "正式界面没有提供三种动作模式");

                MethodInfo selectMode = typeof(S1TrainingCanvasController).GetMethod(
                    "SelectTrainingMode", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(selectMode != null, "找不到训练模式切换入口");
                selectMode.Invoke(controller, new object[] { PhotoTrainingMode.ShallowSquat });
                Equal(PhotoTrainingMode.ShallowSquat,
                    Field<PhotoTrainingMode>(controller, "trainingMode"));
                Check(squatRuntime.CurrentSnapshot.IsModeActive,
                    "切到浅蹲摄影后浅蹲运行时没有启用");
                Check(!sitRuntime.CurrentSnapshot.IsModeActive &&
                      !seatedRuntime.IsTrainingModeActive,
                    "浅蹲模式启用后其他动作运行时仍在驱动人物");

                Texture2D selected = Field<Texture2D>(controller, "sourcePhoto");
                Check(selected != null &&
                      (selected.name == "grass_mushrooms_01" ||
                       selected.name == "butterflies_01" || selected.name == "lotus_01"),
                    "浅蹲模式没有切换到 C_低处素材");

                GameObject viewport = Field<GameObject>(controller, "photoViewport");
                RawImage photo = Field<RawImage>(controller, "photoImage");
                Check(viewport != null && viewport.GetComponent<RectMask2D>() != null,
                    "低处取景缺少固定裁切视口");
                Check(photo != null && photo.transform.parent == viewport.transform,
                    "低处图片内容没有放入固定取景视口");

                ShallowSquatTrainingSnapshot uiSnapshot =
                    Field<ShallowSquatTrainingSnapshot>(controller, "shallowSquatSnapshot");
                uiSnapshot.DropProgress01 = 1f;
                SetField(controller, "shallowSquatSnapshot", uiSnapshot);
                Canvas.ForceUpdateCanvases();
                Vector2 before = Field<Vector2>(controller, "photoBaseAnchoredPosition");
                InvokePrivate(controller, "RefreshPhotoPanel");
                Check(photo.rectTransform.anchoredPosition.y >= before.y,
                    "浅蹲进度没有让固定取景框内的照片向上滚动");

                uiSnapshot.Stage = ShallowSquatStage.TooDeep;
                uiSnapshot.IsDataValid = true;
                uiSnapshot.BlockReason = "已超出浅蹲范围，请回浅一点；回到范围后重新保持";
                SetField(controller, "shallowSquatSnapshot", uiSnapshot);
                MethodInfo instruction = typeof(S1TrainingCanvasController).GetMethod(
                    "ShallowSquatInstruction", BindingFlags.Instance | BindingFlags.NonPublic);
                Equal(uiSnapshot.BlockReason, (string)instruction.Invoke(controller, null));
                var session = Field<S1TrainingSession>(controller, "session");
                session.Start();
                MethodInfo caption = typeof(S1TrainingCanvasController).GetMethod(
                    "StageCaption", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(((string)caption.Invoke(controller, null)).Contains("回浅"),
                    "过深状态仍被显示成准备或拍摄返回");
                session.End();
                session.ReturnToSetup();
                selectMode.Invoke(controller, new object[] { PhotoTrainingMode.SitToStand });
                Check(!squatRuntime.CurrentSnapshot.IsModeActive && sitRuntime.CurrentSnapshot.IsModeActive,
                    "浅蹲切回坐站后运行时未互斥");
                selectMode.Invoke(controller, new object[] { PhotoTrainingMode.SeatedKneeExtension });
                Check(!squatRuntime.CurrentSnapshot.IsModeActive && seatedRuntime.IsTrainingModeActive,
                    "浅蹲切回伸膝后运行时未互斥");
            }
            finally
            {
                if (canvasRoot != null) UnityEngine.Object.DestroyImmediate(canvasRoot);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        private sealed class SquatTrial
        {
            public readonly ShallowSquatEvaluator Evaluator;
            public float Time;
            public int Version = 1;
            public LowerBodyMeasurement Last;

            public SquatTrial(ShallowSquatSettings settings = null)
            {
                Evaluator = new ShallowSquatEvaluator(settings ?? new ShallowSquatSettings());
                Evaluator.Reset(true);
            }

            public void Feed(float leftThigh, float leftKnee,
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

            public void Prepare() => Stand(4);
            public void Stand(int frames) => Feed(10f, 5f, 10f, 5f, frames);
            public void Squat(int frames) => Feed(40f, 45f, 40f, 45f, frames);
            public void Sit(int frames) => Feed(90f, 90f, 90f, 90f, frames);

            public void Dropout(int freshMask, string reason, int frames = 1)
            {
                for (int i = 0; i < frames; i++)
                {
                    Time += 0.25f;
                    Last = Sample(Time, Version, 40f, 45f, 40f, 45f,
                        false, freshMask, reason);
                    Evaluator.Update(Last, Time);
                }
            }
        }

        private sealed class SitTrial
        {
            public readonly SitToStandEvaluator Evaluator =
                new SitToStandEvaluator(new SitToStandSettings());
            public float Time;

            public SitTrial() => Evaluator.Reset(true);

            public void Feed(float leftThigh, float leftKnee,
                float rightThigh, float rightKnee, int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    Time += 0.25f;
                    Evaluator.Update(Sample(Time, 1, leftThigh, leftKnee,
                        rightThigh, rightKnee, true, 15, ""), Time);
                }
            }

            public void Stand(int frames) => Feed(10f, 5f, 10f, 5f, frames);
            public void Squat(int frames) => Feed(40f, 45f, 40f, 45f, frames);
        }

        private static LowerBodyMeasurement Sample(float time, int version,
            float leftThigh, float leftKnee, float rightThigh, float rightKnee,
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

        private static void SetField(object instance, string name, object value)
        {
            FieldInfo field = instance.GetType().GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);
            Check(field != null, "找不到字段：" + name);
            field.SetValue(instance, value);
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("[Stage4 tests] PASS " + name);
            }
            catch (Exception exception)
            {
                throw new Exception("[Stage4 tests] FAIL " + name, exception);
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
