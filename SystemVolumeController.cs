using System;
using System.Runtime.InteropServices;

namespace Triki_Knob
{
    public static class SystemVolumeController
    {
        private const byte VolumeMuteKey = 0xAD;
        private const byte VolumeDownKey = 0xAE;
        private const byte VolumeUpKey = 0xAF;
        private const uint KeyEventExtendedKey = 0x0001;
        private const uint KeyEventKeyUp = 0x0002;

        public static void Mute()
        {
            SendMediaKey(VolumeMuteKey);
        }

        public static void VolumeDown()
        {
            VolumeDown(1);
        }

        public static void VolumeUp()
        {
            VolumeUp(1);
        }

        public static void VolumeDown(int steps)
        {
            SendMediaKey(VolumeDownKey, steps);
        }

        public static void VolumeUp(int steps)
        {
            SendMediaKey(VolumeUpKey, steps);
        }

        private static void SendMediaKey(byte virtualKey, int steps = 1)
        {
            for (var i = 0; i < Math.Clamp(steps, 1, 5); i++)
            {
                keybd_event(virtualKey, 0, KeyEventExtendedKey, UIntPtr.Zero);
                keybd_event(virtualKey, 0, KeyEventExtendedKey | KeyEventKeyUp, UIntPtr.Zero);
            }
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    }
}
