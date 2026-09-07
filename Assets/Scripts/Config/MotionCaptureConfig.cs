using UnityEngine;

/// <summary>
/// 动捕系统可序列化配置（ScriptableObject）。
/// 
/// 使用方式：
///   1. 在 Unity 编辑器中右键 → Create → MotionCapture → Config 创建配置资产
///   2. 在 Inspector 中填写对应角色的骨骼名称、传感器数量、串口参数等
///   3. 将配置资产拖入 MotionCaptureController 组件的 config 字段
///   4. 更换角色模型时只需切换配置资产，无需修改代码
/// 
/// 所有运行时参数集中于此，避免在代码中硬编码。
/// </summary>
[CreateAssetMenu(fileName = "MotionCaptureConfig", menuName = "MotionCapture/Config")]
public class MotionCaptureConfig : ScriptableObject
{
    // ═══════════════════════════════════════════════════════════════
    //  设备与骨骼映射
    // ═══════════════════════════════════════════════════════════════

    [Header("设备")]
    [Tooltip("IMU 传感器的总数量，必须与 boneNames 数组长度一致")]
    public int deviceCount = 9;

    [Header("骨骼映射")]
    [Tooltip("每个传感器对应的骨骼节点名称，长度必须等于 deviceCount。\n" +
             "索引 0-3 = 手臂(左上/左前/右上/右前)，4 = 脊柱，5-8 = 腿部(左大腿/左小腿/右大腿/右小腿)")]
    public string[] boneNames = new string[]
    {
        // 索引 0: 左上臂传感器 → 3D 模型左上臂骨骼
        "Bip01 L UpperArm",
        // 索引 1: 左前臂传感器 → 3D 模型左前臂骨骼
        "Bip01 L Forearm",
        // 索引 2: 右上臂传感器 → 3D 模型右上臂骨骼
        "Bip01 R UpperArm",
        // 索引 3: 右前臂传感器 → 3D 模型右前臂骨骼
        "Bip01 R Forearm",
        // 索引 4: 躯干传感器 → 3D 模型脊柱骨骼
        "Bip01 Spine2",
        // 索引 5: 左大腿传感器 → 3D 模型左大腿骨骼
        "Bip01 L Thigh",
        // 索引 6: 左小腿传感器 → 3D 模型左小腿骨骼
        "Bip01 L Calf",
        // 索引 7: 右大腿传感器 → 3D 模型右大腿骨骼
        "Bip01 R Thigh",
        // 索引 8: 右小腿传感器 → 3D 模型右小腿骨骼
        "Bip01 R Calf"
    };

    [Header("角色根节点")]
    [Tooltip("Unity 场景中角色最顶层 GameObject 的名称，用于查找角色根 Transform")]
    public string avatarRootName = "renwu";

    // ═══════════════════════════════════════════════════════════════
    //  串口通信
    // ═══════════════════════════════════════════════════════════════

    [Header("串口默认值")]
    [Tooltip("默认串口名称（如 COM9、/dev/ttyUSB0），程序启动时自动尝试匹配")]
    public string defaultPort = "COM9";

    [Tooltip("默认波特率，需与 IMU 硬件发送端一致")]
    public int defaultBaud = 115200;

    // ═══════════════════════════════════════════════════════════════
    //  角度限制
    //  每个骨骼的局部旋转限制（度），按 (X, Y, Z) 轴指定最小/最大值。
    //  用于防止手臂穿模、膝盖反弯等不自然姿态。
    // ═══════════════════════════════════════════════════════════════

    [Header("角度限制（每骨骼 XYZ）")]
    [Tooltip("每个骨骼各轴允许的最小角度（度），索引顺序与 boneNames 一致")]
    public Vector3[] minLocalAngles = new Vector3[]
    {
        new Vector3(-60, -60, -60),   // [0] 左上臂：肩关节，三轴均 ±60°
        new Vector3(  0, -30, -45),   // [1] 左前臂：肘只能弯不能反弯，所以 X 最小 0°
        new Vector3(-60, -60, -60),   // [2] 右上臂：同左上臂
        new Vector3(  0, -30, -45),   // [3] 右前臂：同左前臂
        new Vector3(-30, -30, -30),   // [4] 脊柱：适度限制躯干扭转
        new Vector3(-50, -40, -40),   // [5] 左大腿：髋关节
        new Vector3(-40, -30, -30),   // [6] 左小腿：膝关节
        new Vector3(-50, -40, -40),   // [7] 右大腿：同左大腿
        new Vector3(-40, -30, -30)    // [8] 右小腿：同左小腿
    };

    [Tooltip("每个骨骼各轴允许的最大角度（度），索引顺序与 boneNames 一致")]
    public Vector3[] maxLocalAngles = new Vector3[]
    {
        new Vector3( 60,  60,  60),   // [0] 左上臂
        new Vector3(145,  30,  45),   // [1] 左前臂：肘屈伸最大约 145°
        new Vector3( 60,  60,  60),   // [2] 右上臂
        new Vector3(145,  30,  45),   // [3] 右前臂
        new Vector3( 30,  30,  30),   // [4] 脊柱
        new Vector3( 50,  40,  40),   // [5] 左大腿
        new Vector3( 40,  30,  30),   // [6] 左小腿
        new Vector3( 50,  40,  40),   // [7] 右大腿
        new Vector3( 40,  30,  30)    // [8] 右小腿
    };

    // ═══════════════════════════════════════════════════════════════
    //  异常检测
    //  通过分析最近几帧的角度变化量，过滤传感器突变/脉冲干扰。
    // ═══════════════════════════════════════════════════════════════

    [Header("异常检测")]
    [Tooltip("是否启用异常帧过滤（建议开启）")]
    public bool anomalyEnable = true;

    [Tooltip("异常检测的滑动窗口大小：保留最近多少帧用于对比分析")]
    public int anomalyBufferSize = 10;

    [Tooltip("单帧角度跳变阈值（度）：超过此值的帧被视为异常并丢弃")]
    public float anomalyThreshold = 45f;

    [Header("下肢突跳保护")]
    [Tooltip("06～09 单次变化超过该角度时，先视为可疑帧，不直接驱动人物或游戏判定")]
    [Range(15f, 90f)]
    public float lowerBodyJumpRejectDeg = 45f;

    [Tooltip("可疑姿态需要连续多少帧彼此接近，才作为传感器恢复后的真实姿态接收")]
    [Range(2, 5)]
    public int lowerBodyJumpRecoveryFrames = 3;

    [Tooltip("连续可疑帧之间允许的角度差；超过后重新开始恢复确认")]
    [Range(1f, 30f)]
    public float lowerBodyJumpRecoveryToleranceDeg = 12f;

    // ═══════════════════════════════════════════════════════════════
    //  稳定性检测
    //  开始驱动前，确认传感器数据已稳定（穿戴者保持静止）。
    //  避免在抖动/校准未完成时进入驱动状态导致姿态跳变。
    // ═══════════════════════════════════════════════════════════════

    [Header("稳定性")]
    [Tooltip("连续多少帧角速度低于阈值才算稳定")]
    public int requiredStableFrames = 20;

    [Tooltip("每帧允许的最大角速度（度/帧），低于此值视为该设备稳定")]
    public float maxAngularSpeedDeg = 3f;

    [Tooltip("是否要求所有 9 个传感器都稳定才允许开始（严格模式）")]
    public bool requireAllDevices = false;

    [Tooltip("宽松模式下至少需要多少个设备稳定才允许开始")]
    public int minStableDevices = 1;

    [Tooltip("若某个骨骼在场景中未找到对应 GameObject，是否在稳定性判断中忽略它")]
    public bool ignoreBonesWithoutObject = true;

    // ═══════════════════════════════════════════════════════════════
    //  旋转驱动参数
    // ═══════════════════════════════════════════════════════════════

    [Header("驱动")]
    [Tooltip("Slerp 平滑插值速度因子：值越大动作越灵敏、越小越平滑（建议 5-15）")]
    public float smoothSpeed = 10f;

    [Tooltip("去抖阈值（度）：旋转变化小于此值时不更新目标，避免静止时微抖")]
    public float debounceThresholdDeg = 5f;

    [Header("右腿定向约束")]
    [Tooltip("只反转右大腿相对站姿的内收/外展方向；不会反转前踢、后踢或下蹲方向")]
    public bool rightThighInvertLateral = true;

    [Tooltip("右大腿绕自身长轴允许的最大扭转角；用于抑制右腿拧转")]
    [Range(0f, 30f)]
    public float rightThighMaxTwistDeg = 12f;

    [Tooltip("右大腿偏离站立方向的最大摆角；阻止传感器轴异常时大腿越过180度并连续翻转")]
    [Range(60f, 140f)]
    public float rightThighMaxSwingDeg = 110f;

    [Tooltip("右小腿在骨骼局部空间中的膝关节铰链轴；Bip01 小腿沿 Local X 延伸，实际屈膝轴为 Local Z")]
    public Vector3 rightKneeHingeAxisLocal = Vector3.forward;

    [Tooltip("膝角小于该值时直接回到站立零位，消除静止微抖")]
    [Range(0f, 10f)]
    public float rightKneeNeutralDeadZoneDeg = 3f;

    [Tooltip("08/09数据超过该时长仍未更新时不再锁住旧膝角，开始回到站立零位")]
    [Range(0.5f, 3f)]
    public float rightLegInputFreshnessSeconds = 1.2f;

    [Tooltip("右腿输入陈旧后回到站立零位的速度")]
    [Range(1f, 20f)]
    public float rightLegReturnToNeutralSpeed = 8f;

    [Header("双腿骨段映射")]
    [Tooltip("大腿向前屈曲的最大角度；坐下通常约 70-100 度")]
    [Range(60f, 130f)]
    public float lowerBodyThighMaxFlexionDeg = 115f;

    [Tooltip("大腿允许向后伸展的最大角度")]
    [Range(0f, 45f)]
    public float lowerBodyThighMaxExtensionDeg = 30f;

    [Tooltip("膝关节最大屈曲角度")]
    [Range(90f, 150f)]
    public float lowerBodyKneeMaxFlexionDeg = 135f;

    [Tooltip("小于该角度的膝部变化视为静止抖动")]
    [Range(0f, 10f)]
    public float lowerBodyKneeNeutralDeadZoneDeg = 2f;

    [Tooltip("小腿骨骼局部膝铰链轴；Bip01 小腿沿 Local X 延伸，实际屈膝轴为 Local Z")]
    public Vector3 lowerBodyKneeHingeAxisLocal = Vector3.forward;

    [Tooltip("膝弯曲方向；当前 Bip01 腿骨绕 Local Z 正方向弯曲")]
    [Range(-1f, 1f)]
    public float lowerBodyKneeFlexionSign = 1f;

    // ═══════════════════════════════════════════════════════════════
    //  根节点位移补偿
    //  IMU 只能测量旋转，不能测量位移。下蹲时如果不补偿根节点高度，
    //  角色会悬浮在半空中只做腿部弯曲动作。此功能通过读取足部骨骼
    //  的世界坐标反算根节点应下沉的高度，使脚部始终贴地。
    // ═══════════════════════════════════════════════════════════════

    [Header("根节点位移补偿")]
    [Tooltip("启用后只根据腿部折叠量移动人物根节点高度，使下蹲/坐下时上半身下降；是否允许水平移动由下方独立开关控制")]
    public bool rootMotionEnabled = true;

    [Tooltip("左脚骨骼名称（通常为 Calf 的子节点），用于计算脚踝高度")]
    public string leftFootBoneName = "Bip01 L Foot";

    [Tooltip("右脚骨骼名称")]
    public string rightFootBoneName = "Bip01 R Foot";

    [Tooltip("补偿平滑速度，越大响应越快，越小过渡越柔和")]
    public float rootMotionSmoothSpeed = 8f;

    [Tooltip("根节点最大下沉距离（米），防止异常数据导致角色钻入地下")]
    public float rootMotionMaxDrop = 1.5f;

    [Tooltip("启用后人物根节点会产生 X/Z 位移；当前项目保持关闭，确保场景及人物水平位置不变")]
    public bool rootMotionHorizontalEnabled = false;

    [Tooltip("水平补偿平滑速度，越大响应越快，越小过渡越柔和")]
    public float rootMotionHorizontalSmoothSpeed = 6f;

    [Tooltip("根节点最大水平偏移距离（米），过大会与脊柱旋转冲突导致上半身扭曲")]
    public float rootMotionMaxHorizontalOffset = 0.3f;

    // ═══════════════════════════════════════════════════════════════
    //  校验
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 运行时校验配置合法性。
    /// 在 Awake 阶段由 MotionCaptureController 调用，
    /// 若 boneNames 长度与 deviceCount 不匹配则自动修正并输出警告。
    /// </summary>
    public void Validate()
    {
        if (boneNames == null || boneNames.Length != deviceCount)
        {
            Debug.LogWarning($"[MotionCaptureConfig] boneNames 长度({boneNames?.Length}) != deviceCount({deviceCount})，已重置为默认值");
            boneNames = new string[]
            {
                "Bip01 L UpperArm", "Bip01 L Forearm",
                "Bip01 R UpperArm", "Bip01 R Forearm",
                "Bip01 Spine2",
                "Bip01 L Thigh", "Bip01 L Calf",
                "Bip01 R Thigh", "Bip01 R Calf"
            };
        }
    }
}


