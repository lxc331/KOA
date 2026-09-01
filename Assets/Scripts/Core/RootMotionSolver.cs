
using UnityEngine;

/// <summary>
/// 根节点位移补偿器（垂直 + 水平）。
/// 
/// 问题背景：
///   IMU 传感器只能测量旋转（姿态），不能测量位移（位置）。
///   当穿戴者做下蹲动作时，大腿和小腿的旋转被正确捕获，
///   但骨盆的垂直下降及水平位移无法被 IMU 感知。结果：3D 角色的腿部
///   正确弯曲，但整个身体悬浮在原始高度和位置，脚部离开地面且重心偏移。
/// 
/// 解决原理（正向运动学反算）：
///   垂直补偿：
///     1. T-Pose 初始化时记录脚踝的世界 Y 坐标作为"地面参考线" (groundY)
///     2. 每帧在应用骨骼旋转之后，读取脚踝的新世界 Y 坐标
///     3. 计算偏移 = groundY - 最低脚踝Y
///        - 下蹲时脚踝因骨骼链折叠而上升 → 偏移为负 → 根节点下沉
///        - 站立时脚踝回到地面参考 → 偏移为零 → 根节点归位
///     4. 将偏移平滑地应用到角色根节点的 Y 坐标
/// 
///   水平补偿：
///     1. T-Pose 初始化时记录双脚中点的世界 XZ 坐标作为"水平参考点" (groundXZ)
///     2. 每帧在应用骨骼旋转之后，读取双脚中点的新世界 XZ 坐标
///     3. 计算水平偏移 = groundXZ - 当前双脚中点XZ
///        - 下蹲时脚部因骨骼链折叠而前移 → 根节点后移 → 保持脚部在参考位置
///     4. 将偏移平滑地应用到角色根节点的 X/Z 坐标
/// 
/// 关键设计 — 三步调用顺序：
///   Step 1: ResetRootPosition()  — 先把根节点复位到原始位置，消除上一帧偏移
///   Step 2: (外部) ApplyToBones  — 应用传感器旋转到骨骼
///   Step 3: SolveAndApply()      — 读取脚踝坐标，计算新偏移，移动根节点
///   
///   为什么不能省略 Step 1？
///   如果不复位，Step 3 读取的脚踝坐标是"上一帧偏移+本帧旋转"的叠加，
///   会导致偏移量逐帧累积漂移。先复位再计算，每帧结果独立且一致。
/// 
/// 数值示例（单位：米）：
///   T-Pose:  rootY=0, ankleY=0.05(地面参考), groundY=0.05
///   下蹲帧:  复位 rootY→0 → 应用旋转 → ankleY=0.55(腿折叠后脚踝上升)
///            offset = 0.05 - 0.55 = -0.50
///            rootY = 0 + (-0.50) = -0.50
///            ankleY(最终) = 0.55 + (-0.50) = 0.05 ✓ 脚踝贴地
///   水平同理：footXZ 前移 0.3m → root 后移 0.3m → 脚踝回到参考点 ✓
/// </summary>
public class RootMotionSolver
{
    // ═══════════════════════════════════════════════════════════════
    //  公开属性
    // ═══════════════════════════════════════════════════════════════

    /// <summary>总开关：是否启用根节点补偿</summary>
    public bool Enabled { get; set; }

    /// <summary>诊断日志：根节点补偿器是否已成功找到根节点和足部骨骼。</summary>
    public bool IsInitialized => initialized;

    /// <summary>诊断日志：当前实际应用的垂直偏移（米）。</summary>
    public float CurrentVerticalOffset => currentOffset;

    /// <summary>诊断日志：当前实际应用的水平 X/Z 偏移（米）。</summary>
    public Vector2 CurrentHorizontalOffset => currentHorizontalOffset;

    /// <summary>诊断日志：初始化时记录的脚部地面高度。</summary>
    public float GroundY => groundY;

    // ═══════════════════════════════════════════════════════════════
    //  内部字段
    // ═══════════════════════════════════════════════════════════════

    /// <summary>角色根节点 Transform（移动它会带动整个角色上下移动）</summary>
    private Transform avatarRoot;

    /// <summary>左脚骨骼 Transform（Bip01 L Foot，用于读取脚踝世界坐标）</summary>
    private Transform leftFoot;

    /// <summary>右脚骨骼 Transform（Bip01 R Foot）</summary>
    private Transform rightFoot;

    // ── T-Pose 时的参考值 ──
    /// <summary>T-Pose 时角色根节点的世界坐标（复位基准）</summary>
    private Vector3 originalRootPos;

    /// <summary>T-Pose 时最低脚踝的 Y 坐标（地面参考线）</summary>
    private float groundY;

    /// <summary>T-Pose 时双脚中点的世界 XZ 坐标（水平参考点）</summary>
    private Vector2 groundXZ;

    // ── 垂直平滑 ──
    /// <summary>垂直平滑速度因子（越大越灵敏，越小越平滑）</summary>
    private float smoothSpeed;

    /// <summary>当前帧实际应用的平滑后垂直偏移量</summary>
    private float currentOffset;

    // ── 水平补偿 ──
    /// <summary>水平补偿总开关</summary>
    private bool horizontalEnabled;

    /// <summary>水平平滑速度因子</summary>
    private float horizontalSmoothSpeed;

    /// <summary>当前帧实际应用的平滑后水平偏移量</summary>
    private Vector2 currentHorizontalOffset;

    /// <summary>允许的最大水平偏移距离（防止异常数据导致角色大幅漂移）</summary>
    private float maxHorizontalOffset;

    // ── 安全限制 ──
    /// <summary>允许的最大下沉距离（防止异常数据把角色送入地下）</summary>
    private float maxDropDistance;

    /// <summary>初始化是否成功（足部骨骼是否找到）</summary>
    private bool initialized;

    // ═══════════════════════════════════════════════════════════════
    //  初始化
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 初始化补偿器。在 T-Pose 状态下调用（Start 阶段），
    /// 记录根节点位置、脚踝地面参考线和水平参考点。
    /// </summary>
    /// <param name="root">角色根节点 Transform</param>
    /// <param name="lFoot">左脚骨骼 Transform（可为 null）</param>
    /// <param name="rFoot">右脚骨骼 Transform（可为 null）</param>
    /// <param name="smoothSpd">垂直平滑速度因子（建议 5-12）</param>
    /// <param name="maxDrop">最大下沉距离（米，建议 1.0-2.0）</param>
    /// <param name="hEnabled">是否启用水平位移补偿</param>
    /// <param name="hSmoothSpd">水平平滑速度因子（建议 4-10）</param>
    /// <param name="maxHOffset">最大水平偏移距离（米，建议 0.5-1.5）</param>
    public void Initialize(Transform root, Transform lFoot, Transform rFoot,
                           float smoothSpd = 8f, float maxDrop = 1.5f,
                           bool hEnabled = true, float hSmoothSpd = 6f, float maxHOffset = 1.0f)
    {
        avatarRoot = root;
        leftFoot = lFoot;
        rightFoot = rFoot;
        smoothSpeed = smoothSpd;
        maxDropDistance = maxDrop;
        horizontalEnabled = hEnabled;
        horizontalSmoothSpeed = hSmoothSpd;
        maxHorizontalOffset = maxHOffset;

        if (root == null)
        {
            Debug.LogWarning("[RootMotionSolver] avatarRoot 为 null，已禁用根节点补偿");
            initialized = false;
            return;
        }

        if (lFoot == null && rFoot == null)
        {
            Debug.LogWarning("[RootMotionSolver] 未找到任何足部骨骼，已禁用根节点补偿");
            initialized = false;
            return;
        }

        originalRootPos = root.position;
        groundY = ComputeLowestFootY();
        groundXZ = ComputeFootMidpointXZ();
        currentOffset = 0f;
        currentHorizontalOffset = Vector2.zero;
        initialized = true;

        Debug.Log($"[RootMotionSolver] 初始化完成 — groundY={groundY:F3}, groundXZ=({groundXZ.x:F3}, {groundXZ.y:F3}), rootPos={originalRootPos}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Step 1：复位根节点
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 在 ApplyToBones 之前调用。
    /// 将根节点复位到 T-Pose 时的原始位置，确保本帧旋转计算
    /// 不受上一帧偏移的干扰。
    /// </summary>
    public void ResetRootPosition()
    {
        if (!initialized || !Enabled || avatarRoot == null) return;
        avatarRoot.position = originalRootPos;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Step 3：计算偏移并应用
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 在 ApplyToBones 之后调用。
    /// 读取当前脚踝世界坐标，计算根节点需要的垂直和水平偏移，
    /// 经平滑处理后应用到根节点。
    /// </summary>
    public void SolveAndApply()
    {
        if (!initialized || !Enabled || avatarRoot == null) return;

        // ── 垂直补偿 ──
        float lowestFootY = ComputeLowestFootY();
        float targetOffset = groundY - lowestFootY;
        targetOffset = Mathf.Clamp(targetOffset, -maxDropDistance, 0f);

        float alpha = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);
        currentOffset = Mathf.Lerp(currentOffset, targetOffset, alpha);

        if (Mathf.Abs(currentOffset) < 0.0005f && Mathf.Abs(targetOffset) < 0.0005f)
            currentOffset = 0f;

        // ── 水平补偿 ──
        // 使用与垂直相同的 alpha，保证根节点三轴位移同步
        Vector2 targetHorizontalOffset = Vector2.zero;
        if (horizontalEnabled)
        {
            Vector2 currentFootXZ = ComputeFootMidpointXZ();
            targetHorizontalOffset = groundXZ - currentFootXZ;

            float hMag = targetHorizontalOffset.magnitude;
            if (hMag > maxHorizontalOffset)
                targetHorizontalOffset = targetHorizontalOffset.normalized * maxHorizontalOffset;

            currentHorizontalOffset = Vector2.Lerp(currentHorizontalOffset, targetHorizontalOffset, alpha);

            if (currentHorizontalOffset.magnitude < 0.0005f && targetHorizontalOffset.magnitude < 0.0005f)
                currentHorizontalOffset = Vector2.zero;
        }

        // ── 合成并应用三轴偏移 ──
        Vector3 pos = originalRootPos;
        pos.x += currentHorizontalOffset.x;
        pos.y += currentOffset;
        pos.z += currentHorizontalOffset.y;
        avatarRoot.position = pos;
    }

    // ═══════════════════════════════════════════════════════════════
    //  重置
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 全量重置：清空平滑状态，将根节点恢复到初始位置。
    /// 在用户点击"重置"时由 Controller 调用。
    /// </summary>
    public void Reset()
    {
        currentOffset = 0f;
        currentHorizontalOffset = Vector2.zero;
        if (avatarRoot != null)
            avatarRoot.position = originalRootPos;
    }

    // ═══════════════════════════════════════════════════════════════
    //  参数调整
    // ═══════════════════════════════════════════════════════════════

    /// <summary>运行时修改垂直平滑速度</summary>
    public void SetSmoothSpeed(float speed) => smoothSpeed = speed;

    /// <summary>运行时修改最大下沉距离</summary>
    public void SetMaxDrop(float dist) => maxDropDistance = dist;

    /// <summary>运行时切换水平补偿开关</summary>
    public void SetHorizontalEnabled(bool enabled) => horizontalEnabled = enabled;

    /// <summary>运行时修改水平平滑速度</summary>
    public void SetHorizontalSmoothSpeed(float speed) => horizontalSmoothSpeed = speed;

    /// <summary>运行时修改最大水平偏移距离</summary>
    public void SetMaxHorizontalOffset(float dist) => maxHorizontalOffset = dist;

    // ═══════════════════════════════════════════════════════════════
    //  内部工具
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 计算双脚中点的世界 XZ 坐标（返回 Vector2(x, z)）。
    /// 取中点的原因：水平补偿需要跟踪双脚整体的平均位置，
    /// 而非单脚，这样弓步等单侧动作时两脚的水平偏移会相互抵消，
    /// 根节点只在双脚一起偏移时才做大幅补偿。
    /// </summary>
    private Vector2 ComputeFootMidpointXZ()
    {
        bool hasL = leftFoot != null;
        bool hasR = rightFoot != null;

        if (hasL && hasR)
        {
            Vector3 mid = (leftFoot.position + rightFoot.position) * 0.5f;
            return new Vector2(mid.x, mid.z);
        }
        if (hasL) return new Vector2(leftFoot.position.x, leftFoot.position.z);
        if (hasR) return new Vector2(rightFoot.position.x, rightFoot.position.z);
        return groundXZ;
    }

    /// <summary>
    /// 获取两只脚中较低的 Y 世界坐标。
    /// 取最低值的原因：较低的脚应贴地（如下蹲、弓步），
    /// 以它为基准计算偏移，可确保至少一只脚保持在地面。
    /// 若某只脚的骨骼缺失则仅用另一只。
    /// </summary>
    private float ComputeLowestFootY()
    {
        bool hasL = leftFoot != null;
        bool hasR = rightFoot != null;

        if (hasL && hasR)
            return Mathf.Min(leftFoot.position.y, rightFoot.position.y);
        if (hasL)
            return leftFoot.position.y;
        if (hasR)
            return rightFoot.position.y;

        // 两只脚都没有（理论上不应该走到这里，Initialize 已做过检查）
        // 返回地面参考，不产生偏移
        return groundY;
    }
}



