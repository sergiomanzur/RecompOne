namespace RecompOne.Runtime.Hardware;

public static class Controller
{
    public const ushort Select = 1 << 0;
    public const ushort L3 = 1 << 1;
    public const ushort R3 = 1 << 2;
    public const ushort Start = 1 << 3;
    public const ushort Up = 1 << 4;
    public const ushort Right = 1 << 5;
    public const ushort Down = 1 << 6;
    public const ushort Left = 1 << 7;
    public const ushort L2 = 1 << 8;
    public const ushort R2 = 1 << 9;
    public const ushort L1 = 1 << 10;
    public const ushort R1 = 1 << 11;
    public const ushort Triangle = 1 << 12;
    public const ushort Circle = 1 << 13;
    public const ushort Cross = 1 << 14;
    public const ushort Square = 1 << 15;

    public static ushort State = 0xFFFF;
    public static byte   RightX = 0x80;
    public static byte   RightY = 0x80;
    public static byte   LeftX = 0x80;
    public static byte   LeftY = 0x80;

    public static ushort State2 = 0xFFFF;
    public static bool   Connected2;
    public static byte   RightX2 = 0x80;
    public static byte   RightY2 = 0x80;
    public static byte   LeftX2 = 0x80;
    public static byte   LeftY2 = 0x80;

    // Host-supplied input, e.g. the Android on-screen overlay. Kept separate from the
    // physical pad so the two producers cannot clobber each other - InputManager ANDs
    // them together once per frame. Both values are active low (0 = pressed).
    static readonly object _externalLock = new();
    static ushort _external = 0xFFFF;
    static readonly byte[] _externalHold = new byte[16];

    // A tap can begin and end between two polls, and a press that is only visible for a
    // single frame is unreliable - the game samples the pad once per frame and misses
    // most of them. Every press is therefore stretched to a minimum number of polled
    // frames (~50ms at 60fps), which is imperceptible to the player but always seen.
    const byte MinPressFrames = 3;

    /// <summary>Publish the host's current held buttons. Safe to call from any thread.</summary>
    public static void SetExternalState(ushort state)
    {
        lock (_externalLock)
        {
            for (int i = 0; i < 16; i++)
                if ((state & (1 << i)) == 0)
                    _externalHold[i] = MinPressFrames;
            _external = state;
        }
    }

    /// <summary>
    /// Held buttons, plus any button whose minimum press has not elapsed yet. Called once
    /// per frame; the counters always decrement, so a button can never stick.
    /// </summary>
    public static ushort ConsumeExternalState()
    {
        lock (_externalLock)
        {
            ushort v = _external;
            for (int i = 0; i < 16; i++)
            {
                if (_externalHold[i] == 0) continue;
                _externalHold[i]--;
                v &= unchecked((ushort)~(1 << i));
            }
            return v;
        }
    }
}
