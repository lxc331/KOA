using System;

namespace RehabPhotoGame
{
    public enum S1TrainingRunState
    {
        Idle,
        Running,
        Paused,
        Completed,
        Ended
    }

    /// <summary>第2段正式界面的会话状态，不参与膝角或四元数计算。</summary>
    public sealed class S1TrainingSession
    {
        public const int MinimumTarget = 1;
        public const int MaximumTarget = 20;

        public S1TrainingRunState State { get; private set; } = S1TrainingRunState.Idle;
        public int TargetRepetitions { get; private set; } = 3;
        public int CompletedRepetitions { get; private set; }

        public void SetTarget(int target)
        {
            if (State != S1TrainingRunState.Idle) return;
            TargetRepetitions = Math.Max(MinimumTarget,
                Math.Min(MaximumTarget, target));
        }

        public void Start()
        {
            CompletedRepetitions = 0;
            State = S1TrainingRunState.Running;
        }

        public bool Pause()
        {
            if (State != S1TrainingRunState.Running) return false;
            State = S1TrainingRunState.Paused;
            return true;
        }

        public bool Resume()
        {
            if (State != S1TrainingRunState.Paused) return false;
            State = S1TrainingRunState.Running;
            return true;
        }

        /// <returns>本次记录后是否刚好完成训练目标。</returns>
        public bool RecordCapture()
        {
            if (State != S1TrainingRunState.Running) return false;
            CompletedRepetitions++;
            if (CompletedRepetitions < TargetRepetitions) return false;
            CompletedRepetitions = TargetRepetitions;
            State = S1TrainingRunState.Completed;
            return true;
        }

        public bool End()
        {
            if (State == S1TrainingRunState.Idle ||
                State == S1TrainingRunState.Ended)
                return false;
            State = S1TrainingRunState.Ended;
            return true;
        }

        public void ReturnToSetup()
        {
            CompletedRepetitions = 0;
            State = S1TrainingRunState.Idle;
        }
    }
}
