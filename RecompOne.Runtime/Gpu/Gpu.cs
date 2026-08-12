using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime;

//ToDO: HW Renderer must use a separate "view" quad text so widescren patches are possible
// or find a betrter approach, maybe allongate the vram as needed? a dufferent tex seens more ideal?
public sealed partial class Gpu
{
    public const int VramWidth = VramShadow.Width;
    public const int VramHeight = VramShadow.Height;

    public readonly VramShadow Shadow = new();
    public ushort[] Vram => Shadow.Pixels;

    int _drawAreaLeft, _drawAreaTop, _drawAreaRight = VramWidth - 1, _drawAreaBottom = VramHeight - 1;
    int _drawOffsetX, _drawOffsetY;

    int _texPageX, _texPageY;
    int _texDepth;
    int _blendMode;
    bool _dither;
    bool _texDisable;

    int _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY;

    bool _setMask, _checkMask;

    int _dispVramX, _dispVramY;
    int _hRange1 = 0x200, _hRange2 = 0xC00, _vRange1 = 0x10, _vRange2 = 0x100;
    int _hres;
    bool _hres368, _vres480, _pal, _disp24, _interlace, _displayDisabled = true;
    int _dmaDir;

    readonly List<uint> _fifo = new(16);
    int _need;
    bool _polyline;

    bool _loadImage;
    int _loadX, _loadY, _loadW, _loadH, _loadPx;

    bool _readImage;
    int _readX, _readY, _readW, _readH, _readPx;
    uint _gpuRead;

    bool _statField;

    public int DisplayX => _dispVramX;
    public int DisplayY => _dispVramY;
    public bool DisplayEnabled => !_displayDisabled;
    public bool Display24Bit => _disp24;
    public bool Pal => _pal;

    int CyclesPerPixel => _hres368 ? 7 : _hres switch { 0 => 10, 1 => 8, 2 => 5, _ => 4 };

    public int DisplayWidth
    {
        get
        {
            int w = ((_hRange2 - _hRange1) / CyclesPerPixel + 2) & ~3;
            return Math.Clamp(w, 0, VramWidth);
        }
    }

    public int DisplayHeight
    {
        get
        {
            int lines = _vRange2 - _vRange1;
            if (_vres480) lines <<= 1;
            return Math.Clamp(lines, 0, VramHeight);
        }
    }

    public uint ReadStat() 
    {
        uint s = 0;
        s |= (uint)((_texPageX / 64) & 0xF);
        s |= (uint)(((_texPageY / 256) & 1) << 4);
        s |= (uint)((_blendMode & 3) << 5);
        s |= (uint)((_texDepth & 3) << 7);
        
        if (_dither) s |= 1u << 9;
        s |= 1u << 10;
        if (_setMask) s |= 1u << 11;
        if (_checkMask) s |= 1u << 12;
        s |= 1u << 13;
        
        if (_texDisable) s |= 1u << 15;
        if (_hres368) s |= 1u << 16;
        
        s |= (uint)((_hres & 3) << 17);
        
        if (_vres480) s |= 1u << 19;
        if (_pal) s |= 1u << 20;
        if (_disp24) s |= 1u << 21;
        if (_interlace) s |= 1u << 22;
        if (_displayDisabled) s |= 1u << 23;
        
        s |= 1u << 26;
        s |= 1u << 27;
        s |= 1u << 28;
        s |= (uint)((_dmaDir & 3) << 29);
        s |= _dmaDir switch { 1 => 1u << 25, 2 => 1u << 28, 3 => 1u << 27, _ => 0u };
        
        _statField = !_statField;
        if (_statField) s |= 1u << 31;
        return s;
    }

    public uint ReadData()
    {
        if (!_readImage) return _gpuRead;
        ushort lo = ReadImageHalfword();
        ushort hi = ReadImageHalfword();
        return (uint)(lo | (hi << 16));
    }

    public void WriteGp0(uint word)
    {
        if (_loadImage) { StoreImageHalfword((ushort)word); StoreImageHalfword((ushort)(word >> 16)); return; }
        if (_polyline)
        {
            if ((word & 0xF000F000u) == 0x50005000u) { _polyline = false; ExecutePolyline(); _fifo.Clear(); }
            else _fifo.Add(word);
            return;
        }

        _fifo.Add(word);
        if (_fifo.Count == 1)
        {
            _need = CommandLength(word);
            if (_need == LenPolyline) { _polyline = true; return; }
            if (_need == LenImageLoad) _need = 3;
        }

        if (_fifo.Count >= _need) { Execute(); if (!_loadImage) _fifo.Clear(); }
    }

    public void WriteGp1(uint word)
    {
        uint op = (word >> 24) & 0xFF;
        uint p = word & 0xFFFFFF;
        switch (op)
        {
            case >= 0x05 and <= 0x08:
                WriteGp1Display(op, p);
                GpuHle.NotifyDisplay(_dispVramX, _dispVramY, DisplayWidth, DisplayHeight);
                return;
            case 0x00: Reset(); break;
            case 0x01: _fifo.Clear(); _polyline = false; _loadImage = false; break;
            case 0x02: break;
            case 0x03: _displayDisabled = (p & 1) != 0; break;
            case 0x04: _dmaDir = (int)(p & 3); break;
            case 0x10: SetGpuInfo(p); break;
        }
    }

    void WriteGp1Display(uint op, uint p)
    {
        switch (op)
        {
            case 0x05: _dispVramX = (int)(p & 0x3FF); _dispVramY = (int)((p >> 10) & 0x1FF); break;
            case 0x06: _hRange1 = (int)(p & 0xFFF); _hRange2 = (int)((p >> 12) & 0xFFF); break;
            case 0x07: _vRange1 = (int)(p & 0x3FF); _vRange2 = (int)((p >> 10) & 0x3FF); break;
            case 0x08:
                _hres = (int)(p & 3);
                _hres368 = (p & 0x40) != 0;
                _vres480 = (p & 4) != 0;
                _pal = (p & 8) != 0;
                _disp24 = (p & 0x10) != 0;
                _interlace = (p & 0x20) != 0;
                break;
        }
    }

    void Reset()
    {
        _fifo.Clear();
        _polyline = _loadImage = _readImage = false;
        _displayDisabled = true;
        _dmaDir = 0;
        _texPageX = _texPageY = _texDepth = _blendMode = 0;
        _dither = _texDisable = false;
        _texWinMaskX = _texWinMaskY = _texWinOffX = _texWinOffY = 0;
        _drawAreaLeft = _drawAreaTop = 0;
        _drawAreaRight = VramWidth - 1;
        _drawAreaBottom = VramHeight - 1;
        _drawOffsetX = _drawOffsetY = 0;
        _setMask = _checkMask = false;
        _dispVramX = _dispVramY = 0;
    }

    void SetGpuInfo(uint p)
    {
        switch (p & 0xFF)
        {
            case 0x03: _gpuRead = (uint)(_drawAreaLeft | (_drawAreaTop << 10)); break;
            case 0x04: _gpuRead = (uint)(_drawAreaRight | (_drawAreaBottom << 10)); break;
            case 0x05: _gpuRead = (uint)((_drawOffsetX & 0x7FF) | ((_drawOffsetY & 0x7FF) << 11)); break;
            default: _gpuRead = 0; break;
        }
    }

    public struct GpuStateSnapshot
    {
        public int DrawAreaLeft, DrawAreaTop, DrawAreaRight, DrawAreaBottom;
        public int DrawOffsetX, DrawOffsetY;
        public int TexPageX, TexPageY, TexDepth, BlendMode;
        public bool Dither, TexDisable;
        public int TexWinMaskX, TexWinMaskY, TexWinOffX, TexWinOffY;
        public bool SetMask, CheckMask;
        public int DispVramX, DispVramY;
        public int HRange1, HRange2, VRange1, VRange2;
        public int HRes;
        public bool HRes368, VRes480, Pal, Disp24, Interlace, DisplayDisabled;
        public int DmaDir;
    }

    public GpuStateSnapshot SnapshotState() => new GpuStateSnapshot
    {
        DrawAreaLeft = _drawAreaLeft,
        DrawAreaTop = _drawAreaTop,
        DrawAreaRight = _drawAreaRight,
        DrawAreaBottom = _drawAreaBottom,
        DrawOffsetX = _drawOffsetX,
        DrawOffsetY = _drawOffsetY,
        TexPageX = _texPageX,
        TexPageY = _texPageY,
        TexDepth = _texDepth,
        BlendMode = _blendMode,
        Dither = _dither,
        TexDisable = _texDisable,
        TexWinMaskX = _texWinMaskX,
        TexWinMaskY = _texWinMaskY,
        TexWinOffX = _texWinOffX,
        TexWinOffY = _texWinOffY,
        SetMask = _setMask,
        CheckMask = _checkMask,
        DispVramX = _dispVramX,
        DispVramY = _dispVramY,
        HRange1 = _hRange1,
        HRange2 = _hRange2,
        VRange1 = _vRange1,
        VRange2 = _vRange2,
        HRes = _hres,
        HRes368 = _hres368,
        VRes480 = _vres480,
        Pal = _pal,
        Disp24 = _disp24,
        Interlace = _interlace,
        DisplayDisabled = _displayDisabled,
        DmaDir = _dmaDir
    };

    public void RestoreState(in GpuStateSnapshot s)
    {
        _drawAreaLeft = s.DrawAreaLeft;
        _drawAreaTop = s.DrawAreaTop;
        _drawAreaRight = s.DrawAreaRight;
        _drawAreaBottom = s.DrawAreaBottom;
        _drawOffsetX = s.DrawOffsetX;
        _drawOffsetY = s.DrawOffsetY;
        _texPageX = s.TexPageX;
        _texPageY = s.TexPageY;
        _texDepth = s.TexDepth;
        _blendMode = s.BlendMode;
        _dither = s.Dither;
        _texDisable = s.TexDisable;
        _texWinMaskX = s.TexWinMaskX;
        _texWinMaskY = s.TexWinMaskY;
        _texWinOffX = s.TexWinOffX;
        _texWinOffY = s.TexWinOffY;
        _setMask = s.SetMask;
        _checkMask = s.CheckMask;
        _dispVramX = s.DispVramX;
        _dispVramY = s.DispVramY;
        _hRange1 = s.HRange1;
        _hRange2 = s.HRange2;
        _vRange1 = s.VRange1;
        _vRange2 = s.VRange2;
        _hres = s.HRes;
        _hres368 = s.HRes368;
        _vres480 = s.VRes480;
        _pal = s.Pal;
        _disp24 = s.Disp24;
        _interlace = s.Interlace;
        _displayDisabled = s.DisplayDisabled;
        _dmaDir = s.DmaDir;

        GpuHle.NotifyDisplay(_dispVramX, _dispVramY, DisplayWidth, DisplayHeight);
    }
}
