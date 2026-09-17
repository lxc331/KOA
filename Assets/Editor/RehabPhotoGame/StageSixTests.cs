using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RehabPhotoGame.Editor
{
    public static class StageSixTests
    {
        [MenuItem("Tools/Rehab Photo Game/Run Stage 6 Regression Tests")]
        public static void RunAll()
        {
            StageFiveTests.RunAll();
            RunLogicTests();
            Run("S2 正式入口、步骤、JPG 与结果关联", FormalCombination);
            Debug.Log("[Stage6 tests] PASS: logic and native S2 integration, after Stage 1–5 regressions.");
        }

        public static void RunLogicTests()
        {
            Run("默认 A5+B5 配置复制并支持减量", Configuration);
            Run("可选择仅 A、仅 B 或 A+B 连续训练", SelectablePlans);
            Run("不能跳过对焦与放下直接坐站拍摄", OrderedA);
            Run("A 对焦不计照片，放下后才解锁坐站", FocusThenLower);
            Run("A 高处只拍一次，多组间必须坐回", MultipleA);
            Run("最后一组 A 保持站立进入 B", LastAToB);
            Run("B 低处快门后必须站回才计组合照片", CompleteB);
            Run("持续保持与重复快照不能重复收集", NoDuplicates);
            Run("短时无效快照不推进组合", InvalidData);
            Run("门禁期间快门与回位不能恢复后补算", GatedEdges);
            Run("动作版本与切腿变化回退且保留作品", SessionChanges);
            Run("长停顿只提醒一次且不减少进度", SlowTransition);
            Run("跳过未完成组不拍照，下一组重新准备", SkipIncomplete);
            Run("低处回位前重试不生成作品", RetryPendingLow);
            Run("停止禁止后续步骤和照片", Stop);
            Run("原快照适配器不解释原始角度", SnapshotAdapters);
            Run("复用第五段门禁记录 S2 组合结果", FlowIntegration);
            Run("现有三个动作判定器合成时序贯通 S2", EvaluatorReplay);
        }

        private sealed class Trial
        {
            public readonly S2CombinationSession Session = new S2CombinationSession();
            public readonly List<S2CombinationEvent> Events = new List<S2CombinationEvent>();
            public double Time;
            public Trial(int a = 1, int b = 1, S2TrainingPlan plan = S2TrainingPlan.AThenB)
            {
                Session.EventRaised += Events.Add;
                Check(Session.Begin(new S2CombinationSettings { groups_a = a, groups_b = b }, plan, 0), "启动失败");
            }
            public void Feed(bool ready = false, bool holding = false, bool returning = false,
                int completed = 0, int capture = 0, bool valid = true, bool reference = true,
                bool allowed = true, int version = 1, TrainingLeg leg = TrainingLeg.Left)
            {
                Time += .1;
                Session.Observe(new S2ActionFrame
                {
                    Mode = Session.RequiredMode, Version = version, Leg = leg, Valid = valid,
                    Reference = reference, Ready = ready, Holding = holding, Returning = returning,
                    Completed = completed, CaptureSequence = capture
                }, allowed, Time);
            }
            public void Focus()
            {
                Feed(ready: true); Feed(holding: true); Feed(returning: true);
            }
            public void Lower()
            {
                Feed(completed: 1, reference: false);
            }
            public void Rise()
            {
                Session.SetPhotoResources("RehabPhotoGame/Photos/A_Distant/spring_01", "RehabPhotoGame/Photos/B_High/pavilion_01");
                Feed(ready: true); Feed(holding: true); Feed(returning: true, capture: 1);
            }
            public void A() { Focus(); Lower(); Rise(); }
            public void Low(int count = 0)
            {
                Session.SetPhotoResources("", "RehabPhotoGame/Photos/C_Low/grass_mushrooms_01");
                Feed(ready: true, completed: count, capture: count);
                Feed(holding: true, completed: count, capture: count);
                Feed(returning: true, completed: count, capture: count + 1);
            }
        }

        private static void Configuration()
        {
            var settings = new S2CombinationSettings();
            Check(settings.IsValid && settings.groups_a == 5 && settings.groups_b == 5, "默认不符合计划");
            var s = new S2CombinationSession();
            s.Begin(settings, 0);
            settings.groups_a = 1;
            Check(s.Result.config_snapshot.groups_a == 5, "会话配置被外部修改");
            settings.groups_b = 6;
            Check(!settings.IsValid, "超出原型上限仍接受");
            settings.groups_b = 1; settings.transition_reminder_seconds = float.NaN;
            Check(!settings.IsValid, "NaN 仍接受");
        }
        private static void SelectablePlans()
        {
            var a = new Trial(1, 2, S2TrainingPlan.AOnly);
            Check(a.Session.Step == S2CombinationStep.SeatedFocus && a.Session.Result.training_plan == "a_only",
                "仅 A 没有从坐姿伸膝开始或计划未记录");
            a.A();
            Check(!a.Session.IsRunning && a.Session.Result.completed_a == 1 && a.Session.Result.completed_b == 0 &&
                a.Session.CompletedPhotos == 1, "仅 A 完成后仍被迫进入 B");

            var b = new Trial(2, 1, S2TrainingPlan.BOnly);
            Check(b.Session.Step == S2CombinationStep.StandingPreparation &&
                b.Session.RequiredMode == PhotoTrainingMode.ShallowSquat && b.Session.Result.training_plan == "b_only",
                "仅 B 没有从站立准备开始或计划未记录");
            b.Low();
            Check(b.Session.Step == S2CombinationStep.ReturnStanding && b.Session.CompletedPhotos == 0,
                "仅 B 在站回前提前生成照片");
            b.Feed(completed: 1, capture: 1, reference: false);
            Check(!b.Session.IsRunning && b.Session.Result.completed_a == 0 && b.Session.Result.completed_b == 1 &&
                b.Session.CompletedPhotos == 1, "仅 B 未独立完成");

            var both = new Trial();
            Check(both.Session.Result.training_plan == "a_then_b", "原 A+B 默认计划不再兼容");
        }
        private static void OrderedA()
        {
            var t = new Trial();
            t.Session.Observe(new S2ActionFrame { Mode = PhotoTrainingMode.SitToStand, Valid = true,
                Reference = true, Returning = true, CaptureSequence = 99 }, true, 1);
            Check(t.Session.Step == S2CombinationStep.SeatedFocus && t.Session.CompletedPhotos == 0, "跨步骤误拍");
            t.Feed(returning: true, capture: 99);
            Check(t.Session.CompletedPhotos == 0 && t.Session.Step == S2CombinationStep.SeatedFocus, "旧返回态被当成对焦");
        }
        private static void FocusThenLower()
        {
            var t = new Trial(); t.Focus();
            Check(t.Session.Step == S2CombinationStep.LowerLeg && t.Session.CompletedPhotos == 0, "对焦误计照片");
            t.Feed(returning: true);
            Check(t.Session.RequiredMode == PhotoTrainingMode.SeatedKneeExtension, "未放下就进入坐站");
            t.Lower();
            Check(t.Session.Step == S2CombinationStep.RiseHigh, "放下未解锁坐站");
        }
        private static void MultipleA()
        {
            var t = new Trial(2, 1); t.A();
            Check(t.Session.Step == S2CombinationStep.ReturnSitting && t.Session.CompletedPhotos == 1, "A 组间未等坐回");
            t.Feed(returning: true, capture: 1);
            Check(t.Session.CompletedPhotos == 1, "高处重复拍摄");
            t.Feed(completed: 1, capture: 1, reference: false);
            Check(t.Session.Step == S2CombinationStep.SeatedFocus, "坐回未返回对焦");
            t.A();
            Check(t.Session.Result.completed_a == 2, "第二组 A 无法完成");
        }
        private static void LastAToB()
        {
            var t = new Trial(); t.A();
            Check(t.Session.Step == S2CombinationStep.StandingPreparation && t.Session.RequiredMode == PhotoTrainingMode.ShallowSquat,
                "最后 A 要求多余坐回");
        }
        private static void CompleteB()
        {
            var t = new Trial(); t.A(); t.Low();
            Check(t.Session.Step == S2CombinationStep.ReturnStanding && t.Session.CompletedPhotos == 1 && t.Session.Result.pending_low_photo,
                "B 未站回就收集作品");
            t.Feed(completed: 1, capture: 1, reference: false);
            Check(!t.Session.IsRunning && t.Session.CompletedPhotos == 2 && t.Session.Result.completed_b == 1, "B 站回未完成");
        }
        private static void NoDuplicates()
        {
            var t = new Trial(1, 2); t.A(); t.Low();
            for (int i = 0; i < 20; i++) t.Feed(returning: true, capture: 1);
            Check(t.Session.CompletedPhotos == 1, "持蹲重复收集");
            t.Feed(completed: 1, capture: 1, reference: false);
            t.Feed(ready: true, completed: 1, capture: 1);
            Check(t.Session.CompletedPhotos == 2, "旧计数重复拍照");
            t.Low(1); t.Feed(completed: 2, capture: 2, reference: false);
            Check(t.Session.CompletedPhotos == 3 && !t.Session.IsRunning, "第二 B 无法完成");
        }
        private static void InvalidData()
        {
            var t = new Trial(); t.Feed(ready: true); t.Feed(holding: true);
            t.Feed(returning: true, valid: false);
            Check(t.Session.Step == S2CombinationStep.SeatedFocus && t.Session.CompletedPhotos == 0, "无效帧推进");
        }
        private static void GatedEdges()
        {
            var t = new Trial(); t.Feed(ready: true); t.Feed(holding: true);
            t.Feed(returning: true, allowed: false); t.Feed(returning: true);
            Check(t.Session.Step == S2CombinationStep.SeatedFocus && t.Session.CompletedPhotos == 0, "恢复补做旧对焦");
            t = new Trial(); t.A(); t.Low();
            t.Feed(completed: 1, capture: 1, reference: false, allowed: false);
            t.Feed(ready: true, completed: 1, capture: 1);
            Check(t.Session.CompletedPhotos == 1 && !t.Session.Result.pending_low_photo, "门禁期间回位补算组合");
        }
        private static void SessionChanges()
        {
            var t = new Trial(); t.A(); t.Low();
            t.Feed(returning: true, capture: 1, version: 2);
            Check(t.Session.Step == S2CombinationStep.StandingPreparation && t.Session.CompletedPhotos == 1, "重标定丢作品或未回退");
            t = new Trial(); t.Focus(); t.Feed(returning: true, leg: TrainingLeg.Right);
            Check(t.Session.Step == S2CombinationStep.SeatedFocus, "切腿串接旧对焦");
        }
        private static void SlowTransition()
        {
            var t = new Trial(); t.Focus();
            for (int i = 0; i < 80; i++) t.Feed(returning: true);
            Check(t.Session.Step == S2CombinationStep.LowerLeg && t.Session.Result.transition_reminders == 1 &&
                t.Session.Result.skipped_groups == 0, "缓慢被判错或重复提示");
            t.Lower(); t.Rise();
            Check(t.Session.CompletedPhotos == 1, "长停顿后不能继续");
        }
        private static void SkipIncomplete()
        {
            var t = new Trial(2, 1); t.Focus(); t.Session.Skip(t.Time);
            Check(t.Session.Step == S2CombinationStep.SeatedFocus && t.Session.GroupNumber == 2 && t.Session.CompletedPhotos == 0,
                "跳过误拍或串对焦");
            t.Session.Skip(t.Time); t.Session.Skip(t.Time);
            Check(!t.Session.IsRunning && t.Session.Result.skipped_groups == 3, "全跳过不结束计划");
        }
        private static void RetryPendingLow()
        {
            var t = new Trial(); t.A(); t.Low(); t.Session.Restart(t.Time, "user_restart");
            Check(t.Session.Step == S2CombinationStep.StandingPreparation && t.Session.CompletedPhotos == 1 && !t.Session.Result.pending_low_photo,
                "重试保留未完成低处作品");
        }
        private static void Stop()
        {
            var t = new Trial(); t.A(); t.Low(); t.Session.End("user_safety_stop");
            t.Feed(completed: 1, capture: 1);
            Check(t.Session.CompletedPhotos == 1 && !t.Session.Result.pending_low_photo, "停止仍完成旧组");
            Check(!t.Session.Skip(100), "停止后仍跳组");
        }
        private static void SnapshotAdapters()
        {
            var s = new KneeExtensionTrainingSnapshot { IsDataValid = true, HasReadyReference = true,
                Stage = KneeExtensionStage.Holding, KneeAngleDeg = 999, Leg = TrainingLeg.Right };
            Check(S2ActionFrame.From(s).Holding && S2ActionFrame.From(s).Valid, "重新按角度解释快照");
            s.IsSwitchingLeg = true;
            Check(!S2ActionFrame.From(s).Valid, "切腿仍有效");
            Check(!S2ActionFrame.From(new ShallowSquatTrainingSnapshot { IsDataValid = true }).Valid, "未启用模式仍有效");
        }
        private static void FlowIntegration()
        {
            var flow = new TrainingFlowSession();
            flow.Begin("s2_combination", 2, true, true, 1, new TrainingFlowSettings(), 0, "test", "S2");
            Check(flow.Result.stage == "S2", "总结仍写 S1");
            Check(flow.RecordCapture(1, false) && flow.State == TrainingFlowState.Training, "组合重复进入单动作回位");
            flow.Tick(2, false, 2, 1);
            Check(!flow.RecordCapture(2, false), "组合绕过门禁拍照");
            flow.StopForSafety(3);
            Check(!flow.RecordCapture(4, false), "安全停止后组合拍照");
        }

        private static void EvaluatorReplay()
        {
            var combination = new S2CombinationSession();
            combination.Begin(new S2CombinationSettings { groups_a = 1, groups_b = 1 }, 0);
            var knee = new SeatedKneeExtensionEvaluator(new SeatedKneeExtensionSettings(), TrainingLeg.Right);
            knee.Reset(TrainingLeg.Right, true);
            var stand = new SitToStandEvaluator(new SitToStandSettings()); stand.Reset(true);
            var squat = new ShallowSquatEvaluator(new ShallowSquatSettings()); squat.Reset(true);
            float time = 0;
            Action<PhotoTrainingMode, float, float, float, float, int> feed = (mode, lt, lk, rt, rk, n) =>
            {
                for (int i = 0; i < n; i++)
                {
                    time += .25f;
                    var sample = new LowerBodyMeasurement { IsValid = true, FreshMask = 15, CalibrationVersion = 1,
                        SampleTimeSeconds = time, LeftThighDeg = lt, LeftKneeDeg = lk, RightThighDeg = rt, RightKneeDeg = rk };
                    S2ActionFrame frame;
                    if (mode == PhotoTrainingMode.SeatedKneeExtension)
                    {
                        knee.Update(sample, time);
                        frame = S2ActionFrame.From(new KneeExtensionTrainingSnapshot { Stage = knee.Stage,
                            SessionVersion = 1, Leg = TrainingLeg.Right, IsDataValid = true,
                            HasReadyReference = knee.HasReadyReference, CompletedRepetitions = knee.CompletedRepetitions });
                    }
                    else if (mode == PhotoTrainingMode.SitToStand)
                    {
                        stand.Update(sample, time);
                        frame = S2ActionFrame.From(new SitToStandTrainingSnapshot { Stage = stand.Stage,
                            SessionVersion = 1, IsModeActive = true, IsDataValid = true, HasSeatedReference = stand.HasSeatedReference,
                            CompletedRepetitions = stand.CompletedRepetitions, CaptureSequence = stand.CaptureSequence });
                    }
                    else
                    {
                        squat.Update(sample, time);
                        frame = S2ActionFrame.From(new ShallowSquatTrainingSnapshot { Stage = squat.Stage,
                            SessionVersion = 1, IsModeActive = true, IsDataValid = true, HasStandingReference = squat.HasStandingReference,
                            CompletedRepetitions = squat.CompletedRepetitions, CaptureSequence = squat.CaptureSequence });
                    }
                    combination.Observe(frame, true, time);
                }
            };
            feed(PhotoTrainingMode.SeatedKneeExtension, 90, 90, 90, 90, 6);
            feed(PhotoTrainingMode.SeatedKneeExtension, 90, 90, 90, 10, 16);
            Check(combination.Step == S2CombinationStep.LowerLeg, "真实伸膝保持事件未衔接");
            feed(PhotoTrainingMode.SeatedKneeExtension, 90, 90, 90, 90, 6);
            Check(combination.Step == S2CombinationStep.RiseHigh, "真实放下事件未衔接");
            feed(PhotoTrainingMode.SitToStand, 90, 90, 90, 90, 6);
            feed(PhotoTrainingMode.SitToStand, 10, 5, 10, 5, 14);
            Check(combination.Step == S2CombinationStep.StandingPreparation && combination.CompletedPhotos == 1, "真实坐站未完成 A");
            feed(PhotoTrainingMode.ShallowSquat, 10, 5, 10, 5, 6);
            feed(PhotoTrainingMode.ShallowSquat, 40, 45, 40, 45, 14);
            Check(combination.Step == S2CombinationStep.ReturnStanding && combination.CompletedPhotos == 1, "真实浅蹲未进入回位");
            feed(PhotoTrainingMode.ShallowSquat, 10, 5, 10, 5, 6);
            Check(!combination.IsRunning && combination.CompletedPhotos == 2, "真实站回未完成 B");
        }

        private static void FormalCombination()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/City park/Scenes/RehabPhotoGame.unity");
            var host = new GameObject("Stage6 Test Host");
            GameObject root = null;
            try
            {
                host.AddComponent<SeatedKneeExtensionRuntimeFix>();
                host.AddComponent<SitToStandRuntime>();
                host.AddComponent<ShallowSquatRuntime>();
                var ui = host.AddComponent<S1TrainingCanvasController>();
                Invoke(ui, "Start");
                root = Field<Canvas>(ui, "canvas").gameObject;
                Field<Button>(ui, "s2StageButton").onClick.Invoke();
                Check(Field<bool>(ui, "s2Selected") && !Field<Button>(ui, "modalPrimaryButton").interactable,
                    "S2 入口丢失或绕过训练前门禁");
                Check(Field<Button>(ui, "s2AOnlyButton").gameObject.activeSelf &&
                    Field<Button>(ui, "s2BOnlyButton").gameObject.activeSelf &&
                    Field<Button>(ui, "s2BothButton").gameObject.activeSelf, "S2 训练内容选择入口未显示");
                Field<Button>(ui, "s2BOnlyButton").onClick.Invoke();
                Check(Field<S2TrainingPlan>(ui, "s2Plan") == S2TrainingPlan.BOnly &&
                    Field<PhotoTrainingMode>(ui, "trainingMode") == PhotoTrainingMode.ShallowSquat &&
                    Field<TMPro.TMP_Text>(ui, "targetValueText").text.StartsWith("B "),
                    "仅 B 选择没有切到浅蹲准备或组数未联动");
                Render(ui, "01-s2-preflight-b-only");
                Field<Button>(ui, "s2BothButton").onClick.Invoke();
                Check(Field<S2TrainingPlan>(ui, "s2Plan") == S2TrainingPlan.AThenB &&
                    Field<PhotoTrainingMode>(ui, "trainingMode") == PhotoTrainingMode.SeatedKneeExtension,
                    "A+B 选择没有恢复原组合起点");
                Check(Field<GameObject>(ui, "s2AlbumPanel").transform.GetSiblingIndex() <
                    Field<GameObject>(ui, "modalOverlay").transform.GetSiblingIndex(), "相册遮挡模态窗口");
                Check(Resources.Load<S2CombinationConfig>("RehabPhotoGame/S2CombinationConfig").settings.IsValid, "配置未加载");
                Render(ui, "01-s2-preflight");
                var flow = Field<TrainingFlowSession>(ui, "flow");
                var session = Field<S1TrainingSession>(ui, "session");
                var combination = Field<S2CombinationSession>(ui, "combination");
                string dir = Path.Combine(Application.dataPath, "..", "Logs", "Stage6Review", "TestSessions");
                Set(ui, "resultStore", new TrainingResultStore(dir));
                Set(ui, "s2Settings", new S2CombinationSettings { groups_a = 1, groups_b = 1 });
                flow.Begin("s2_combination", 2, true, true, 1, new TrainingFlowSettings(), Time.realtimeSinceStartup, "test", "S2");
                session.SetTarget(2); session.Start();
                combination.Begin(new S2CombinationSettings { groups_a = 1, groups_b = 1 }, 0);
                flow.Result.combinations = combination.Result;
                var t = new UiFeed(ui, combination);
                t.Feed(ready: true); t.Feed(holding: true); t.Feed(returning: true);
                Check(flow.Result.captured_photos == 0, "对焦写为照片");
                t.Feed(completed: 1, reference: false);
                // 验证互斥模式接口，无硬件时不运行真实判定器。
                Invoke(ui, "ActivateS2Step", false);
                Check(Field<PhotoTrainingMode>(ui, "trainingMode") == PhotoTrainingMode.SitToStand &&
                    !Field<SeatedKneeExtensionRuntimeFix>(ui, "training").IsTrainingModeActive, "模式没有互斥");
                combination.SetPhotoResources("RehabPhotoGame/Photos/A_Distant/spring_01", "RehabPhotoGame/Photos/B_High/pavilion_01");
                t.Feed(ready: true); t.Feed(holding: true); t.Feed(returning: true, capture: 1);
                Check(flow.Result.captured_photos == 1 && combination.CompletedPhotos == 1, "A UI 未记录");
                Render(ui, "02-s2-high-album");
                Invoke(ui, "ActivateS2Step", false);
                t.Feed(ready: true); t.Feed(holding: true); t.Feed(returning: true, capture: 1);
                Check(flow.Result.captured_photos == 1 && combination.Result.pending_low_photo, "B 没等站回");
                Render(ui, "03-s2-return-standing");
                t.Feed(completed: 1, capture: 1, reference: false);
                Check(flow.State == TrainingFlowState.Review && flow.Result.captured_photos == 2, "UI 未结束 S2");
                Invoke(ui, "RefreshModal");
                Invoke(ui, "RefreshS2Controls");
                Check(!Field<GameObject>(ui, "s2AlbumPanel").activeSelf && Field<GameObject>(ui, "feedbackControls").activeSelf,
                    "总结被相册遮住或反馈按钮隐藏");
                Render(ui, "04-s2-summary");
                Check(File.Exists(Path.Combine(dir, flow.Result.session_id + ".summary.json")), "总结未保存");
                foreach (var photo in combination.Result.photos)
                {
                    Check(photo.image_status == "saved" && File.Exists(Path.Combine(dir, photo.image_file)), "JPG 未保存");
                    var image = new Texture2D(2, 2);
                    try
                    {
                        Check(image.LoadImage(File.ReadAllBytes(Path.Combine(dir, photo.image_file))) && image.width == 1296 && image.height == 360,
                            "双联照片不可解码");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(image); }
                }
                Invoke(ui, "HandleCapture");
                Check(flow.Result.captured_photos == 2, "旧快门绕过组合");
                Invoke(ui, "HandleModalPrimary");
                Field<Button>(ui, "s1StageButton").onClick.Invoke();
                Check(!Field<bool>(ui, "s2Selected") && Field<Button>(ui, "kneeModeButton").gameObject.activeSelf,
                    "不能返回 S1");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private sealed class UiFeed
        {
            private readonly S1TrainingCanvasController ui;
            private readonly S2CombinationSession session;
            private double time;
            public UiFeed(S1TrainingCanvasController ui, S2CombinationSession session) { this.ui = ui; this.session = session; }
            public void Feed(bool ready = false, bool holding = false, bool returning = false, int completed = 0,
                int capture = 0, bool reference = true)
            {
                S2ActionFrame frame;
                if (session.RequiredMode == PhotoTrainingMode.SitToStand)
                    frame = S2ActionFrame.From(Field<SitToStandRuntime>(ui, "sitToStandTraining").CurrentSnapshot);
                else if (session.RequiredMode == PhotoTrainingMode.ShallowSquat)
                    frame = S2ActionFrame.From(Field<ShallowSquatRuntime>(ui, "shallowSquatTraining").CurrentSnapshot);
                else frame = S2ActionFrame.From(Field<SeatedKneeExtensionRuntimeFix>(ui, "training").CurrentSnapshot);
                frame.Valid = true; frame.Reference = reference; frame.Ready = ready; frame.Holding = holding;
                frame.Returning = returning; frame.Completed = completed; frame.CaptureSequence = capture;
                session.Observe(frame, true, time += .1);
            }
        }

        private static void Render(S1TrainingCanvasController ui, string name)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (Field<TrainingFlowSession>(ui, "flow").IsActive)
            {
                // 只为无硬件的界面截图显示既有面板；不改变正式启动门禁。
                Field<GameObject>(ui, "coachPanel").SetActive(true);
                Field<GameObject>(ui, "photoPanel").SetActive(true);
                Field<GameObject>(ui, "trainingPanel").SetActive(true);
                Invoke(ui, "RefreshTrainingPanel");
                Invoke(ui, "RefreshPhotoPanel");
                Invoke(ui, "UpdatePhotoTexture");
            }
            Invoke(ui, "RefreshS2Visuals"); Invoke(ui, "RefreshModal"); Invoke(ui, "RefreshFlowControls");
            typeof(StageFiveTests).GetMethod("RenderUI", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { ui, name });
            string source = Path.Combine(Application.dataPath, "..", "Logs", "Stage5Review", name + ".png");
            string dir = Path.Combine(Application.dataPath, "..", "Logs", "Stage6Review");
            Directory.CreateDirectory(dir);
            File.Copy(source, Path.Combine(dir, name + ".png"), true);
        }
        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("[Stage6 tests] PASS " + name); }
            catch (Exception e) { throw new InvalidOperationException("[Stage6 tests] FAIL " + name, e); }
        }
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private static void Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
    }
}
