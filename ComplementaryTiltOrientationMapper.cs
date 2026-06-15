using System;
using System.Windows.Media.Media3D;
using TrikiReader;

namespace Triki_Knob
{
    public sealed class ComplementaryTiltOrientationMapper : IVisualOrientationMapper
    {
        private const double AutoZeroGyroStillThreshold = 2.0;
        private const double AutoZeroMinimumAccelMagnitude = 0.85;
        private const double AutoZeroMaximumAccelMagnitude = 1.15;
        private const double YawStillDeadbandDegreesPerSecond = 1.0;
        private const double GyroZBiasSmoothing = 0.02;

        private readonly double _gyroGain;
        private readonly double _fallbackDeltaSeconds;
        private readonly double _minimumDeltaSeconds;
        private readonly double _complementaryAlpha;
        private readonly double _smoothingFactor;
        private readonly double _visualDeadbandDegrees;

        private DateTimeOffset? _lastTimestamp;
        private double _pitch;
        private double _roll;
        private double _yaw;
        private double _gyroZBias;
        private double _pitchOffset;
        private double _rollOffset;
        private double _yawOffset;
        private double _displayPitch;
        private double _displayRoll;
        private double _displayYaw;
        private bool _isFirstDisplaySample = true;
        private bool _isAutoZeroPending;
        private int _autoZeroSampleCount;
        private int _autoZeroStableSampleCount;
        private int _autoZeroMinimumSamples;
        private int _autoZeroRequiredStableSamples;
        private int _autoZeroMaximumSamples;

        public ComplementaryTiltOrientationMapper(
            double gyroGain = 1.0,
            double fallbackDeltaSeconds = 0.02,
            double minimumDeltaSeconds = 0.001,
            double complementaryAlpha = 0.96,
            double smoothingFactor = 0.35,
            double visualDeadbandDegrees = 4.0)
        {
            if (gyroGain <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(gyroGain), "Gyro gain must be greater than zero.");
            if (fallbackDeltaSeconds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(fallbackDeltaSeconds), "Fallback sample period must be greater than zero.");
            if (minimumDeltaSeconds < 0.0)
                throw new ArgumentOutOfRangeException(nameof(minimumDeltaSeconds), "Minimum sample period cannot be negative.");
            if (complementaryAlpha < 0.0 || complementaryAlpha > 1.0)
                throw new ArgumentOutOfRangeException(nameof(complementaryAlpha), "Complementary alpha must be between 0 and 1.");
            if (smoothingFactor <= 0.0 || smoothingFactor > 1.0)
                throw new ArgumentOutOfRangeException(nameof(smoothingFactor), "Smoothing factor must be between (0, 1].");
            if (visualDeadbandDegrees < 0.0)
                throw new ArgumentOutOfRangeException(nameof(visualDeadbandDegrees), "Visual deadband cannot be negative.");

            _gyroGain = gyroGain;
            _fallbackDeltaSeconds = fallbackDeltaSeconds;
            _minimumDeltaSeconds = minimumDeltaSeconds;
            _complementaryAlpha = complementaryAlpha;
            _smoothingFactor = smoothingFactor;
            _visualDeadbandDegrees = visualDeadbandDegrees;
        }

        public VisualOrientation Update(ImuSample sample)
        {
            var dt = CalculateDeltaSeconds(sample.TimestampUtc);
            UpdateAngles(sample, dt);

            if (_isAutoZeroPending)
            {
                return UpdateAutoZeroCalibration(sample);
            }

            var targetPitch = ApplyDeadband(NormalizeAngle(_pitch - _pitchOffset));
            var targetRoll = ApplyDeadband(NormalizeAngle(_roll - _rollOffset));
            var targetYaw = ApplyDeadband(NormalizeAngle(_yaw - _yawOffset));

            if (_isFirstDisplaySample)
            {
                _displayPitch = targetPitch;
                _displayRoll = targetRoll;
                _displayYaw = targetYaw;
                _isFirstDisplaySample = false;
            }
            else
            {
                _displayPitch += NormalizeAngle(targetPitch - _displayPitch) * _smoothingFactor;
                _displayRoll += NormalizeAngle(targetRoll - _displayRoll) * _smoothingFactor;
                _displayYaw += NormalizeAngle(targetYaw - _displayYaw) * _smoothingFactor;
            }

            return ToOrientation(_displayPitch, _displayRoll, _displayYaw);
        }

        public void ResetForNewStream(
            int minimumStabilizationSamples = 50,
            int stableWindowSamples = 10,
            int maximumStabilizationSamples = 200)
        {
            if (minimumStabilizationSamples < 0)
                throw new ArgumentOutOfRangeException(nameof(minimumStabilizationSamples), "Minimum stabilization sample count cannot be negative.");
            if (stableWindowSamples <= 0)
                throw new ArgumentOutOfRangeException(nameof(stableWindowSamples), "Stable window sample count must be greater than zero.");
            if (maximumStabilizationSamples < minimumStabilizationSamples)
                throw new ArgumentOutOfRangeException(nameof(maximumStabilizationSamples), "Maximum stabilization sample count cannot be less than the minimum.");

            _lastTimestamp = null;
            _pitch = 0.0;
            _roll = 0.0;
            _yaw = 0.0;
            _gyroZBias = 0.0;
            _pitchOffset = 0.0;
            _rollOffset = 0.0;
            _yawOffset = 0.0;
            _displayPitch = 0.0;
            _displayRoll = 0.0;
            _displayYaw = 0.0;
            _isFirstDisplaySample = true;
            _isAutoZeroPending = true;
            _autoZeroSampleCount = 0;
            _autoZeroStableSampleCount = 0;
            _autoZeroMinimumSamples = minimumStabilizationSamples;
            _autoZeroRequiredStableSamples = stableWindowSamples;
            _autoZeroMaximumSamples = maximumStabilizationSamples;
        }

        public void Reset()
        {
            _pitchOffset = _pitch;
            _rollOffset = _roll;
            _yawOffset = _yaw;
            _displayPitch = 0.0;
            _displayRoll = 0.0;
            _displayYaw = 0.0;
            _isFirstDisplaySample = true;
            _isAutoZeroPending = false;
            _autoZeroSampleCount = 0;
            _autoZeroStableSampleCount = 0;
        }

        private double CalculateDeltaSeconds(DateTimeOffset timestampUtc)
        {
            var dt = _fallbackDeltaSeconds;
            if (_lastTimestamp is not null)
            {
                dt = (timestampUtc - _lastTimestamp.Value).TotalSeconds;
                if (dt <= _minimumDeltaSeconds)
                {
                    dt = _fallbackDeltaSeconds;
                }
            }

            _lastTimestamp = timestampUtc;
            return dt;
        }

        private void UpdateAngles(ImuSample sample, double dt)
        {
            var isStill = IsStillForAutoZero(sample);
            if (isStill)
            {
                _gyroZBias += (sample.GyroZ - _gyroZBias) * GyroZBiasSmoothing;
            }

            var correctedGyroZ = sample.GyroZ - _gyroZBias;
            if (isStill && Math.Abs(correctedGyroZ) < YawStillDeadbandDegreesPerSecond)
            {
                correctedGyroZ = 0.0;
            }

            var gyroPitch = _pitch + sample.GyroY * _gyroGain * dt;
            var gyroRoll = _roll - sample.GyroX * _gyroGain * dt;
            _yaw = NormalizeAngle(_yaw - correctedGyroZ * _gyroGain * dt);

            if (TryGetAccelerometerTilt(sample, out var accelPitch, out var accelRoll))
            {
                _pitch = _complementaryAlpha * gyroPitch + (1.0 - _complementaryAlpha) * accelPitch;
                _roll = _complementaryAlpha * gyroRoll + (1.0 - _complementaryAlpha) * accelRoll;
            }
            else
            {
                _pitch = gyroPitch;
                _roll = gyroRoll;
            }

            _pitch = NormalizeAngle(_pitch);
            _roll = NormalizeAngle(_roll);
        }

        private static bool TryGetAccelerometerTilt(ImuSample sample, out double pitch, out double roll)
        {
            var magnitude = Math.Sqrt(
                sample.AccelX * sample.AccelX +
                sample.AccelY * sample.AccelY +
                sample.AccelZ * sample.AccelZ);

            if (magnitude < 0.65 || magnitude > 1.35)
            {
                pitch = 0.0;
                roll = 0.0;
                return false;
            }

            var horizontal = Math.Sqrt(sample.AccelY * sample.AccelY + sample.AccelZ * sample.AccelZ);
            pitch = Math.Atan2(sample.AccelX, horizontal) * 180.0 / Math.PI;
            roll = Math.Atan2(sample.AccelY, -sample.AccelZ) * 180.0 / Math.PI;
            return true;
        }

        private VisualOrientation UpdateAutoZeroCalibration(ImuSample sample)
        {
            _autoZeroSampleCount++;

            if (_autoZeroSampleCount > _autoZeroMinimumSamples)
            {
                _autoZeroStableSampleCount = IsStillForAutoZero(sample)
                    ? _autoZeroStableSampleCount + 1
                    : 0;
            }

            if (_autoZeroStableSampleCount >= _autoZeroRequiredStableSamples ||
                _autoZeroSampleCount >= _autoZeroMaximumSamples)
            {
                _pitchOffset = _pitch;
                _rollOffset = _roll;
                _yawOffset = _yaw;
                _displayPitch = 0.0;
                _displayRoll = 0.0;
                _displayYaw = 0.0;
                _isFirstDisplaySample = true;
                _isAutoZeroPending = false;
            }

            return ToOrientation(0.0, 0.0, 0.0);
        }

        private static bool IsStillForAutoZero(ImuSample sample)
        {
            var gyroMagnitude = Math.Sqrt(
                sample.GyroX * sample.GyroX +
                sample.GyroY * sample.GyroY +
                sample.GyroZ * sample.GyroZ);
            if (gyroMagnitude > AutoZeroGyroStillThreshold)
            {
                return false;
            }

            var accelMagnitude = Math.Sqrt(
                sample.AccelX * sample.AccelX +
                sample.AccelY * sample.AccelY +
                sample.AccelZ * sample.AccelZ);
            return accelMagnitude >= AutoZeroMinimumAccelMagnitude &&
                   accelMagnitude <= AutoZeroMaximumAccelMagnitude;
        }

        private double ApplyDeadband(double angle)
        {
            if (_visualDeadbandDegrees <= 0.0)
            {
                return angle;
            }

            var absoluteAngle = Math.Abs(angle);
            if (absoluteAngle <= _visualDeadbandDegrees)
            {
                return 0.0;
            }

            return Math.Sign(angle) * (absoluteAngle - _visualDeadbandDegrees);
        }

        private static VisualOrientation ToOrientation(double pitch, double roll, double yaw)
        {
            var matrix = Matrix3D.Identity;
            matrix.Rotate(new Quaternion(new Vector3D(0, 0, 1), yaw));
            matrix.Rotate(new Quaternion(new Vector3D(0, 1, 0), pitch));
            matrix.Rotate(new Quaternion(new Vector3D(1, 0, 0), roll));

            return new VisualOrientation(pitch, roll, yaw, matrix);
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
