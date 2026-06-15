using System;
using System.Runtime.InteropServices;

namespace Triki_Knob
{
    public static class SystemScrollController
    {
        private const uint MouseEventWheel = 0x0800;
        private const int WheelDelta = 120;

        public static void ScrollVertical(int steps)
        {
            if (steps == 0)
            {
                return;
            }

            mouse_event(MouseEventWheel, 0, 0, steps * WheelDelta, UIntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern void mouse_event(
            uint dwFlags,
            uint dx,
            uint dy,
            int dwData,
            UIntPtr dwExtraInfo);
    }
}
