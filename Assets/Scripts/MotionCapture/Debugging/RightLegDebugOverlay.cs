using UnityEngine;

/// <summary>
/// V77.30 右腿轻量诊断面板。补全旧包中的0字节脚本，避免场景已挂载该组件时
/// 出现 Missing Script。默认不显示，不改变任何驱动参数。
/// </summary>
[DisallowMultipleComponent]
public class RightLegDebugOverlay : MonoBehaviour
{
    [SerializeField] private MotionCaptureController controller;
    [SerializeField] private bool showOverlay = false;
    [SerializeField] private Rect windowRect = new Rect(600, 20, 500, 310);

    private GUIStyle textStyle;

    private void Awake()
    {
        if (controller == null)
            controller = GetComponent<MotionCaptureController>();
    }

    private void OnGUI()
    {
        if (!showOverlay) return;
        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                normal = { textColor = Color.black }
            };
        }

        windowRect = GUI.Window(912346, windowRect, DrawWindow, "Right Leg Pipeline");
    }

    private void DrawWindow(int id)
    {
        if (controller == null)
        {
            GUILayout.Label("MotionCaptureController 未绑定", textStyle);
            GUI.DragWindow();
            return;
        }

        Quaternion[] q = controller.TransformedQuaternions;
        MotionDataHub hub = controller.DataHub;
        GUILayout.Label("08号同步姿态: " + FormatQuaternion(q, RightLegPoseDriver.RightThighIndex), textStyle);
        GUILayout.Label("08号数据年龄: " + FormatAge(hub, RightLegPoseDriver.RightThighIndex), textStyle);
        GUILayout.Label("接收/异常/非法: " + FormatCounters(hub, RightLegPoseDriver.RightThighIndex), textStyle);
        GUILayout.Label("说明：骨骼目标与实际姿态可在 Controller 的 Leg Debug Logging 中查看。", textStyle);
        GUI.DragWindow();
    }

    private static string FormatQuaternion(Quaternion[] q, int index)
    {
        if (q == null || index < 0 || index >= q.Length) return "N/A";
        Vector3 e = q[index].eulerAngles;
        return $"({e.x:F1}, {e.y:F1}, {e.z:F1})";
    }

    private static string FormatAge(MotionDataHub hub, int index)
    {
        if (hub == null) return "N/A";
        double age = hub.GetDataAgeSeconds(index, System.DateTime.UtcNow);
        return double.IsInfinity(age) ? "N/A" : age.ToString("F3") + " s";
    }

    private static string FormatCounters(MotionDataHub hub, int index)
    {
        if (hub == null) return "N/A";
        return $"{hub.GetAcceptedFrameCount(index)} / {hub.GetAnomalyFrameCount(index)} / {hub.GetInvalidFrameCount(index)}";
    }
}
