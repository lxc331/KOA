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
        "Bip01 Spine1",
        // 索引 5: 左大腿传感器 → 3D 模型左大腿骨骼
        "Bip01 L Thigh",
        // 索引 6: 左小腿传感器 → 3D 模型左小腿骨骼
        "Bip01 L Calf",
        // 索引 7: 右大腿传感器 → 3D 模型右大腿骨骼
        "Bip01 R Thigh",
        // 索引 8: 右小腿传感器 → 3D 模型右小腿骨骼
        "Bip01 R Calf"
    };

    [Header("传感器安装角度补偿")]
    [Tooltip("每个传感器的安装角度偏移（欧拉角 XYZ，度）。\n" +
             "当某个传感器的安装朝向和其他传感器不同时（如旋转了 90°），\n" +
             "在此设置补偿角度，使弯肘/弯膝映射到正确的骨骼轴上。\n" +
             "常用值：(0,0,0) 无补偿，(90,0,0) / (0,90,0) / (0,0,90) 旋转 90°")]
    public Vector3[] sensorMountingOffsets = new Vector3[]
    {
        Vector3.zero,               // [0] 左上臂
        new Vector3(0, 0, -90),     // [1] 左前臂：恢复参考包固定安装补偿
        Vector3.zero,               // [2] 右上臂
        Vector3.zero,               // [3] 右前臂
        Vector3.zero,               // [4] 脊柱
        Vector3.zero,               // [5] 左大腿
        Vector3.zero,               // [6] 左小腿
        Vector3.zero,               // [7] 右大腿
        Vector3.zero                // [8] 右小腿
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
        new Vector3(-999, -999, -999),   // [0] 左上臂：肩关节，三轴均 ±60°
        new Vector3(  0, -30, -45),   // [1] 左前臂：肘只能弯不能反弯，所以 X 最小 0°
        new Vector3(-999, -999, -999),   // [2] 右上臂：同左上臂
        new Vector3(  0, -30, -45),   // [3] 右前臂：同左前臂
        new Vector3(-60, -30, -10),   // [4] 脊柱：X 轴前屈 ±60°，YZ 适度限制
        new Vector3(-50, -40, -40),   // [5] 左大腿：髋关节
        new Vector3(-40, -30, -30),   // [6] 左小腿：膝关节
        new Vector3(-50, -40, -40),   // [7] 右大腿：同左大腿
        new Vector3(-40, -30, -30)    // [8] 右小腿：同左小腿
    };

    [Tooltip("每个骨骼各轴允许的最大角度（度），索引顺序与 boneNames 一致")]
    public Vector3[] maxLocalAngles = new Vector3[]
    {
        new Vector3( 999,  999,  999),   // [0] 左上臂
        new Vector3(145,  30,  45),   // [1] 左前臂：肘屈伸最大约 145°
        new Vector3( 999,  999,  999),   // [2] 右上臂
        new Vector3(145,  30,  45),   // [3] 右前臂
        new Vector3( 60,  30,  60),   // [4] 脊柱：X 轴前屈 ±60°
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
    public bool anomalyEnable = false;

    [Tooltip("异常检测的滑动窗口大小：保留最近多少帧用于对比分析")]
    public int anomalyBufferSize = 10;

    [Tooltip("单帧角度跳变阈值（度）：超过此值的帧被视为异常并丢弃")]
    public float anomalyThreshold = 45f;

    // ═══════════════════════════════════════════════════════════════
    //  稳定性检测
    //  开始驱动前，确认传感器数据已稳定（穿戴者保持静止）。
    //  避免在抖动/校准未完成时进入驱动状态导致姿态跳变。
    // ═══════════════════════════════════════════════════════════════

    [Header("稳定性")]
    [Tooltip("稳定进度显示的最大值。V77.30起实际判定改为按持续时间，不再依赖传感器刷新帧数。")]
    public int requiredStableFrames = 20;

    [Tooltip("旧场景兼容字段。V77.30不再按度/帧判定稳定。")]
    public float maxAngularSpeedDeg = 3f;

    [Tooltip("进入稳定区的角速度阈值（度/秒）。低于该值时累计稳定持续时间。")]
    public float stableAngularSpeedDegPerSec = 20f;

    [Tooltip("退出稳定区的角速度阈值（度/秒）。必须高于进入阈值，两者之间为滞回区。")]
    public float unstableAngularSpeedDegPerSec = 45f;

    [Tooltip("连续处于稳定区达到该时长后才判定稳定，与设备刷新率无关。")]
    public float requiredStableDurationSeconds = 0.6f;

    [Tooltip("是否要求所有 9 个传感器都稳定才允许开始（严格模式）")]
    public bool requireAllDevices = false;

    [Tooltip("宽松模式下至少需要多少个设备稳定才允许开始；手臂模式建议至少4个")]
    public int minStableDevices = 4;

    [Tooltip("若某个骨骼在场景中未找到对应 GameObject，是否在稳定性判断中忽略它")]
    public bool ignoreBonesWithoutObject = true;

    // ═══════════════════════════════════════════════════════════════
    //  旋转驱动参数
    // ═══════════════════════════════════════════════════════════════

    [Header("驱动")]
    [Tooltip("唯一骨骼输出层的 Slerp 平滑速度。V77.30 腿部已取消输入层重复低通，建议 25-35。")]
    public float smoothSpeed = 30f;

    [Tooltip("去抖阈值（度）：旋转变化小于此值时不更新目标，静止时略大可减少 IMU 噪声抖动")]
    public float debounceThresholdDeg = 2f;

    [Header("手臂恢复")]
    [Tooltip("休息姿势吸引区角度（度）：骨骼偏离 T-Pose 小于此值时开始向休息姿势吸引")]
    public float armRestAttractionAngle = 8f;

    [Tooltip("吸引力强度（0-1）：越大越快回到精确休息姿势")]
    [Range(0f, 1f)]
    public float armRestAttractionStrength = 0.18f;

    [Tooltip("前臂-上臂耦合强度（0-1）：一方接近休息而另一方落后时，对落后方施加额外拉力")]
    [Range(0f, 1f)]
    public float armCouplingStrength = 0.12f;

    [Tooltip("微稳定判定（度/帧）：单帧相对上一帧偏离休息角的变化小于此值视为静止，跳过磁吸避免与驱动器打架产生抖动")]
    public float armRestMicroStableDeg = 0.45f;

    [Tooltip("主动远离判定（度）：相对上一帧偏离角需增大超过此值才视为在主动运动，避免噪声反复开关磁吸")]
    public float armRestMovingAwayThresholdDeg = 1.15f;

    [Tooltip("主动运动时前臂跟随上臂的耦合强度（0-1）：值越大前臂越紧跟上臂，避免反关节")]
    [Range(0f, 1f)]
    public float armLiftCouplingStrength = 0.6f;

    [Tooltip("触发前臂跟随的上臂领先角度（度）：上臂比前臂多偏离休息姿势超过此值时，前臂会被推动跟上")]
    public float armLiftLeadThresholdDeg = 3f;

    [Tooltip("前臂幅度放大系数（1.0=跟随上臂，>1.0=前臂幅度大于上臂）：解决反关节问题")]
    [Range(1f, 2f)]
    public float armForearmAmplifyFactor = 1.35f;

    [Header("大臂球状约束（Swing-Twist）")]
    [Tooltip("关闭后上臂不受椭圆锥约束，用于排查上举受限问题")]
    public bool swingClampEnabled = true;

    [Tooltip("上臂的椭圆锥活动范围（度）。将大臂关节视为球窝，限制在球体的可达区域内旋转。\n" +
             "四个分量分别对应垂直于骨骼延伸方向的两个正交轴的正/负极限：\n" +
             "  X = +perpA 方向最大摆角\n" +
             "  Y = -perpA 方向最大摆角\n" +
             "  Z = +perpB 方向最大摆角\n" +
             "  W = -perpB 方向最大摆角\n" +
             "初次使用时请逐个缩小单个分量来确认哪个方向对应前/后/上/内")]
    public Vector4[] armSwingLimits = new Vector4[]
    {
        new Vector4(80, 25, 60, 40),  // [0] 左上臂：向前 80°、向后 25°（防反关节）、向上 60°、向内 40°（防穿体）
        new Vector4(80, 25, 60, 40),  // [1] 右上臂：同左
    };

    [Tooltip("球状约束钳制的平滑速度：值越大越硬，越小越柔")]
    [Range(5f, 30f)]
    public float swingClampSmoothSpeed = 12f;

    // ═══════════════════════════════════════════════════════════════
    //  根节点位移补偿
    //  IMU 只能测量旋转，不能测量位移。下蹲时如果不补偿根节点高度，
    //  角色会悬浮在半空中只做腿部弯曲动作。此功能通过读取足部骨骼
    //  的世界坐标反算根节点应下沉的高度，使脚部始终贴地。
    // ═══════════════════════════════════════════════════════════════

    [Header("根节点位移补偿")]
    [Tooltip("启用后下蹲等动作时角色会自动下沉，脚部保持贴地")]
    public bool rootMotionEnabled = true;

    [Tooltip("左脚骨骼名称（通常为 Calf 的子节点），用于计算脚踝高度")]
    public string leftFootBoneName = "Bip01 L Foot";

    [Tooltip("右脚骨骼名称")]
    public string rightFootBoneName = "Bip01 R Foot";

    [Tooltip("补偿平滑速度，越大响应越快，越小过渡越柔和")]
    public float rootMotionSmoothSpeed = 8f;

    [Tooltip("根节点最大下沉距离（米），防止异常数据导致角色钻入地下")]
    public float rootMotionMaxDrop = 1.5f;

    [Tooltip("启用后下蹲等动作时角色根节点会随足部水平偏移，保持重心自然")]
    public bool rootMotionHorizontalEnabled = true;

    [Tooltip("水平补偿平滑速度，越大响应越快，越小过渡越柔和")]
    public float rootMotionHorizontalSmoothSpeed = 6f;

    [Tooltip("根节点最大水平偏移距离（米），过大会与脊柱旋转冲突导致上半身扭曲")]
    public float rootMotionMaxHorizontalOffset = 0.3f;

    [Header("腰部转身")]
    [Tooltip("脊柱 Yaw 死区（度）。\n" +
             "腰部传感器绕 Y 轴旋转低于此值时不触发角色转身，过滤 IMU 噪声。\n" +
             "建议 1~3°，过小会导致静止时角色微转，过大会吞掉真实转身")]
    [Range(0f, 10f)]
    public float spineYawDeadZoneDeg = 1.5f;

    [Tooltip("腰部转身转移到角色根节点的最大角度（度）。\n" +
             "超出此范围的 Yaw 会保留在脊柱骨骼上（表现为躯干扭转）。\n" +
             "人体腰椎正常旋转范围约 ±45°，建议 30~60°")]
    [Range(10f, 120f)]
    public float spineYawMaxDeg = 45f;

    [Header("真实肢体尺寸")]
    [Tooltip("穿戴者躯干高度（脊柱传感器到头顶的近似距离，米）。\n" +
             "用于脊柱倾斜 → 水平位移的投影计算。中国成人平均约 0.45m")]
    public float realTorsoHeight = 0.45f;

    [Tooltip("穿戴者大腿长度（髋关节到膝关节，米）。中国成人平均约 0.45m")]
    public float realThighLength = 0.45f;

    [Tooltip("穿戴者小腿长度（膝关节到踝关节，米）。中国成人平均约 0.40m")]
    public float realCalfLength = 0.40f;

    [Header("前臂降噪")]
    [Tooltip("手臂噪声死区阈值（度）。\n" +
             "驱动器帧间旋转变化 ≤ 此值时视为 IMU 噪声并锁定不动；\n" +
             "> 此值时立即跟随（零延迟、零幅度损失）。\n" +
             "设为 0 则关闭。建议略高于诊断中观测到的 '驱动器帧间Δ'（如 0.8）")]
    [Range(0f, 3f)]
    public float armNoiseDeadZoneDeg = 0.8f;

    [Header("诊断")]
    [Tooltip("开启后在 Console 逐帧输出前臂抖动诊断：驱动器输出的帧间变化 / 后处理引入的偏差")]
    public bool forearmJitterDiag = false;

    [Header("膝盖反关节约束")]
    [Tooltip("启用几何约束防止小腿出现反关节弯曲（膝盖向前突出）。\n" +
             "原理：T-Pose 时记录正常弯曲方向，运行时检测实际弯曲方向是否相反")]
    public bool kneeConstraintEnabled = true;

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
        requiredStableFrames = Mathf.Max(1, requiredStableFrames);
        stableAngularSpeedDegPerSec = Mathf.Max(0f, stableAngularSpeedDegPerSec);
        unstableAngularSpeedDegPerSec = Mathf.Max(
            stableAngularSpeedDegPerSec + 0.1f,
            unstableAngularSpeedDegPerSec);
        requiredStableDurationSeconds = Mathf.Clamp(requiredStableDurationSeconds, 0.1f, 5f);
        smoothSpeed = Mathf.Max(0.01f, smoothSpeed);

        if (boneNames == null || boneNames.Length != deviceCount)
        {
            Debug.LogWarning($"[MotionCaptureConfig] boneNames 长度({boneNames?.Length}) != deviceCount({deviceCount})，已重置为默认值");
            boneNames = new string[]
            {
                "Bip01 L UpperArm", "Bip01 L Forearm",
                "Bip01 R UpperArm", "Bip01 R Forearm",
                "Bip01 Spine1",
                "Bip01 L Thigh", "Bip01 L Calf",
                "Bip01 R Thigh", "Bip01 R Calf"
            };
        }
    }
}

