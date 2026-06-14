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
            SendMediaKey(VolumeDownKey);
        }

        public static void VolumeUp()
        {
            SendMediaKey(VolumeUpKey);
        }

        private static void SendMediaKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, KeyEventExtendedKey, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KeyEventExtendedKey | KeyEventKeyUp, UIntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    }
}