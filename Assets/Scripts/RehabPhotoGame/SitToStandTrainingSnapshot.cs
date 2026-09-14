using System;

namespace RehabPhotoGame
{
    public enum PhotoTrainingMode
    {
        SeatedKneeExtension,
        SitToStand,
        ShallowSquat
    }

    public enum SitToStandStage
    {
        Preparing,
        Rising,
        Holding,
        Returning
    }

    /// <summary>坐站动作层向正式摄影界面公开的只读数据。</summary>
    [Serializable]
    public struct SitToStandTrainingSnapshot
    {
        public SitToStandStage Stage;
        public int SessionVersion;
        public bool IsModeActive;
        public bool IsSessionPaused;
        public bool IsDataValid;
        public bool HasSeatedReference;
        public bool SignalPaused;
        public int CompletedRepetitions;
        public int CaptureSequence;
        public float RaiseProgress01;
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
