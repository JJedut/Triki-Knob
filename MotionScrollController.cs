using System;
using TrikiReader;

namespace Triki_Knob
{
    public enum ScrollDirection
    {
        Normal,
        Inverted
    }

    public sealed class MotionScrollController
    {
        private const double DefaultScrollDeadZoneDegrees = 6.0;
        private const double MaxScrollPitchDegrees = 45.0;
        private const double MinScrollStepsPerSecond = 2.0;
        private const double MaxScrollStepsPerSecond = 18.0;

        private DateTimeOffset? _lastUpdateUtc;
        private double _scrollAccumulator;

        public double SensitivityPercent { get; set; } = 75.0;
        public double DeadZoneDegrees { get; set; } = DefaultScrollDeadZoneDegrees;
        public double SpeedPercent { get; set; } = 100.0;
        public ScrollDirection Direction { get; set; } = ScrollDirection.Normal;

        public string? Update(DateTimeOffset timestampUtc, VisualOrientation orientation)
        {
            var elapsedSeconds = CalculateElapsedSeconds(timestampUtc);
            var sensitivity = Math.Clamp(SensitivityPercent, 1.0, 100.0) / 100.0;
            var configuredDeadZoneDegrees = Math.Clamp(DeadZoneDegrees, 1.0, 45.0);
            var deadZoneDegrees = configuredDeadZoneDegrees / sensitivity;
            var pitchMagnitude = Math.Abs(orientation.Pitch);
            if (pitchMagnitude < deadZoneDegrees)
            {
                _scrollAccumulator = 0.0;
                return null;
            }

            var scrollDown = Direction == ScrollDirection.Normal
                ? orientation.Pitch > 0
                : orientation.Pitch < 0;
            var normalizedTilt = Math.Clamp(
                (pitchMagnitude - deadZoneDegrees) / (MaxScrollPitchDegrees - deadZoneDegrees),
                0.0,
                1.0);
            var stepsPerSecond = MinScrollStepsPerSecond +
                                 normalizedTilt * (MaxScrollStepsPerSecond - MinScrollStepsPerSecond);
            stepsPerSecond *= Math.Clamp(SpeedPercent, 25.0, 200.0) / 100.0;

            _scrollAccumulator += stepsPerSecond * elapsedSeconds;
            var steps = (int)Math.Floor(_scrollAccumulator);
            if (steps == 0)
            {
                return null;
            }

            _scrollAccumulator -= steps;

            SystemScrollController.ScrollVertical(scrollDown ? -steps : steps);
            return scrollDown ? "Scroll: down" : "Scroll: up";
        }

        public void Reset()
        {
            _lastUpdateUtc = null;
            _scrollAccumulator = 0.0;
        }

        private double CalculateElapsedSeconds(DateTimeOffset timestampUtc)
        {
            if (_lastUpdateUtc is null)
            {
                _lastUpdateUtc = timestampUtc;
                return 0.0;
            }

            var elapsedSeconds = Math.Max(
                0.0,
                (timestampUtc - _lastUpdateUtc.Value).TotalSeconds);
            _lastUpdateUtc = timestampUtc;
            return Math.Min(elapsedSeconds, 0.1);
        }
    }
}
