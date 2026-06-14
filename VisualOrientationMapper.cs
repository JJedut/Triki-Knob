using System;
using System.Windows.Media.Media3D;
using Triki_Knob;

namespace TrikiReader
{
    public readonly record struct VisualOrientation(double Pitch, double Roll, double Yaw, Matrix3D Transform);

    public interface IVisualOrientationMapper
    {
        VisualOrientation Update(ImuSample sample);
        void ResetForNewStream(
            int minimumStabilizationSamples = 50,
            int stableWindowSamples = 10,
            int maximumStabilizationSamples = 200);
        void Reset();
    }

}
