using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RehabPhotoGame.Editor
{
    public static class StageFiveTests
    {
        [MenuItem("Tools/Rehab Photo Game/Run Stage 5 Regression Tests")]
        public static void RunAll()
        {
            StageFourTests.RunAll();
            RunLogicTests();
            Run("第五段配置可加载", ConfigResource);
            Run("结果本地幂等保存且事件可关联", ResultPersistence);
            Run("正式界面准备门禁与安全停止优先", FormalFlow);
            Debug.Log("[Stage5 tests] PASS: 14 Stage 5 scenarios after 64 baseline scenarios.");
        }

        public static void RunLogicTests()
        {
            Run("启动需要本人确认与四传感器就绪", PreparationGate);
            Run("正常结束保留描述性结果且不能重复结束", NormalFinish);
            Run("暂停时间排除且恢复必须主动确认", PauseAndResume);
            Run("短时断流冻结拍摄并在连续新样本后自动恢复", SignalRecovery);
            Run("持续断流十五秒后才升级全屏异常", SignalTimeout);
            Run("拍照后必须回位且语音只描述游戏流程", ReturnPreparationAndVoice);
            Run("安全停止高于异常并禁止继续", SafetyPriority);
            Run("重标定冻结并要求重新准备", CalibrationChange);
            Run("配置在每次训练开始时复制", ConfigSnapshot);
            Run("时钟回拨不产生负时长或重复统计", ClockRollback);
            Run("返回设置后使用新 ID 且不串接照片", SeparateSessions);
        }

        private static TrainingFlowSession Begin(TrainingFlowSettings settings = null)
        {
            var flow = new TrainingFlowSession();
            Check(flow.Begin("squat", 3, true, true, 1,
                settings ?? new TrainingFlowSettings(), 0, "test"), "无法启动测试训练");
            return flow;
        }

        private static void PreparationGate()
        {
            var flow = new TrainingFlowSession();
            var settings = new TrainingFlowSettings();
            Check(!flow.Begin("squat", 3, false, true, 1, settings, 0, "test"), "未确认仍启动");
            Check(!flow.Begin("squat", 3, true, false, 1, settings, 0, "test"), "设备不齐仍启动");
            settings.sensor_error_timeout_seconds = float.NaN;
            Check(!flow.Begin("squat", 3, true, true, 1, settings, 0, "test"), "无效配置仍启动");
            Check(!flow.RecordCapture(1), "准备中拍摄");
        }

        private static void NormalFinish()
        {
            var flow = Begin();
            Check(flow.RecordCapture(1), "正常拍摄未记录");
            Check(flow.Finish(4, "user_end"), "未结束");
            Check(!flow.Finish(5, "target_completed"), "重复结束改变原因");
            Check(!flow.RecordCapture(5), "结束后仍计数");
            Equal("user_end", flow.Result.termination_reason);
            Equal("not_evaluated", flow.Result.clinical_pass);
            Equal("not_evaluated", flow.Result.quality_assessment);
            Near(4, flow.Result.active_seconds);
            Check(flow.Result.captured_photos == 1, "提前结束丢失照片");
        }

        private static void PauseAndResume()
        {
            var flow = Begin();
            flow.RecordCapture(1);
            Check(flow.Pause(2), "暂停未生效");
            Check(!flow.Resume(3), "未确认有效数据就继续");
            Check(!flow.RecordCapture(4), "暂停仍拍摄");
            flow.Tick(5, true, 5, 1);
            flow.Tick(6, true, 6, 1);
            Check(flow.CanRecover && flow.State == TrainingFlowState.Paused, "恢复自动继续");
            Check(flow.Resume(10), "不能主动恢复");
            flow.Finish(12, "user_end");
            Near(4, flow.Result.active_seconds);
            Near(8, flow.Result.paused_seconds);
            Check(flow.Result.pause_count == 1 && flow.Result.captured_photos == 1, "暂停丢计数");
        }

        private static void SignalRecovery()
        {
            var flow = Begin();
            flow.Tick(1, false, 0, 1);
            Check(flow.State == TrainingFlowState.SignalRecovery, "短时波动直接升级为全屏异常");
            Check(!flow.RecordCapture(1), "异常帧误拍");
            flow.Tick(2, true, 2, 1);
            flow.Tick(2.4, true, 2, 1);
            Check(!flow.CanRecover, "重复旧样本确认恢复");
            flow.Tick(2.8, true, 2.8f, 1);
            Check(flow.State == TrainingFlowState.Training && flow.AllowsCapture,
                "连续新样本后没有自动恢复");
            flow.Finish(3, "user_end");
            Near(1.8, flow.Result.sensor_error_seconds);
            Check(flow.Result.sensor_error_count == 1, "一次波动被重复计数");
        }

        private static void SignalTimeout()
        {
            var flow = Begin();
            flow.Tick(1, false, 0, 1);
            flow.Tick(15.9, false, 0, 1);
            Check(flow.State == TrainingFlowState.SignalRecovery, "十五秒前已经显示全屏异常");
            flow.Tick(16, false, 0, 1);
            Check(flow.State == TrainingFlowState.SensorError && flow.Result.termination_reason == null,
                "持续断流没有升级为可恢复的全屏异常");
            Check(!flow.RecordCapture(16), "全屏异常期间仍可拍摄");
            flow.Tick(17, true, 17, 1);
            flow.Tick(18, true, 18, 1);
            Check(flow.CanRecover && !flow.AllowsCapture, "全屏异常未等待人工确认");
            Check(flow.Resume(19) && flow.State == TrainingFlowState.Training, "人工确认后不能恢复");
            flow.Finish(20, "user_end");
        }

        private static void ReturnPreparationAndVoice()
        {
            var flow = Begin();
            Check(flow.RecordCapture(1), "正常拍摄未记录");
            Check(flow.State == TrainingFlowState.ReturnPreparation && !flow.AllowsCapture,
                "拍照后没有进入回位准备");
            Check(!flow.RecordCapture(1.5), "尚未回位再次拍照");
            Check(flow.CompleteReturnPreparation(2) && flow.AllowsCapture,
                "现有动作状态确认回位后没有进入下一次");
            Equal("拍照完成，请缓慢站直并站稳。",
                TrainingVoicePrompts.Text(TrainingVoiceCue.PhotoCompletedReturn,
                    PhotoTrainingMode.ShallowSquat));
            Equal("准备完成，请开始下一次浅蹲。",
                TrainingVoicePrompts.Text(TrainingVoiceCue.ReadyForNext,
                    PhotoTrainingMode.ShallowSquat));
            Check(TrainingVoicePrompts.Text(TrainingVoiceCue.SignalRecovering,
                    PhotoTrainingMode.ShallowSquat).Contains("正在恢复"),
                "信号语音没有说明冻结恢复状态");
        }

        private static void SafetyPriority()
        {
            var flow = Begin();
            flow.RecordCapture(1);
            flow.ObserveMeasurement(new LowerBodyMeasurement { IsValid = true, LeftKneeDeg = 45 });
            flow.Tick(2, false, 0, 1);
            Check(flow.StopForSafety(3), "异常时不能安全停止");
            flow.Tick(40, true, 40, 1);
            Check(flow.State == TrainingFlowState.SafetyStop, "异常恢复覆盖安全停止");
            Check(!flow.Resume(41) && !flow.RecordCapture(41), "安全停止后仍能继续");
            Check(!flow.Finish(42, "user_end"), "普通结束覆盖安全原因");
            Equal("review_required", flow.Result.review_status);
            Check(flow.Result.captured_photos == 1, "安全停止删除已拍照片");
            Check(flow.Result.has_last_valid_measurement && flow.Result.last_valid_measurement.LeftKneeDeg == 45,
                "安全停止未保留最后有效测量");
        }

        private static void CalibrationChange()
        {
            var flow = Begin();
            flow.Tick(1, true, 1, 2);
            Check(flow.State == TrainingFlowState.SensorError && !flow.CanRecover, "重标定没有冻结");
            flow.Tick(2, true, 2, 2);
            flow.Tick(3, true, 3, 2);
            Check(flow.CanRecover && !flow.AllowsCapture, "重标定自动拍摄");
        }

        private static void ConfigSnapshot()
        {
            var config = new TrainingFlowSettings();
            var flow = Begin(config);
            config.sensor_error_timeout_seconds = 1;
            config.config_version = "changed";
            flow.Tick(1, false, 0, 1);
            flow.Tick(3, false, 0, 1);
            Check(flow.State == TrainingFlowState.SignalRecovery, "中途修改影响旧训练");
            Equal("stage5-game-flow-v2", flow.Result.config_version);
            Near(15, flow.Result.config_snapshot.sensor_error_timeout_seconds);
        }

        private static void ClockRollback()
        {
            var flow = Begin();
            flow.Tick(5, true, 5, 1);
            flow.Tick(2, true, 2, 1);
            flow.Finish(7, "user_end");
            Near(7, flow.Result.duration_seconds);
        }

        private static void SeparateSessions()
        {
            var flow = Begin();
            flow.RecordCapture(1);
            string id = flow.Result.session_id;
            flow.StopForSafety(2);
            flow.ReturnToSetup();
            Check(!flow.Begin("squat", 3, false, true, 1, new TrainingFlowSettings(), 3, "test"), "新训练绕过准备");
            Check(flow.Begin("sit_to_stand", 2, true, true, 1, new TrainingFlowSettings(), 4, "test"), "新训练不能启动");
            Check(flow.Result.session_id != id && flow.Result.captured_photos == 0, "ID或计数串联");
        }

        private static void ConfigResource()
        {
            var config = Resources.Load<TrainingFlowConfig>("RehabPhotoGame/TrainingFlowConfig");
            Check(config != null && config.settings.IsValid, "配置资源不可用");
        }

        private static void ResultPersistence()
        {
            string dir = Path.Combine(Path.GetTempPath(), "koa-stage5-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new TrainingResultStore(dir);
                var flow = new TrainingFlowSession();
                var events = new List<TrainingFlowEvent>();
                flow.EventRaised += events.Add;
                flow.Begin("squat", 3, true, true, 1, new TrainingFlowSettings(), 0, "test");
                flow.RecordCapture(1);
                flow.Finish(2, "user_end");
                string path = store.Save(flow.Result);
                Equal(path, store.Save(flow.Result));
                Check(Directory.GetFiles(dir, "*.summary.json").Length == 1, "重复保存产生多份结果");
                var restored = JsonUtility.FromJson<TrainingSessionResult>(File.ReadAllText(path));
                Equal(flow.Result.session_id, restored.session_id);
                Check(restored.captured_photos == 1, "序列化丢失照片数");
                foreach (var item in events) store.Append(item.session_id, JsonUtility.ToJson(item));
                Check(File.ReadAllLines(Path.Combine(dir, flow.Result.session_id + ".events.jsonl")).Length == 3,
                    "事件日志缺失");
                bool rejected = false;
                try { store.Append("../escape", "{}"); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "允许非法会话路径");
                bool conflictRejected = false;
                restored.captured_photos = 2;
                try { store.Save(restored); } catch (IOException) { conflictRejected = true; }
                Check(conflictRejected, "同一ID允许覆盖不同结果");
            }
            finally
            {
                // 仅删除本测试刚创建的随机目录里的文件，不递归删除用户目录。
                if (Directory.Exists(dir))
                {
                    foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
                    Directory.Delete(dir);
                }
            }
        }

        private static void FormalFlow()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/City park/Scenes/RehabPhotoGame.unity");
            var host = new GameObject("Stage5 UI Test Host");
            GameObject canvasRoot = null;
            try
            {
                host.AddComponent<SeatedKneeExtensionRuntimeFix>();
                host.AddComponent<SitToStandRuntime>();
                host.AddComponent<ShallowSquatRuntime>();
                var ui = host.AddComponent<S1TrainingCanvasController>();
                Invoke(ui, "Start");
                canvasRoot = Field<Canvas>(ui, "canvas").gameObject;
                Check(!Field<Button>(ui, "modalPrimaryButton").interactable, "未准备仍可开始");
                Check(Field<GameObject>(ui, "preflightControls").activeSelf, "准备清单未显示");
                Button environment = Field<Button>(ui, "environmentButton");
                ClickButtonViaPointer(environment);
                Invoke(ui, "RefreshModal");
                Check((bool)typeof(S1TrainingCanvasController).GetField("environmentConfirmed",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ui), "确认按钮没有写入状态");
                Check(environment.GetComponentInChildren<TMPro.TMP_Text>().text.Contains("已确认："),
                    "确认后仍显示成未选方框");
                ClickButtonViaPointer(environment);
                Invoke(ui, "RefreshModal");
                Check(environment.GetComponentInChildren<TMPro.TMP_Text>().text.Contains("点击确认："),
                    "确认项不能撤销");
                Field<Button>(ui, "calibrateButton").onClick.Invoke();
                Check((string)typeof(S1TrainingCanvasController).GetField("preparationHint",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ui) != "", "标定按钮禁用且没有给出原因");
                Check(Field<Button>(ui, "flowDeviceButton").transform.parent.GetSiblingIndex() >
                    Field<GameObject>(ui, "modalOverlay").transform.GetSiblingIndex(), "模态遮挡底部入口");
                RenderUI(ui, "01-preflight");

                Button attachment = Field<Button>(ui, "attachmentButton");
                Button wellbeing = Field<Button>(ui, "wellbeingButton");
                ClickButtonViaPointer(environment);
                ClickButtonViaPointer(attachment);
                ClickButtonViaPointer(wellbeing);
                Invoke(ui, "RefreshModal");
                Check(environment.GetComponentInChildren<TMPro.TMP_Text>().text.Contains("已确认：") &&
                      attachment.GetComponentInChildren<TMPro.TMP_Text>().text.Contains("已确认：") &&
                      wellbeing.GetComponentInChildren<TMPro.TMP_Text>().text.Contains("已确认："),
                    "三项本人确认没有给出明确的已确认反馈");
                Check(Field<TMPro.TMP_Text>(ui, "modalBodyText").text.Contains("06～09"),
                    "本人确认完成后没有显示传感器阻塞原因");
                Field<Button>(ui, "calibrateButton").onClick.Invoke();
                Check(((string)typeof(S1TrainingCanvasController).GetField("preparationHint",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ui)).Contains("06～09"),
                    "下一步按钮没有说明需要连接的传感器");
                Invoke(ui, "RefreshModal");
                RenderUI(ui, "01b-preflight-confirmed");

                var flow = Field<TrainingFlowSession>(ui, "flow");
                var session = Field<S1TrainingSession>(ui, "session");
                string testDir = Path.Combine(Application.dataPath, "..", "Logs", "Stage5Review", "TestSessions");
                Set(ui, "resultStore", new TrainingResultStore(testDir));
                flow.Begin("squat", 3, true, true, 1, new TrainingFlowSettings(), Time.realtimeSinceStartup, "test");
                session.Start();
                Invoke(ui, "HandleCapture");
                Check(session.CompletedRepetitions == 1, "正常 UI 拍摄未记录");
                Invoke(ui, "PauseTraining");
                float frozenFocus = Field<PhotoFocusSession>(ui, "focusSession").Focus01;
                Vector2 frozenPosition = Field<RawImage>(ui, "photoImage").rectTransform.anchoredPosition;
                Invoke(ui, "LateUpdate");
                Near(frozenFocus, Field<PhotoFocusSession>(ui, "focusSession").Focus01);
                Check(frozenPosition == Field<RawImage>(ui, "photoImage").rectTransform.anchoredPosition,
                    "暂停时取景位置变化");
                Invoke(ui, "HandleCapture");
                Check(session.CompletedRepetitions == 1, "暂停后 UI 仍拍摄");
                Invoke(ui, "RefreshModal");
                RenderUI(ui, "02-paused");
                Invoke(ui, "SafetyStopTraining");
                Invoke(ui, "HandleCapture");
                Invoke(ui, "ResumeTraining");
                Check(flow.State == TrainingFlowState.SafetyStop && session.CompletedRepetitions == 1,
                    "安全停止后仍恢复或拍摄");
                Check(Field<TMPro.TMP_Text>(ui, "modalBodyText").text.Contains("不判定临床正确率"), "总结缺少边界");
                RenderUI(ui, "03-safety-review");
                Invoke(ui, "HandleModalPrimary");
                Check(session.State == S1TrainingRunState.Idle && flow.State == TrainingFlowState.Setup, "未返回设置");
                Check(!(bool)Get(ui, "PreflightConfirmed"), "新训练没有清除本人确认");
                flow.Begin("squat", 3, true, true, 0, new TrainingFlowSettings(), Time.realtimeSinceStartup, "test");
                session.Start();
                Invoke(ui, "UpdateStageFiveFlow");
                Check(flow.State == TrainingFlowState.SignalRecovery && session.State == S1TrainingRunState.Running,
                    "短时无效数据没有进入非阻塞恢复阶段");
                Invoke(ui, "HandleCapture");
                Check(session.CompletedRepetitions == 0, "冻结首帧仍拍摄");
                Invoke(ui, "RefreshModal");
                Invoke(ui, "RefreshFlowControls");
                Check(!Field<GameObject>(ui, "modalOverlay").activeSelf, "短时断流立即显示全屏异常");
                Check(Field<TMPro.TMP_Text>(ui, "safetyNotice").text.Contains("正在自动恢复"),
                    "短时断流没有非阻塞文字提示");
                RenderUI(ui, "04-signal-recovery");
                flow.Freeze(Time.realtimeSinceStartup, "test_full_screen_escalation");
                session.Pause();
                Invoke(ui, "SuspendGameRuntimes", "test_full_screen_escalation");
                Invoke(ui, "RefreshModal");
                Check(Field<GameObject>(ui, "modalOverlay").activeSelf, "持续断流没有全屏异常入口");
                RenderUI(ui, "04b-sensor-error");
                Invoke(ui, "EndTraining");
                Invoke(ui, "HandleModalPrimary");
                flow.Begin("squat", 3, true, true, 1, new TrainingFlowSettings(), Time.realtimeSinceStartup, "test");
                session.Start();
                Invoke(ui, "HandleCapture");
                flow.CompleteReturnPreparation(Time.realtimeSinceStartup);
                Invoke(ui, "HandleCapture");
                flow.CompleteReturnPreparation(Time.realtimeSinceStartup);
                Invoke(ui, "HandleCapture");
                Check(flow.State == TrainingFlowState.Review && session.State == S1TrainingRunState.Completed,
                    "完成目标未进入总结");
                Set(ui, "flashEndsAt", -1f);
                Invoke(ui, "LateUpdate");
                Near(0, Field<Image>(ui, "flashImage").color.a);
                Invoke(ui, "HandleCapture");
                Check(session.CompletedRepetitions == 3 && flow.Result.captured_photos == 3, "完成后重复拍摄");
                RenderUI(ui, "05-completed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                if (canvasRoot != null) UnityEngine.Object.DestroyImmediate(canvasRoot);
            }
        }

        private static void RenderUI(S1TrainingCanvasController ui, string name)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var canvas = Field<Canvas>(ui, "canvas");
            var cameraHost = new GameObject("Stage5 Review Camera");
            var camera = cameraHost.AddComponent<Camera>();
            var target = new RenderTexture(1920, 1080, 24);
            var texture = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.12f, .18f, .22f);
                camera.cullingMask = 1 << 5;
                camera.targetTexture = target;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1;
                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
                texture.Apply();
                string directory = Path.Combine(Application.dataPath, "..", "Logs", "Stage5Review");
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, name + ".png"), texture.EncodeToPNG());
            }
            finally
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.worldCamera = null;
                RenderTexture.active = previous;
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(cameraHost);
            }
        }

        private static void ClickButtonViaPointer(Button button)
        {
            EventSystem eventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
            Check(eventSystem != null, "场景缺少 EventSystem，正式按钮不能接收点击");
            ExecuteEvents.Execute(button.gameObject, new PointerEventData(eventSystem),
                ExecuteEvents.pointerClickHandler);
        }

        private static object Get(object obj, string name) => obj.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private static void Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Equal(string expected, string actual) => Check(expected == actual, expected + " != " + actual);
        private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .001, expected + " != " + actual);
        private static void Run(string name, Action action)
        {
            action();
            Console.WriteLine("[Stage5 tests] PASS " + name);
        }
    }
}
