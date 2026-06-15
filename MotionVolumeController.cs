using System;
using TrikiReader;

namespace Triki_Knob
{
    public enum VolumeRotationDirection
    {
        Left,
        Right
    }

    public sealed class MotionVolumeController
    {
        private const double BackAccelZThreshold = 0.75;
        private const double BackHorizontalAccelThreshold = 0.45;
        private const double StartPositionPitchThreshold = 18.0;
        private const double StartPositionRollThreshold = 18.0;
        private const double DefaultVolumeDeadZoneDegrees = 5.0;
        private const double MinimumYawSpeedDegreesPerSecond = 18.0;
        private static readonly TimeSpan VolumeStepCooldown = TimeSpan.FromMilliseconds(80);

        private bool _isMutedByBackPose;
        private bool _hasVolumeReferenceYaw;
        private bool _hasLastYaw;
        private double _volumeReferenceYaw;
        private double _lastYaw;
        private DateTimeOffset _lastYawTimestampUtc;
        private DateTimeOffset _lastVolumeStepUtc = DateTimeOffset.MinValue;

        public double SensitivityPercent { get; set; } = 75.0;
        public double DeadZoneDegrees { get; set; } = DefaultVolumeDeadZoneDegrees;
        public int StepCount { get; set; } = 1;
        public VolumeRotationDirection Direction { get; set; } = VolumeRotationDirection.Right;

        public string? Update(ImuSample sample, VisualOrientation orientation)
        {
            if (IsOnBack(sample))
            {
                if (!_isMutedByBackPose)
                {
                    _isMutedByBackPose = true;
                    _hasVolumeReferenceYaw = false;
                    _hasLastYaw = false;
                    SystemVolumeController.Mute();
                    return "Volume: mute";
                }

                return null;
            }

            if (_isMutedByBackPose)
            {
                if (IsStartPosition(orientation))
                {
                    _isMutedByBackPose = false;
                    _hasVolumeReferenceYaw = false;
                    _hasLastYaw = false;
                    SystemVolumeController.Mute();
                    return "Volume: unmute";
                }

                return null;
            }

            return UpdateVolumeFromYaw(sample.TimestampUtc, orientation.Yaw);
        }

        private string? UpdateVolumeFromYaw(DateTimeOffset timestampUtc, double yaw)
        {
            if (!_hasLastYaw)
            {
                _lastYaw = yaw;
                _lastYawTimestampUtc = timestampUtc;
                _hasLastYaw = true;
                _volumeReferenceYaw = yaw;
                _hasVolumeReferenceYaw = true;
                return null;
            }

            var elapsedSeconds = Math.Max((timestampUtc - _lastYawTimestampUtc).TotalSeconds, 0.001);
            var yawSpeed = NormalizeAngle(yaw - _lastYaw) / elapsedSeconds;
            _lastYaw = yaw;
            _lastYawTimestampUtc = timestampUtc;

            if (!_hasVolumeReferenceYaw)
            {
                _volumeReferenceYaw = yaw;
                _hasVolumeReferenceYaw = true;
                return null;
            }

            if (Math.Abs(yawSpeed) < MinimumYawSpeedDegreesPerSecond)
            {
                _volumeReferenceYaw = yaw;
                return null;
            }

            var yawDelta = NormalizeAngle(yaw - _volumeReferenceYaw);
            var sensitivity = Math.Clamp(SensitivityPercent, 1.0, 100.0) / 100.0;
            var deadZoneDegrees = Math.Clamp(DeadZoneDegrees, 1.0, 45.0);
            var requiredStepDegrees = deadZoneDegrees / sensitivity;
            if (Math.Abs(yawDelta) < requiredStepDegrees)
            {
                return null;
            }

            var now = timestampUtc;
            if (now - _lastVolumeStepUtc < VolumeStepCooldown)
            {
                return null;
            }

            _lastVolumeStepUtc = now;
            _volumeReferenceYaw = yaw;
            var volumeUp = Direction == VolumeRotationDirection.Right
                ? yawDelta > 0
                : yawDelta < 0;

            if (volumeUp)
            {
                SystemVolumeController.VolumeUp(StepCount);
                return "Volume: up";
            }

            SystemVolumeController.VolumeDown(StepCount);
            return "Volume: down";
        }

        public void Reset()
        {
            _isMutedByBackPose = false;
            _hasVolumeReferenceYaw = false;
            _hasLastYaw = false;
            _volumeReferenceYaw = 0.0;
            _lastYaw = 0.0;
            _lastYawTimestampUtc = DateTimeOffset.MinValue;
            _lastVolumeStepUtc = DateTimeOffset.MinValue;
        }

        private static bool IsOnBack(ImuSample sample)
        {
            return sample.AccelZ > BackAccelZThreshold &&
                   Math.Abs(sample.AccelX) < BackHorizontalAccelThreshold &&
                   Math.Abs(sample.AccelY) < BackHorizontalAccelThreshold;
        }

        private static bool IsStartPosition(VisualOrientation orientation)
        {
            return Math.Abs(orientation.Pitch) < StartPositionPitchThreshold &&
                   Math.Abs(orientation.Roll) < StartPositionRollThreshold;
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle > 180.0)
            {
                angle -= 360.0;
            }

            while (angle < -180.0)
            {
                angle += 360.0;
            }

            return angle;
        }
    }
}
