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
            Run("四元数角度提取、左右符号和显示增益隔离", QuaternionMeasurements);
            Debug.Log("[Stage1 tests] PASS: 17 regression scenarios.");
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
            Run("四传感器同时停更时中止", AllSensorsStale);
            Run("单传感器失联、恢复后必须重新准备", DropoutRecovery);
            Run("返回途中失联不能补计上次动作", DropoutDuringReturn);
            Run("重标定和切换腿中止旧动作", RecalibrationAndLegSwitch);
            Run("双腿模式不能由一侧动作完成", Bilateral);
            Run("右腿模式独立选择正确侧", RightLegSelection);
            Run("站姿不能触发坐位膝伸", StandingRejected);
            Run("NaN角度和无效配置停止判定", InvalidData);
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("[Stage1 tests] PASS " + name); }
            catch (Exception exception) { throw new Exception("[Stage1 tests] FAIL " + name, exception); }
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
            t.Feed(40f);
            Equal(KneeExtensionStage.Extending, t.Evaluator.Stage);
            Near(0f, t.Evaluator.HoldSeconds);
            t.Feed(10f, frames: 20);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
        }

        private static void TargetHysteresis()
        {
            var t = new Trial(); t.Prepare(); t.Feed(19f);
            t.Feed(23f, frames: 35);
            Equal(KneeExtensionStage.Returning, t.Evaluator.Stage);
            t = new Trial(); t.Prepare(); t.Feed(19f); t.Feed(26f);
            Equal(KneeExtensionStage.Extending, t.Evaluator.Stage);
        }

        private static void UnstableWithinTarget()
        {
            var t = new Trial(); t.Prepare();
            for (int i = 0; i < 80; i++) t.Feed(i % 2 == 0 ? 2f : 18f);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
            Equal(0, t.Evaluator.CompletedRepetitions);
            Near(0f, t.Evaluator.HoldSeconds);
        }

        private static void RepeatedSample()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f);
            for (int i = 0; i < 1000; i++) t.Evaluator.Update(t.Last, t.Time + 0.5f);
            Near(0f, t.Evaluator.HoldSeconds);
            Equal(KneeExtensionStage.Holding, t.Evaluator.Stage);
        }

        private static void AllSensorsStale()
        {
            var t = new Trial(); t.Prepare(); t.Feed(10f, frames: 20);
            t.Evaluator.Update(t.Last, t.Time + 2f);
            Equal(KneeExtensionStage.Preparing, t.Evaluator.Stage);
            Check(t.Evaluator.BlockReason.Length > 0, "全部停更未阻止判定");
            Near(0f, t.Evaluator.HoldSeconds);
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
            var offline = t.Last; offline.FreshMask = 7;
            t.Evaluator.Update(offline, t.Time);
            t.Feed(10f, frames: 40);
            Equal(KneeExtensionStage.Preparing, t.Evaluator.Stage);
            Equal(0, t.Evaluator.CompletedRepetitions);
            t.ReachReturn(); t.Feed(90f, frames: 12);
            Equal(1, t.Evaluator.CompletedRepetitions);
        }

        private static void DropoutDuringReturn()
        {
            var t = new Trial(); t.ReachReturn();
            var offline = t.Last; offline.IsValid = false;
            t.Evaluator.Update(offline, t.Time);
            t.Feed(90f, frames: 20);
            Equal(0, t.Evaluator.CompletedRepetitions);
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
            t.Settings.targetKneeMaxDeg = 90f; t.Feed(90f);
            Check(t.Evaluator.BlockReason.Contains("参数"), "重叠目标/准备范围未拒绝");
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
                // 当前显示增益会把左膝 30°显示为 0°，游戏必须仍读取 30°。
                Near(0f, Quaternion.Angle(targets[5], targets[6]));
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
