using System.Reflection;
using UnityEngine;

namespace RehabPhotoGame
{
    /// <summary>
    /// S1 坐姿伸膝专用的人物稳定层。
    ///
    /// 目标：
    /// 1. 坐下后让脚回到地面附近，避免整个人悬在半空；
    /// 2. 一旦建立自然下垂的准备姿势，就锁定根节点 Y，
    ///    避免抬腿时 RootMotionSolver 把整个人上下带动；
    /// 3. 右腿准备完成后，按左右膝/脚高度自动做一次右大腿视觉对齐，
    ///    并在本轮右腿训练中固定右大腿的局部姿态，避免前踢时右大腿上浮。
    ///
    /// 该组件只影响最终人物显示，不改四元数原始数据、膝角、训练判定和完成次数。
    /// </summary>
    [DefaultExecutionOrder(1350)]
    internal static class S1SeatedPoseStabilizerBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // 已由 SeatedKneeExtensionRuntimeFix 统一管理双腿姿态和根高度。
            // 保留旧类只为兼容已有资源，但禁止再次自动安装，避免两套逻辑抢写
            // avatarRoot 与右大腿，造成切腿下坠、左右腿不等高和抬腿形变。
        }
    }

    [DefaultExecutionOrder(1400)]
    [DisallowMultipleComponent]
    public sealed class S1SeatedPoseStabilizer : MonoBehaviour
    {
        private const float RightSearchLimitDeg = 28f;
        private const float RightSearchStepDeg = 0.5f;
        private const float RightFootWeight = 0.35f;
        private const float RightAlignSmoothSpeed = 12f;
        private const float MaxFinalGroundCorrectionMeters = 0.35f;

        private static readonly FieldInfo AvatarRootField =
            typeof(MotionCaptureController).GetField(
                "avatarRoot",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo BonesField =
            typeof(MotionCaptureController).GetField(
                "bones",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo RootSolverField =
            typeof(MotionCaptureController).GetField(
                "rootSolver",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private MotionCaptureController motionCapture;
        private SeatedKneeExtensionRuntimeFix training;

        private Transform avatarRoot;
        private Transform leftThigh;
        private Transform leftCalf;
        private Transform rightThigh;
        private Transform rightCalf;
        private Transform leftFoot;
        private Transform rightFoot;
        private RootMotionSolver rootSolver;

        private int observedSessionVersion = int.MinValue;
        private bool rootLocked;
        private float lockedRootY;

        private bool rightThighLocked;
        private Quaternion rightTargetLocalRotation = Quaternion.identity;
        private Quaternion rightAppliedLocalRotation = Quaternion.identity;

        private bool rootMotionRequestedOn;
        private bool warnedMissingRig;

        private void OnEnable()
        {
            // 旧组件即使残留在正在运行的场景对象上，也不能再进入 LateUpdate。
            enabled = false;
        }

        private void Start()
        {
            ResolveReferences();
            RequestRootMotion(true);
        }

        private void LateUpdate()
        {
            if (!ResolveReferences())
                return;

            KneeExtensionTrainingSnapshot snapshot = training.CurrentSnapshot;

            if (snapshot.SessionVersion != observedSessionVersion)
            {
                observedSessionVersion = snapshot.SessionVersion;
                rootLocked = false;
                rightThighLocked = false;
                RequestRootMotion(true);

                motionCapture.LogGameDiagnostic(
                    "s1_pose_session_reset",
                    $"session={observedSessionVersion}, leg={snapshot.Leg}");
            }

            if (snapshot.IsSessionPaused && rootLocked)
            {
                RequestRootMotion(false);
                ApplyLockedRootHeight();
                if (rightThighLocked && snapshot.Leg == TrainingLeg.Right)
                    ApplyRightThighLock();
                return;
            }

            if (snapshot.IsSwitchingLeg || !snapshot.HasReadyReference)
            {
                RequestRootMotion(true);
                return;
            }

            if (!rootLocked)
                CaptureAndLockRootHeight(snapshot);

            ApplyLockedRootHeight();

            if (snapshot.Leg == TrainingLeg.Right &&
                !snapshot.IsSessionPaused)
            {
                if (!rightThighLocked)
                    CaptureRightThighAlignment(snapshot);

                ApplyRightThighLock();
            }
            else
            {
                rightThighLocked = false;
            }
        }

        private void CaptureAndLockRootHeight(
            KneeExtensionTrainingSnapshot snapshot)
        {
            float rootYBefore = avatarRoot.position.y;
            float lowestFootY = LowestFootY();
            float groundY = rootSolver != null && rootSolver.IsInitialized
                ? rootSolver.GroundY
                : lowestFootY;

            float finalCorrection = 0f;
            if (!float.IsNaN(lowestFootY) && !float.IsInfinity(lowestFootY) &&
                !float.IsNaN(groundY) && !float.IsInfinity(groundY))
            {
                finalCorrection = Mathf.Clamp(
                    groundY - lowestFootY,
                    -MaxFinalGroundCorrectionMeters,
                    MaxFinalGroundCorrectionMeters);
            }

            lockedRootY = rootYBefore + finalCorrection;
            rootLocked = true;

            RequestRootMotion(false);
            ApplyLockedRootHeight();

            motionCapture.LogGameDiagnostic(
                "s1_seated_root_locked",
                $"session={snapshot.SessionVersion}, leg={snapshot.Leg}, " +
                $"rootBefore={rootYBefore:F3}m, lockedY={lockedRootY:F3}m, " +
                $"lowestFoot={lowestFootY:F3}m, groundY={groundY:F3}m, " +
                $"finalCorrection={finalCorrection:F3}m");
        }

        private void ApplyLockedRootHeight()
        {
            if (!rootLocked || avatarRoot == null)
                return;

            Vector3 p = avatarRoot.position;
            if (Mathf.Abs(p.y - lockedRootY) > 0.0001f)
            {
                p.y = lockedRootY;
                avatarRoot.position = p;
            }
        }

        private void CaptureRightThighAlignment(
            KneeExtensionTrainingSnapshot snapshot)
        {
            if (rightThigh == null || rightCalf == null ||
                leftCalf == null || avatarRoot == null)
                return;

            Quaternion baseWorld = rightThigh.rotation;
            Vector3 axisWorld = avatarRoot.right.sqrMagnitude > 0.000001f
                ? avatarRoot.right.normalized
                : Vector3.right;

            Vector3 kneeOffsetLocal =
                Quaternion.Inverse(baseWorld) *
                (rightCalf.position - rightThigh.position);

            bool hasFeet = leftFoot != null && rightFoot != null;
            Vector3 footOffsetLocal = Vector3.zero;
            if (hasFeet)
            {
                footOffsetLocal =
                    Quaternion.Inverse(baseWorld) *
                    (rightFoot.position - rightThigh.position);
            }

            float leftKneeY = leftCalf.position.y;
            float leftFootY = hasFeet ? leftFoot.position.y : 0f;
            float kneeGapBefore = rightCalf.position.y - leftKneeY;
            float footGapBefore = hasFeet
                ? rightFoot.position.y - leftFootY
                : 0f;

            float bestAngle = 0f;
            float bestError = float.PositiveInfinity;

            for (float angle = -RightSearchLimitDeg;
                 angle <= RightSearchLimitDeg + 0.001f;
                 angle += RightSearchStepDeg)
            {
                Quaternion candidateWorld =
                    Quaternion.AngleAxis(angle, axisWorld) * baseWorld;

                Vector3 predictedKnee =
                    rightThigh.position + candidateWorld * kneeOffsetLocal;

                float error = Mathf.Abs(predictedKnee.y - leftKneeY);

                if (hasFeet)
                {
                    Vector3 predictedFoot =
                        rightThigh.position + candidateWorld * footOffsetLocal;
                    error += RightFootWeight *
                             Mathf.Abs(predictedFoot.y - leftFootY);
                }

                error += Mathf.Abs(angle) * 0.00002f;

                if (error < bestError)
                {
                    bestError = error;
                    bestAngle = angle;
                }
            }

            Quaternion targetWorld =
                Quaternion.AngleAxis(bestAngle, axisWorld) * baseWorld;

            rightTargetLocalRotation = rightThigh.parent != null
                ? Quaternion.Inverse(rightThigh.parent.rotation) * targetWorld
                : targetWorld;

            rightAppliedLocalRotation = rightThigh.localRotation;
            rightThighLocked = true;

            motionCapture.LogGameDiagnostic(
                "s1_right_thigh_locked",
                $"session={snapshot.SessionVersion}, best={bestAngle:F1}deg, " +
                $"kneeGapBefore={kneeGapBefore:F3}m, " +
                $"footGapBefore={footGapBefore:F3}m");
        }

        private void ApplyRightThighLock()
        {
            if (!rightThighLocked || rightThigh == null)
                return;

            float alpha = 1f - Mathf.Exp(
                -RightAlignSmoothSpeed *
                Mathf.Max(0.001f, Time.unscaledDeltaTime));

            rightAppliedLocalRotation = Quaternion.Slerp(
                rightAppliedLocalRotation,
                rightTargetLocalRotation,
                alpha);

            rightThigh.localRotation = rightAppliedLocalRotation;
        }

        private bool ResolveReferences()
        {
            if (motionCapture == null)
                motionCapture =
                    UnityEngine.Object.FindObjectOfType<MotionCaptureController>();

            if (training == null)
                training = GetComponent<SeatedKneeExtensionRuntimeFix>() ??
                           UnityEngine.Object.FindObjectOfType<SeatedKneeExtensionRuntimeFix>();

            if (motionCapture == null || training == null)
                return false;

            if (avatarRoot == null && AvatarRootField != null)
                avatarRoot =
                    AvatarRootField.GetValue(motionCapture) as Transform;

            if (rootSolver == null && RootSolverField != null)
                rootSolver =
                    RootSolverField.GetValue(motionCapture) as RootMotionSolver;

            if ((leftThigh == null || leftCalf == null ||
                 rightThigh == null || rightCalf == null) &&
                BonesField != null)
            {
                GameObject[] bones =
                    BonesField.GetValue(motionCapture) as GameObject[];

                if (bones != null &&
                    bones.Length > LowerBodyPoseDriver.RightCalfIndex)
                {
                    leftThigh = BoneTransform(
                        bones, LowerBodyPoseDriver.LeftThighIndex);
                    leftCalf = BoneTransform(
                        bones, LowerBodyPoseDriver.LeftCalfIndex);
                    rightThigh = BoneTransform(
                        bones, LowerBodyPoseDriver.RightThighIndex);
                    rightCalf = BoneTransform(
                        bones, LowerBodyPoseDriver.RightCalfIndex);
                }
            }

            if (leftFoot == null && leftCalf != null)
                leftFoot = FindFoot(leftCalf);

            if (rightFoot == null && rightCalf != null)
                rightFoot = FindFoot(rightCalf);

            bool ready = avatarRoot != null &&
                         leftThigh != null && leftCalf != null &&
                         rightThigh != null && rightCalf != null;

            if (!ready && !warnedMissingRig)
            {
                warnedMissingRig = true;
                Debug.LogWarning(
                    "[S1SeatedPoseStabilizer] 未找到完整 S1 人物骨骼，本次不执行坐姿稳定。");
            }

            return ready;
        }

        private void RequestRootMotion(bool enabled)
        {
            if (motionCapture == null)
                return;

            if (rootMotionRequestedOn == enabled)
                return;

            rootMotionRequestedOn = enabled;
            motionCapture.SetRootMotionEnabledForTraining(enabled);
        }

        private float LowestFootY()
        {
            bool hasL = leftFoot != null;
            bool hasR = rightFoot != null;

            if (hasL && hasR)
                return Mathf.Min(leftFoot.position.y, rightFoot.position.y);
            if (hasL)
                return leftFoot.position.y;
            if (hasR)
                return rightFoot.position.y;

            return avatarRoot != null ? avatarRoot.position.y : 0f;
        }

        private static Transform BoneTransform(
            GameObject[] bones, int index)
        {
            if (bones == null || index < 0 || index >= bones.Length)
                return null;

            return bones[index] != null
                ? bones[index].transform
                : null;
        }

        private static Transform FindFoot(Transform calf)
        {
            if (calf == null) return null;

            Transform named = FindDescendantContaining(calf, "Foot");
            if (named != null) return named;

            return calf.childCount > 0
                ? calf.GetChild(0)
                : null;
        }

        private static Transform FindDescendantContaining(
            Transform root, string token)
        {
            foreach (Transform child in root)
            {
                if (child.name.IndexOf(
                        token,
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;

                Transform nested =
                    FindDescendantContaining(child, token);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private void OnDisable()
        {
            if (motionCapture != null)
                motionCapture.SetRootMotionEnabledForTraining(true);

            rootLocked = false;
            rightThighLocked = false;
        }

        private void OnDestroy()
        {
            if (motionCapture != null)
                motionCapture.SetRootMotionEnabledForTraining(true);
        }
    }
}
