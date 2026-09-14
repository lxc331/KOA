using System;

namespace RehabPhotoGame
{
    public enum ShallowSquatStage
    {
        Preparing,
        Lowering,
        Holding,
        Returning,
        TooDeep
    }

    /// <summary>浅蹲动作层向正式摄影界面公开的只读数据。</summary>
    [Serializable]
    public struct ShallowSquatTrainingSnapshot
    {
        public ShallowSquatStage Stage;
        public int SessionVersion;
        public bool IsModeActive;
        public bool IsSessionPaused;
        public bool IsDataValid;
        public bool HasStandingReference;
        public bool SignalPaused;
        public bool CoordinationWarning;
        public float LeftKneeFlexionDeg;
        public float RightKneeFlexionDeg;
        public int CompletedRepetitions;
        public int CaptureSequence;
        public float DropProgress01;
        public float LeftProgress01;
        public float RightProgress01;
        public float HoldSeconds;
        public float HoldDurationSeconds;
        public float LeftThighDeg;
        public float LeftKneeDeg;
        public float RightThighDeg;
        public float RightKneeDeg;
        public string BlockReason;
    }
}
