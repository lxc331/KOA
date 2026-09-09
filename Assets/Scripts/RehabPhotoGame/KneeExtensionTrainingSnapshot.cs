using System;

namespace RehabPhotoGame
{
    /// <summary>
    /// 坐姿伸膝训练层向摄影玩法公开的只读数据契约。
    /// 原始四元数、串口帧和算法中间量仍只进入诊断日志。
    /// </summary>
    [Serializable]
    public struct KneeExtensionTrainingSnapshot
    {
        public TrainingLeg Leg;
        public KneeExtensionStage Stage;
        public int SessionVersion;
        public bool IsSwitchingLeg;
        public bool IsSessionPaused;
        public bool IsDataValid;
        public bool HasReadyReference;
        public int CompletedRepetitions;
        public float KneeAngleDeg;
        public float ReadyReferenceKneeDeg;
        public float TargetKneeDeg;
        public float LiftProgress01;
        public float HoldSeconds;
        public float HoldDurationSeconds;
        public string BlockReason;
    }
}
