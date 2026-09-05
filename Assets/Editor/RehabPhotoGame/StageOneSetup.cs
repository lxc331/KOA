using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RehabPhotoGame.Editor
{
    public static class StageOneSetup
    {
        public const string SourceScene = "Assets/City park/Scenes/SampleScene.unity";
        public const string TestScene = "Assets/City park/Scenes/RehabPhotoGame.unity";

        [MenuItem("Tools/Rehab Photo Game/Open Stage 1 - Seated Knee Extension")]
        public static void OpenScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请先退出播放模式。");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureScene();
        }

        // 可用于批处理验证；不修改 Build Settings、原动捕场景或设备配置。
        public static void ValidateAndCreateScene()
        {
            StageOneTests.RunAll();
            EnsureScene();
            Debug.Log("[Stage1] Compilation, regression tests and scene setup passed.");
        }

        private static void EnsureScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TestScene) == null)
            {
                if (!AssetDatabase.CopyAsset(SourceScene, TestScene))
                    throw new InvalidOperationException("无法复制动捕测试场景：" + SourceScene);
                Scene scene = EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single);
                var controller = UnityEngine.Object.FindObjectOfType<MotionCaptureController>();
                if (controller == null) throw new InvalidOperationException("场景中缺少 MotionCaptureController");
                var go = new GameObject("Seated Knee Extension - Stage 1");
                var prototype = go.AddComponent<SeatedKneeExtensionPrototype>();
                var serialized = new SerializedObject(prototype);
                serialized.FindProperty("motionCapture").objectReferenceValue = controller;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException("无法保存坐位膝伸技术验证场景");
            }
            else
                EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single);

            if (UnityEngine.Object.FindObjectsOfType<SeatedKneeExtensionPrototype>().Length != 1)
                throw new InvalidOperationException("技术验证场景应有且只有一个 SeatedKneeExtensionPrototype");
            if (UnityEngine.Object.FindObjectsOfType<MotionCaptureController>().Length != 1)
                throw new InvalidOperationException("技术验证场景应有且只有一个动捕控制器");
        }
    }
}
