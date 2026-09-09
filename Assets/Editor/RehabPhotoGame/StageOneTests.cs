using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace RehabPhotoGame.Editor
{
    /// <summary>不依赖额外测试包，可在 Unity 编辑器菜单或 batchmode 中运行。</summary>
    public static class StageOneTests
    {
        [MenuItem("Tools/Rehab Photo Game/Run Stage 1 Regression Tests")]
        public static void RunAll()
        {
            RunLogicTests();
            Run("实际采样接口按当前时钟检测断流", ProcessorFreshness);
            Run("四元数角度提取、左右符号和人物判定一致", QuaternionMeasurements);
            Run("下肢突跳需连续帧确认后恢复", LowerBodyJumpGuard);
            Run("远景素材与对焦 Shader 可由 Resources 加载", PhotoResources);
            Run("S1 正式 Canvas 可创建且不显示原始遥测", FormalCanvasSmoke);
            Debug.Log("[Stage1 tests] PASS: 29 regression scenarios.");
        }

        public static void RunLogicTests()
        {
            Run("完整返回后计数，长时间停留不重复计数", CompleteCycle);
            Run("未经过准备不能直接计数", MustPrepareFirst);
            Run("中途离开目标后保持计时归零", InterruptedHold);
            Run("目标边缘迟滞与稳定窗口", TargetHysteresis);
            Run("目标内大幅晃动不能积累保持", UnstableWithinTarget);
            Run("重复同一个传感器样本不能累计时间", RepeatedSample);
            Run("设备延迟缩短不能补算进入目标之前的时间", DelayedWatermark);
            Run("短时断流暂停阶段和保持进度", AllSensorsStale);
            Run("短时断流恢复后继续完成动作", DropoutRecovery);
            Run("长时间断流才取消本次动作", LongDropoutRequiresReady);
            Run("非训练腿失联不打断当前动作", OppositeLegDropoutIgnored);
            Run("返回途中短时失联恢复后只计一次", DropoutDuringReturn);
            Run("重标定和切换腿中止旧动作", RecalibrationAndLegSwitch);
            Run("双腿模式不能由一侧动作完成", Bilateral);
            Run("右腿模式独立选择正确侧", RightLegSelection);
            Run("站姿不能触发坐位膝伸", StandingRejected);
            Run("NaN角度和无效配置停止判定", InvalidData);
            Run("摄影清晰度随相对伸膝进度变化", PhotoFocusMapping);
            Run("保持完成只触发一次快门", PhotoCaptureOnlyOnce);
            Run("短时断流冻结摄影清晰度", PhotoFocusFreezesDuringDropout);
            Run("切腿或重置不会串接上一轮快门", PhotoSessionReset);
            Run("正式训练会话按目标次数完成", FormalSessionTarget);
            Run("暂停和结束时不能继续累计拍摄", FormalSessionPauseAndEnd);
            Run("运行时快门音频波形有效", ShutterWaveform);
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("[Stage1 tests] PASS " + name); }
            catch (Exception exception) { throw new Exception("[Stage1 tests] FAIL " + name, exception); }
        }

        private static KneeExtensionTrainingSnapshot PhotoSnapshot(
            KneeExtensionStage stage, float knee, bool valid = true, int version = 1)
        {
            return new KneeExtensionTrainingSnapshot
            {
                Leg = TrainingLeg.Left,
                Stage = stage,
                SessionVersion = version,
                IsDataValid = valid,
                HasReadyReference = true,
                KneeAngleDeg = knee,
                ReadyReferenceKneeDeg = 90f,
                TargetKneeDeg = 60f
            };
        }

        private static void PhotoFocusMapping()
        {
            Near(0f, PhotoFocusSession.CalculateFocus(95f, 90f, 60f));
            Near(0.5f, PhotoFocusSession.CalculateFocus(75f, 90f, 60f));
            Near(1f, PhotoFocusSession.CalculateFocus(55f, 90f, 60f));
            Near(0f, PhotoFocusSession.CalculateFocus(float.NaN, 90f, 60f));
        }

        private static void PhotoCaptureOnlyOnce()
        {
            var photo = new PhotoFocusSession();
            Check(!photo.Update(PhotoSnapshot(KneeExtensionStage.Holding, 55f)),
                "首次进入已有状态不应补触发快门");
            Check(photo.Update(PhotoSnapshot(KneeExtensionStage.Returning, 55f)),
                "保持完成未触发快门");
            Check(!photo.Update(PhotoSnapshot(KneeExtensionStage.Returning, 55f)),
                "停留在返回阶段重复触发快门");
            Equal(1, photo.CapturedPhotos);
        }

        private static void PhotoFocusFreezesDuringDropout()
        {
            var photo = new PhotoFocusSession();
            photo.Update(PhotoSnapshot(KneeExtensionStage.Extending, 75f));
            Near(0.5f, photo.Focus01);
            Check(!photo.Update(PhotoSnapshot(KneeExtensionStage.Holding, 0f, false)),
                "无效数据不应触发快门");
            Near(0.5f, photo.Focus01);
        }

        private static void PhotoSessionReset()
        {
            var photo = new PhotoFocusSession();
            photo.Update(PhotoSnapshot(KneeExtensionStage.Holding, 55f));
            photo.Update(PhotoSnapshot(KneeExtensionStage.Returning, 55f));
            Equal(1, photo.CapturedPhotos);
            Check(!photo.Update(PhotoSnapshot(KneeExtensionStage.Returning, 55f, true, 2)),
                "新会话不能承接旧会话的保持阶段");
            Equal(0, photo.CapturedPhotos);
        }

        private static void FormalSessionTarget()
        {
            var formal = new S1TrainingSession();
            formal.SetTarget(3);
            formal.Start();
            Equal(S1TrainingRunState.Running, formal.State);
            Check(!formal.RecordCapture(), "第1次拍摄不应提前完成训练");
            Check(!formal.RecordCapture(), "第2次拍摄不应提前完成训练");
            Check(formal.RecordCapture(), "达到目标次数后未完成训练");
            Equal(S1TrainingRunState.Completed, formal.State);
            Equal(3, formal.CompletedRepetitions);
            Check(!formal.RecordCapture(), "完成后仍然累计了拍摄次数");
            Equal(3, formal.CompletedRepetitions);
        }

        private static void FormalSessionPauseAndEnd()
        {
            var formal = new S1TrainingSession();
            formal.SetTarget(0);
            Equal(1, formal.TargetRepetitions);
            formal.SetTarget(25);
            Equal(20, formal.TargetRepetitions);
            formal.SetTarget(2);
            formal.Start();
            formal.RecordCapture();
            Check(formal.Pause(), "训练中无法暂停");
            Check(!formal.RecordCapture(), "暂停时仍然累计拍摄次数");
            Equal(1, formal.CompletedRepetitions);
            Check(formal.Resume(), "暂停后无法继续");
            Check(formal.End(), "训练中无法结束");
            Check(!formal.RecordCapture(), "结束后仍然累计拍摄次数");
            formal.ReturnToSetup();
            Equal(S1TrainingRunState.Idle, formal.State);
            Equal(0, formal.CompletedRepetitions);
        }

        private static void ShutterWaveform()
        {
            float[] samples = ShutterSound.BuildSamples(ShutterSound.SampleRate);
            Equal((int)Math.Round(
                    ShutterSound.SampleRate * ShutterSound.DurationSeconds),
                samples.Length);
            double energy = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                Check(!float.IsNaN(samples[i]) && !float.IsInfinity(samples[i]),
                    "快门音频包含无效采样");
                Check(Math.Abs(samples[i]) <= 1f, "快门音频采样超出有效范围");
                energy += samples[i] * samples[i];
            }
            Check(energy > 1.0, "快门音频几乎没有声音");
        }

        private static void PhotoResources()
        {
            Texture2D photo = Resources.Load<Texture2D>(
                "RehabPhotoGame/Photos/A_Distant/spring_01");
            Check(photo != null, "未找到第1段远景素材");
            Equal(2048, photo.width);
            Equal(1152, photo.height);

            Shader shader = Resources.Load<Shader>(
                "RehabPhotoGame/Shaders/FocusBlur");
            Check(shader != null, "未找到对焦模糊 Shader");
            var material = new Material(shader);
            try { Check(material.HasProperty("_BlurPixels"), "Shader 缺少模糊半径参数"); }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        private static void FormalCanvasSmoke()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/City park/Scenes/RehabPhotoGame.unity");
            var host = new GameObject("S1 Formal UI Smoke Host");
            GameObject canvasRoot = null;
            try
            {
                var controller = host.AddComponent<S1TrainingCanvasController>();
                MethodInfo start = typeof(S1TrainingCanvasController).GetMethod(
                    "Start", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(start != null, "找不到正式界面启动入口");
                start.Invoke(controller, null);

                canvasRoot = GameObject.Find("S1 Formal Training Canvas");
                Check(canvasRoot != null, "没有创建正式训练 Canvas");
                Check(canvasRoot.GetComponent<Canvas>() != null, "正式界面缺少 Canvas");
                Check(canvasRoot.GetComponentsInChildren<UnityEngine.UI.Button>(true).Length >= 9,
                    "正式界面操作按钮不完整");
                Check(canvasRoot.GetComponentsInChildren<UnityEngine.UI.RawImage>(true).Length == 1,
                    "正式界面没有唯一摄影取景框");

                TMPro.TMP_Text[] labels =
                    canvasRoot.GetComponentsInChildren<TMPro.TMP_Text>(true);
                Font bundledFont = Resources.Load<Font>(
                    "RehabPhotoGame/Fonts/NotoSansSC-Regular");
                Check(bundledFont != null, "未加载项目内置中文字体");
                Check(labels.Length > 0 && labels[0].font != null &&
                      labels[0].font != TMPro.TMP_Settings.defaultFontAsset &&
                      labels[0].font.HasCharacter('中'),
                    "正式界面没有使用包含中文字形的项目字体");
                string allText = "";
                for (int i = 0; i < labels.Length; i++) allText += labels[i].text + "\n";
                Check(allText.Contains("公园摄影站") &&
                      allText.Contains("教练示范") &&
                      allText.Contains("开始训练"), "正式界面关键内容缺失");
                Check(!allText.Contains("q0") && !allText.Contains("Yaw") &&
                      !allText.Contains("Pitch") && !allText.Contains("Roll") &&
                      !allText.Contains("四元数"), "原始遥测进入了正式界面");
            }
            finally
            {
                if (canvasRoot != null) UnityEngine.Object.DestroyImmediate(canvasRoot);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        private sealed class Trial
        {
            public readonly SeatedKneeExtensionSettings Settings = new SeatedKneeExtensionSettings();
            public readonly SeatedKneeExtensionEvaluator Evaluator;
            public float Time;
            public int Version = 1;
            public LowerBodyMeasurement Last;

            public Trial(TrainingLeg leg = TrainingLeg.Left)
            {
                Evaluator = new SeatedKneeExtensionEvaluator(Settings, leg);
            }

            public void Feed(float left, float right = 90f, float thigh = 90f, int frames = 1)
            {
                for (int i = 0; i < frames; i++)
                {
                    Time += 0.1f;
                    Last = new LowerBodyMeasurement
                    {
                        IsValid = true, FreshMask = 15, SampleTimeSeconds = Time,
                        CalibrationVersion = Version, LeftKneeDeg = left, RightKneeDeg = right,
                        LeftThighDeg = thigh, RightThighDeg = thigh, FailureReason = ""
                    };
                    Evaluator.Update(Last, Time);
                }
            }

            public void Prepare() => Feed(90f, frames: 12);
            public void ReachReturn() { Prepare(); Feed(10f, frames: 36); }
        }

        private static void CompleteCycle()
        {
            var t = new Trial();
            t.ReachReturn();
            Equal(KneeExtensionStage.Returning, t.Evaluator.Stage);
            Equal(0, t.Evaluator.CompletedRepetitions);
            t.Feed(10f, frames: 80);
            Equal(0, t.Evaluator.CompletedRepetitions);
            t.Feed(90f, frames: 80);
            Equal(1, t.Evaluator.CompletedRepetitions);
            t.Feed(10f, frames: 36);
            t.Feed(90f, frames: 12);
            Equal(2, t.Evaluator.CompletedRepetitions);
        }

        private static void MustPrepareFirst()
        {
            var t = new Trial();
            t.Feed(10f, frames: 60);
            Equal(KneeExtensionStage.Preparing, t.Evaluator.Stage);
            t.Feed(90f, frames: 12);
            Equal(0, t.Evaluator.CompletedRepetitions);
        }

        private static void InterruptedHold()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f, frames: 20);
            Check(t.Evaluator.HoldSeconds > 1f, "保持未开始");
            t.Feed(80f);
            Equal(KneeExtensionStage.Extending, t.Evaluator.Stage);
            Near(0f, t.Evaluator.HoldSeconds);
            t.Feed(10f, frames: 20);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
        }

        private static void TargetHysteresis()
        {
            var t = new Trial(); t.Prepare(); t.Feed(64f);
            t.Feed(70f, frames: 35);
            Equal(KneeExtensionStage.Returning, t.Evaluator.Stage);
            t = new Trial(); t.Prepare(); t.Feed(64f); t.Feed(74f);
            Equal(KneeExtensionStage.Extending, t.Evaluator.Stage);
        }

        private static void UnstableWithinTarget()
        {
            var t = new Trial(); t.Prepare();
            for (int i = 0; i < 80; i++) t.Feed(i % 2 == 0 ? 50f : 64f);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
            Equal(0, t.Evaluator.CompletedRepetitions);
            Near(0f, t.Evaluator.HoldSeconds);
        }

        private static void RepeatedSample()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f);
            for (int i = 0; i < 1000; i++) t.Evaluator.Update(t.Last, t.Time + 0.5f);
            Check(t.Evaluator.HoldSeconds < 1f, "重复样本错误累计为完整保持时间");
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
        }

        private static void AllSensorsStale()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f, frames: 20);
            float holdBeforePause = t.Evaluator.HoldSeconds;
            var offline = t.Last;
            offline.IsValid = false;
            offline.FreshMask = 0;
            offline.FailureReason = "06、07 超时；当前动作已中止";
            t.Evaluator.Update(offline, t.Time);
            t.Time += 4f;
            t.Evaluator.Update(offline, t.Time);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
            Check(t.Evaluator.SignalPaused, "断流时未进入暂停状态");
            Near(holdBeforePause, t.Evaluator.HoldSeconds);
        }

        private static void DelayedWatermark()
        {
            var t = new Trial(); t.Prepare();
            t.Time += 1f;
            var delayed = t.Last;
            delayed.SampleTimeSeconds = t.Time - 0.9f;
            delayed.LeftKneeDeg = 10f;
            t.Evaluator.Update(delayed, t.Time);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
            // 延迟突然消失，但现实只保持了 2.5 秒，不能提前达标。
            t.Feed(10f, frames: 25);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
            Check(t.Evaluator.HoldSeconds < 3f, "回补了进入目标之前的时间");
        }

        private static void DropoutRecovery()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f, frames: 20);
            var offline = t.Last;
            offline.IsValid = false;
            offline.FreshMask = 1;
            offline.FailureReason = "07 超时；当前动作已中止";
            t.Evaluator.Update(offline, t.Time);
            t.Time += 3f;
            t.Evaluator.Update(offline, t.Time);
            t.Feed(10f, frames: 15);
            Equal(KneeExtensionStage.Returning, t.Evaluator.Stage);
            Equal(0, t.Evaluator.CompletedRepetitions);
            t.Feed(90f, frames: 12);
            Equal(1, t.Evaluator.CompletedRepetitions);
        }

        private static void LongDropoutRequiresReady()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f, frames: 20);
            var offline = t.Last;
            offline.IsValid = false;
            offline.FreshMask = 1;
            offline.FailureReason = "07 超时；当前动作已中止";
            t.Evaluator.Update(offline, t.Time);
            t.Time += t.Settings.signalGraceSeconds + 0.1f;
            t.Evaluator.Update(offline, t.Time);
            Equal(KneeExtensionStage.Preparing, t.Evaluator.Stage);
            Near(0f, t.Evaluator.HoldSeconds);
            t.Feed(10f, frames: 40);
            Equal(KneeExtensionStage.Preparing, t.Evaluator.Stage);
            t.Feed(90f, frames: 12);
            Equal(KneeExtensionStage.Extending, t.Evaluator.Stage);
        }

        private static void OppositeLegDropoutIgnored()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f, frames: 20);
            var oppositeOffline = t.Last;
            oppositeOffline.FreshMask = 7; // 09 离线，但左腿所需的 06/07 仍在线。
            t.Evaluator.Update(oppositeOffline, t.Time);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
            Check(t.Evaluator.BlockReason == "", "非训练腿断流不应阻止左腿判定");
            t.Feed(10f, frames: 20);
            Equal(KneeExtensionStage.Returning, t.Evaluator.Stage);
        }

        private static void DropoutDuringReturn()
        {
            var t = new Trial(); t.ReachReturn();
            var offline = t.Last;
            offline.IsValid = false;
            offline.FreshMask = 1;
            offline.FailureReason = "07 超时；当前动作已中止";
            t.Evaluator.Update(offline, t.Time);
            t.Time += 2f;
            t.Evaluator.Update(offline, t.Time);
            t.Feed(90f, frames: 20);
            Equal(1, t.Evaluator.CompletedRepetitions);
        }

        private static void RecalibrationAndLegSwitch()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f, frames: 20);
            t.Version++; t.Feed(10f, frames: 40);
            Equal(KneeExtensionStage.Preparing, t.Evaluator.Stage);
            t.Evaluator.Reset(TrainingLeg.Right, true);
            t.Feed(90f, 10f, frames: 40);
            Equal(KneeExtensionStage.Preparing, t.Evaluator.Stage);
        }

        private static void Bilateral()
        {
            var t = new Trial(TrainingLeg.Both); t.Prepare();
            t.Feed(10f, 90f, frames: 40);
            Equal(KneeExtensionStage.Extending, t.Evaluator.Stage);
            t.Feed(10f, 10f, frames: 36);
            Equal(KneeExtensionStage.Returning, t.Evaluator.Stage);
            t.Feed(90f, 90f, frames: 12);
            Equal(1, t.Evaluator.CompletedRepetitions);
        }

        private static void RightLegSelection()
        {
            var t = new Trial(TrainingLeg.Right); t.Prepare();
            t.Feed(10f, 90f, frames: 40);
            Equal(KneeExtensionStage.Extending, t.Evaluator.Stage);
            t.Feed(90f, 10f, frames: 36); t.Feed(90f, 90f, frames: 12);
            Equal(1, t.Evaluator.CompletedRepetitions);
        }

        private static void StandingRejected()
        {
            var t = new Trial(); t.Feed(90f, thigh: 0f, frames: 15);
            t.Feed(0f, thigh: 0f, frames: 40);
            Equal(KneeExtensionStage.Preparing, t.Evaluator.Stage);
            Equal(0, t.Evaluator.CompletedRepetitions);
        }

        private static void InvalidData()
        {
            var t = new Trial(); t.Prepare(); t.Feed(float.NaN);
            Check(t.Evaluator.BlockReason.Length > 0, "NaN 未拒绝");
            t.Settings.minimumExtensionDeg = 0f; t.Feed(90f);
            Check(t.Evaluator.BlockReason.Contains("参数"), "无效相对抬腿目标未拒绝");
        }

        private static void ProcessorFreshness()
        {
            var config = ScriptableObject.CreateInstance<MotionCaptureConfig>();
            try
            {
                var processor = new SensorDataProcessor(config);
                float[] times = Field<float[]>(processor, "lastFrameRealtimeSeconds");
                int[] counts = Field<int[]>(processor, "receivedFrameCounts");
                for (int i = 5; i <= 8; i++) { times[i] = 10f; counts[i] = 3; }
                Equal(15, processor.ReadLowerBodyMeasurement(11f, 1.5f, 1f).FreshMask);
                Equal(0, processor.ReadLowerBodyMeasurement(12f, 1.5f, 1f).FreshMask);
                for (int i = 5; i <= 7; i++) times[i] = 12f;
                Equal(7, processor.ReadLowerBodyMeasurement(12f, 1.5f, 1f).FreshMask);
                LowerBodyMeasurement selectedPair = processor.ReadLowerBodyMeasurement(12f, 3f, 3f, 3);
                Equal(3, selectedPair.FreshMask & 3);
                Check(!selectedPair.FailureReason.Contains("08") && !selectedPair.FailureReason.Contains("09"),
                    "左腿训练不应等待右腿传感器");
                times[8] = 12f;
                processor.RawQuaternions[8] = new Quaternion(float.NaN, 0, 0, 1);
                Check(processor.ReadLowerBodyMeasurement(12f, 1.5f, 1f).FailureReason.Contains("四元数"),
                    "非法四元数未阻止游戏采样");
            }
            finally { UnityEngine.Object.DestroyImmediate(config); }
        }

        private static void QuaternionMeasurements()
        {
            var config = ScriptableObject.CreateInstance<MotionCaptureConfig>();
            var root = new GameObject("Stage1 synthetic rig");
            try
            {
                var bones = new GameObject[9];
                var rotations = new Quaternion[9];
                var rest = new Quaternion[9];
                var targets = new Quaternion[9];
                var state = new MotionCaptureState(9);
                for (int i = 0; i < 9; i++) rotations[i] = rest[i] = targets[i] = Quaternion.identity;
                for (int thigh = 5; thigh <= 7; thigh += 2)
                {
                    bones[thigh] = new GameObject("Thigh " + thigh);
                    bones[thigh].transform.SetParent(root.transform, false);
                    bones[thigh + 1] = new GameObject("Calf " + (thigh + 1));
                    bones[thigh + 1].transform.SetParent(bones[thigh].transform, false);
                    bones[thigh + 1].transform.localPosition = Vector3.down;
                    var foot = new GameObject("Foot");
                    foot.transform.SetParent(bones[thigh + 1].transform, false);
                    foot.transform.localPosition = Vector3.down;
                    state.SetDeviceHasData(thigh, true);
                    state.SetDeviceHasData(thigh + 1, true);
                }
                var driver = new LowerBodyPoseDriver(config);
                driver.TryCalibrate(rotations, bones, rest, Quaternion.identity, state);
                Check(driver.TryMeasureLeg(0, rotations, out float thighDeg, out float kneeDeg), "站姿测量失败");
                Near(0f, kneeDeg);
                rotations[5] = rotations[7] = Quaternion.AngleAxis(-90f, Vector3.right);
                Check(driver.TryMeasureLeg(1, rotations, out thighDeg, out kneeDeg), "右侧坐姿失败");
                Near(90f, thighDeg); Near(90f, kneeDeg);
                rotations[6] = Quaternion.AngleAxis(-60f, Vector3.right);
                rotations[8] = Quaternion.AngleAxis(60f, Vector3.right);
                Check(driver.TryMeasureLeg(0, rotations, out thighDeg, out kneeDeg), "左侧伸膝失败");
                Near(30f, kneeDeg);
                Check(driver.TryMeasureLeg(1, rotations, out thighDeg, out kneeDeg), "右侧伸膝失败");
                Near(30f, kneeDeg);
                driver.ConstrainTargets(rotations, targets, new[] { false, false, false, false, false, true, true, true, true });
                // 人物与游戏使用同一膝角，避免画面已经伸直而判定仍显示 30°。
                Near(30f, Quaternion.Angle(targets[5], targets[6]));
                driver.TryMeasureLeg(0, rotations, out thighDeg, out kneeDeg); Near(30f, kneeDeg);
                Quaternion q = rotations[8];
                rotations[8] = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                driver.TryMeasureLeg(1, rotations, out thighDeg, out kneeDeg); Near(30f, kneeDeg);
                rotations[6] = Quaternion.AngleAxis(90f, Vector3.forward);
                Check(!driver.TryMeasureLeg(0, rotations, out thighDeg, out kneeDeg), "横向退化投影被当成伸直");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        private static void LowerBodyJumpGuard()
        {
            var config = ScriptableObject.CreateInstance<MotionCaptureConfig>();
            try
            {
                config.lowerBodyJumpRejectDeg = 45f;
                config.lowerBodyJumpRecoveryFrames = 3;
                config.lowerBodyJumpRecoveryToleranceDeg = 12f;
                var processor = new SensorDataProcessor(config);
                int[] counts = Field<int[]>(processor, "receivedFrameCounts");
                counts[5] = 1;

                MethodInfo guard = typeof(SensorDataProcessor).GetMethod(
                    "TryAcceptLowerBodyFrame",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Check(guard != null, "找不到下肢突跳保护入口");
                Quaternion jump = Quaternion.AngleAxis(80f, Vector3.right);
                Check(!(bool)guard.Invoke(processor, new object[] { 5, jump }),
                    "第一帧突跳不应直接通过");
                Check(!(bool)guard.Invoke(processor, new object[] { 5, jump }),
                    "第二帧突跳不应直接通过");
                Check((bool)guard.Invoke(processor, new object[] { 5, jump }),
                    "连续三帧一致的新姿态应允许恢复");
                Equal(2L, processor.LowerBodyGuardRejectedFrameCounts[5]);

                Quaternion normal = Quaternion.AngleAxis(20f, Vector3.right);
                Check((bool)guard.Invoke(processor, new object[] { 5, normal }),
                    "正常范围内的动作不应被拦截");
                Quaternion invalid = new Quaternion(float.NaN, 0f, 0f, 1f);
                Check(!(bool)guard.Invoke(processor, new object[] { 5, invalid }),
                    "非法四元数不应进入恢复确认或人物驱动");
            }
            finally { UnityEngine.Object.DestroyImmediate(config); }
        }

        private static T Field<T>(object instance, string name) =>
            (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);

        private static void Equal<T>(T expected, T actual) =>
            Check(Equals(expected, actual), $"expected={expected}; actual={actual}");

        private static void Near(float expected, float actual) =>
            Check(Math.Abs(expected - actual) < 0.02f, $"expected={expected}; actual={actual}");

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
