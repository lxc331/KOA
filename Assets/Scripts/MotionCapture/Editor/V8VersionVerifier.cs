using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

public static class V8VersionVerifier
{
    private const string BuildVersion = "V8.13-ADAPTIVE-CALIBRATION-20260811-A";

    [DidReloadScripts]
    private static void OnScriptsReloaded()
    {
        string[] controllerPaths = FindExactScriptPaths("MotionCaptureController");
        string[] armPaths = FindExactScriptPaths("ArmPoseDriver");
        string[] standalonePaths = FindExactScriptPaths("StandaloneBonePoseDriver");

        Debug.LogWarning(
            "\n##################################################\n" +
            "# V8.13 九传感器自适应标定版已完成编译\n" +
            "# Build: " + BuildVersion + "\n" +
            "# 强制选择：01,02,03,04,05,06,07,08,09\n" +
            "# 上肢：01/02左大臂/左小臂，03/04右大臂/右小臂\n" +
            "# 躯干：05 -> Spine1\n" +
            "# 下肢：06+07左大小腿，08+09右大小腿\n" +
            "# 右大臂：保留V8.10局部Delta连续三轴矩阵\n" +
            "# 禁用：动作识别、标准姿态吸附、四姿态教学和顶部动作提示\n" +
            "# MotionCaptureController: " + FormatPaths(controllerPaths) + "\n" +
            "# ArmPoseDriver: " + FormatPaths(armPaths) + "\n" +
            "# StandaloneBonePoseDriver: " + FormatPaths(standalonePaths) + "\n" +
            "# 进入Play后还应看到 [V8.13 ACTIVE]\n" +
            "##################################################");

        if (controllerPaths.Length != 1)
            Debug.LogError("[V8.11重复脚本检查] MotionCaptureController.cs应只有1份：" + FormatPaths(controllerPaths));
        if (armPaths.Length != 1)
            Debug.LogError("[V8.11重复脚本检查] ArmPoseDriver.cs应只有1份：" + FormatPaths(armPaths));
        if (standalonePaths.Length != 1)
            Debug.LogError("[V8.11重复脚本检查] StandaloneBonePoseDriver.cs应只有1份：" + FormatPaths(standalonePaths));
    }

    private static string[] FindExactScriptPaths(string scriptName)
    {
        return AssetDatabase.FindAssets(scriptName + " t:MonoScript")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => Path.GetFileNameWithoutExtension(path) == scriptName)
            .Distinct().OrderBy(path => path).ToArray();
    }

    private static string FormatPaths(string[] paths)
    {
        return paths == null || paths.Length == 0 ? "未找到" : string.Join(" | ", paths);
    }
}
