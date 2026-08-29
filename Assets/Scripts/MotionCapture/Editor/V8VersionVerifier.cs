using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

public static class V8VersionVerifier
{
    private const string BuildVersion = "V8.22-LOWER-BODY-LINK-RECOVERY-20260829";

    [DidReloadScripts]
    private static void OnScriptsReloaded()
    {
        string[] controllerPaths = FindExactScriptPaths("MotionCaptureController");
        string[] armPaths = FindExactScriptPaths("ArmPoseDriver");
        string[] standalonePaths = FindExactScriptPaths("StandaloneBonePoseDriver");

        Debug.LogWarning(
            "\n##################################################\n" +
            "# V8.22 四传感器下肢通信恢复与双腿算法合并版已完成编译\n" +
            "# Build: " + BuildVersion + "\n" +
            "# 强制选择：06,07,08,09\n" +
            "# 上肢与躯干：01~05不参与标定或驱动，保持初始局部旋转\n" +
            "# 下肢：06+07左大小腿，08+09右大小腿\n" +
            "# 状态机：标定锁定后等待四路运行数据；运行故障不清空DataHub\n" +
            "# 诊断：区分Unity接收Hz、控制板发送Hz和链路到达率\n" +
            "# AI日志：连接即创建JSONL；每秒四路快照；异常退出仍保留已写内容\n" +
            "# 腿部：大小腿按源时间配对；无可靠配对时保持最后小腿姿态\n" +
            "# 恢复：骨骼角速度限幅；Zigbee四节点未同步时自动重发时隙命令\n" +
            "# 通信：连接时丢弃串口旧缓存；连续新序号周期自动重新同步\n" +
            "# 算法：保留新左右腿轴映射、twist限制与膝关节相对旋转路径\n" +
            "# 目录：每次连接自动创建 Logs/yyyyMMdd_HHmmss_fff\n" +
            "# MotionCaptureController: " + FormatPaths(controllerPaths) + "\n" +
            "# ArmPoseDriver: " + FormatPaths(armPaths) + "\n" +
            "# StandaloneBonePoseDriver: " + FormatPaths(standalonePaths) + "\n" +
            "# 进入Play后还应看到 [V8.22 ACTIVE]\n" +
            "##################################################");

        if (controllerPaths.Length != 1)
            Debug.LogError("[V8.22重复脚本检查] MotionCaptureController.cs应只有1份：" + FormatPaths(controllerPaths));
        if (armPaths.Length != 1)
            Debug.LogError("[V8.22重复脚本检查] ArmPoseDriver.cs应只有1份：" + FormatPaths(armPaths));
        if (standalonePaths.Length != 1)
            Debug.LogError("[V8.22重复脚本检查] StandaloneBonePoseDriver.cs应只有1份：" + FormatPaths(standalonePaths));
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
