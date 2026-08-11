using UnityEngine;

/// <summary>
/// 左腿专用调试面板。
/// 挂到和 MotionCaptureController 同一个 GameObject 上即可。
/// 这版增强了：
/// 1. 显示左腿参考姿态
/// 2. 显示膝关节相对角
/// 3. 显示最后错误信息
/// 4. 允许直接在 Inspector 中调左大腿/左小腿轴补偿
/// 5. 允许直接在 Inspector 中调左大腿幅度增益
/// 6. 允许直接在 Inspector 中调死区、传感器滤波、骨骼平滑
/// 7. 以“从休息姿态偏离角度”替代容易跳变的 Euler 主显示
/// </summary>
[DisallowMultipleComponent]
public class LeftLegDebugOverlay : MonoBehaviour
{
    [Header("引用")]
    [SerializeField] private MotionCaptureController controller;

    [Header("显示")]
    [SerializeField] private bool showOverlay = true;
    [SerializeField] private bool showBoneAxes = true;
    [SerializeField] private bool compactMode = false;
    [SerializeField] private bool showRawEulerAsSecondaryInfo = true;

    [Header("窗口位置")]
    [SerializeField] private Rect windowRect = new Rect(20, 20, 560, 760);

    [Header("骨骼轴绘制")]
    [SerializeField] private float axisLength = 0.12f;

    [Header("左腿骨骼轴补偿（实时调试）")]
    [SerializeField] private Vector3 thighBoneAxisOffsetEuler = Vector3.zero;
    [SerializeField] private Vector3 calfBoneAxisOffsetEuler = Vector3.zero;

    [Header("左大腿幅度增益（实时调试）")]
    [SerializeField] private float thighRotationGain = 1.0f;

    [Header("抖动抑制（实时调试）")]
    [SerializeField] private float thighDeadZoneDeg = 2.0f;
    [SerializeField] private float calfDeadZoneDeg = 3.0f;
    [SerializeField] private float sensorFilterSpeed = 10.0f;
    [SerializeField] private float smoothingSpeed = 10.0f;

    private const int LeftThighIndex = 5;
    private const int LeftCalfIndex = 6;

    private GUIStyle labelStyle;
    private GUIStyle titleStyle;
    private GUIStyle boxStyle;

    private void Awake()
    {
        if (controller == null)
            controller = GetComponent<MotionCaptureController>();
    }

    private void Update()
    {
        if (controller == null) return;

        LeftLegPoseDriver driver = GetLeftLegDriver();
        if (driver != null)
        {
            driver.ThighBoneAxisOffsetEuler = thighBoneAxisOffsetEuler;
            driver.CalfBoneAxisOffsetEuler = calfBoneAxisOffsetEuler;
            driver.ThighRotationGain = thighRotationGain;
            driver.ThighDeadZoneDeg = thighDeadZoneDeg;
            driver.CalfDeadZoneDeg = calfDeadZoneDeg;
            driver.SensorFilterSpeed = sensorFilterSpeed;
            driver.SmoothingSpeed = smoothingSpeed;
        }
    }

    private void OnGUI()
    {
        if (!showOverlay) return;

        EnsureGuiStyles();
        windowRect = GUI.Window(912345, windowRect, DrawWindow, "Left Leg Debug");
    }

    private void DrawWindow(int id)
    {
        if (controller == null)
        {
            GUILayout.Label("MotionCaptureController 未绑定", titleStyle);
            GUI.DragWindow();
            return;
        }

        MotionCaptureState state = controller.State;
        Quaternion[] transformed = controller.TransformedQuaternions;
        MotionCaptureConfig cfg = controller.Config;
        LeftLegPoseDriver driver = GetLeftLegDriver();

        Transform thigh = FindBone(cfg, LeftThighIndex);
        Transform calf = FindBone(cfg, LeftCalfIndex);

        GUILayout.BeginVertical(boxStyle);

        DrawTitle("Runtime");
        DrawLine("Connected", state != null && state.IsConnected ? "True" : "False");
        DrawLine("HasAnyData", state != null && state.HasAnyData ? "True" : "False");
        DrawLine("Stable", state != null && state.IsStable ? "True" : "False");
        DrawLine("Driving", state != null && state.IsDriving ? "True" : "False");
        DrawLine("Logging", controller.IsLogging ? "True" : "False");

        if (driver != null)
        {
            DrawLine("Calibrated", driver.IsCalibrated ? "True" : "False");
            DrawLine("LastError", string.IsNullOrEmpty(driver.LastError) ? "(none)" : driver.LastError);
        }

        if (!compactMode)
        {
            GUILayout.Space(6);
            DrawTitle("Sensor Transformed Euler");
            DrawQuaternionEuler("Thigh[5]", transformed, LeftThighIndex);
            DrawQuaternionEuler("Calf[6]", transformed, LeftCalfIndex);

            GUILayout.Space(6);
            DrawTitle("Bone Angle From Rest");
            DrawAngleFromRest("Thigh Angle", thigh, driver != null ? driver.ThighBoneRestLocal : Quaternion.identity);
            DrawAngleFromRest("Calf Angle", calf, driver != null ? driver.CalfBoneRestLocal : Quaternion.identity);

            if (showRawEulerAsSecondaryInfo)
            {
                GUILayout.Space(6);
                DrawTitle("Bone Local Euler (Secondary)");
                DrawTransformEuler("Thigh Bone", thigh);
                DrawTransformEuler("Calf Bone", calf);
            }

            GUILayout.Space(6);
            DrawTitle("Bone Name");
            DrawLine("Thigh", GetBoneName(cfg, LeftThighIndex));
            DrawLine("Calf", GetBoneName(cfg, LeftCalfIndex));

            if (driver != null)
            {
                GUILayout.Space(6);
                DrawTitle("Calibration Reference");
                DrawLine("Thigh Ref", FormatVec3(driver.ThighSensorReference.eulerAngles));
                DrawLine("Calf Ref", FormatVec3(driver.CalfSensorReference.eulerAngles));
                DrawLine("Knee Ref", FormatVec3(driver.KneeSensorRelativeReference.eulerAngles));

                GUILayout.Space(6);
                DrawTitle("Live Relative");
                DrawLine("Knee Rel", FormatVec3(driver.CurrentKneeRelativeRotation.eulerAngles));
                DrawLine("Knee Angle", driver.CurrentKneeRelativeAngleDeg.ToString("F2") + " deg");

                GUILayout.Space(6);
                DrawTitle("Axis Offset");
                DrawLine("Thigh Offset", FormatVec3(thighBoneAxisOffsetEuler));
                DrawLine("Calf Offset", FormatVec3(calfBoneAxisOffsetEuler));
                DrawLine("Thigh Gain", thighRotationGain.ToString("F2"));

                GUILayout.Space(6);
                DrawTitle("Jitter Control");
                DrawLine("Thigh DeadZone", thighDeadZoneDeg.ToString("F2") + " deg");
                DrawLine("Calf DeadZone", calfDeadZoneDeg.ToString("F2") + " deg");
                DrawLine("Sensor Filter", sensorFilterSpeed.ToString("F2"));
                DrawLine("Smoothing", smoothingSpeed.ToString("F2"));
            }
        }

        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    private void OnDrawGizmos()
    {
        if (!showBoneAxes) return;

        MotionCaptureController c = controller != null ? controller : GetComponent<MotionCaptureController>();
        if (c == null || c.Config == null) return;

        Transform thigh = FindBone(c.Config, LeftThighIndex);
        Transform calf = FindBone(c.Config, LeftCalfIndex);

        DrawAxes(thigh, axisLength);
        DrawAxes(calf, axisLength * 0.9f);
    }

    private void DrawAxes(Transform t, float len)
    {
        if (t == null) return;

        Vector3 p = t.position;

        Gizmos.color = Color.red;
        Gizmos.DrawLine(p, p + t.right * len);

        Gizmos.color = Color.green;
        Gizmos.DrawLine(p, p + t.up * len);

        Gizmos.color = Color.blue;
        Gizmos.DrawLine(p, p + t.forward * len);
    }

    private void DrawQuaternionEuler(string label, Quaternion[] qs, int index)
    {
        if (qs == null || index < 0 || index >= qs.Length)
        {
            DrawLine(label, "N/A");
            return;
        }

        Vector3 e = qs[index].eulerAngles;
        DrawLine(label, FormatVec3(e));
    }

    private void DrawTransformEuler(string label, Transform t)
    {
        if (t == null)
        {
            DrawLine(label, "Bone Not Found");
            return;
        }

        DrawLine(label, FormatVec3(t.localEulerAngles));
    }

    private void DrawAngleFromRest(string label, Transform t, Quaternion restLocal)
    {
        if (t == null)
        {
            DrawLine(label, "Bone Not Found");
            return;
        }

        float angle = Quaternion.Angle(restLocal, t.localRotation);
        DrawLine(label, angle.ToString("F2") + " deg");
    }

    private void DrawTitle(string text)
    {
        GUILayout.Label(text, titleStyle);
    }

    private void DrawLine(string key, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(key, labelStyle, GUILayout.Width(150));
        GUILayout.Label(value, labelStyle);
        GUILayout.EndHorizontal();
    }

    private string FormatVec3(Vector3 v)
    {
        return $"({v.x:F1}, {v.y:F1}, {v.z:F1})";
    }

    private Transform FindBone(MotionCaptureConfig cfg, int index)
    {
        if (cfg == null || cfg.boneNames == null) return null;
        if (index < 0 || index >= cfg.boneNames.Length) return null;

        string boneName = cfg.boneNames[index];
        if (string.IsNullOrEmpty(boneName)) return null;

        GameObject go = GameObject.Find(boneName);
        return go != null ? go.transform : null;
    }

    private string GetBoneName(MotionCaptureConfig cfg, int index)
    {
        if (cfg == null || cfg.boneNames == null) return "N/A";
        if (index < 0 || index >= cfg.boneNames.Length) return "N/A";
        return cfg.boneNames[index];
    }

    private LeftLegPoseDriver GetLeftLegDriver()
    {
        if (controller == null) return null;

        var field = typeof(MotionCaptureController).GetField(
            "leftLegDriver",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (field == null) return null;
        return field.GetValue(controller) as LeftLegPoseDriver;
    }

    private void EnsureGuiStyles()
    {
        if (labelStyle != null) return;

        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            normal = { textColor = Color.white }
        };

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.cyan }
        };

        boxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(10, 10, 10, 10)
        };
    }
}