using System;

namespace RecompOne.Runtime.Hle;

public static class GlesColorHelper
{
    public static ushort Convert1555To5551(ushort px)
    {
        return (ushort)(
            ((px & 0x001F) << 11) | // Red: 0..4 -> 11..15
            ((px & 0x03E0) << 1)  | // Green: 5..9 -> 6..10
            ((px & 0x7C00) >> 9)  | // Blue: 10..14 -> 1..5
            ((px & 0x8000) != 0 ? 1 : 0) // Alpha: 15 -> 0
        );
    }

    public static ushort Convert5551To1555(ushort px)
    {
        return (ushort)(
            ((px & 0xF800) >> 11) | // Red: 11..15 -> 0..4
            ((px & 0x07C0) >> 1)  | // Green: 6..10 -> 5..9
            ((px & 0x003E) << 9)  | // Blue: 1..5 -> 10..14
            ((px & 0x0001) != 0 ? 0x8000 : 0) // Alpha: 0 -> 15
        );
    }

    public static void Convert1555To5551(ReadOnlySpan<ushort> src, Span<ushort> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            ushort px = src[i];
            dst[i] = (ushort)(
                ((px & 0x001F) << 11) |
                ((px & 0x03E0) << 1)  |
                ((px & 0x7C00) >> 9)  |
                ((px & 0x8000) != 0 ? 1 : 0)
            );
        }
    }

    public static void Convert5551To1555(ReadOnlySpan<ushort> src, Span<ushort> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            ushort px = src[i];
            dst[i] = (ushort)(
                ((px & 0xF800) >> 11) |
                ((px & 0x07C0) >> 1)  |
                ((px & 0x003E) << 9)  |
                ((px & 0x0001) != 0 ? 0x8000 : 0)
            );
        }
    }
}
