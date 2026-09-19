using System.Runtime.InteropServices;

namespace Microkernel.Abstractions.Input
{
    /// <summary>
    /// Zero-allocation representation of a decoded keyboard event.
    ///
    /// For USB HID boot-protocol reports, <see cref="UsageId"/> carries the HID
    /// Usage ID (NOT a PS/2 Set-1 scancode) and <see cref="Modifiers"/> mirrors
    /// report byte 0. <see cref="Ascii"/> is the translated character for the
    /// current modifier state, or 0 for keys with no direct ASCII mapping.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RawKeyEvent
    {
        /// <summary>HID modifier bitmask from report byte 0 (Ctrl/Alt/Gui/Shift bits).</summary>
        public byte Modifiers;

        /// <summary>HID Usage ID on the Keyboard/Keypad usage page (0x07).</summary>
        public byte UsageId;

        /// <summary>1 when the key transitioned to pressed, 0 on release.</summary>
        public byte Pressed;

        /// <summary>Translated ASCII/control character, or 0 if unmapped.</summary>
        public uint Ascii;
    }
}
