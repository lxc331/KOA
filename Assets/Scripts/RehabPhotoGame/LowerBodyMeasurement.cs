namespace RehabPhotoGame
{
    /// <summary>角度约定：膝伸直为 0°，坐姿通常约 90°；来自标定后四元数。</summary>
    public struct LowerBodyMeasurement
    {
        public bool IsValid;
        public string FailureReason;
        public int FreshMask;
        public int CalibrationVersion;
        public float SampleTimeSeconds;
        public float LeftThighDeg, RightThighDeg;
        public float LeftKneeDeg, RightKneeDeg;
    }
}
