using System;
using System.Threading;
using RecompOne.Runtime.Hardware;
#if !ANDROID
using Silk.NET.OpenAL;
using ALDevice = Silk.NET.OpenAL.Device;
using ALCtx = Silk.NET.OpenAL.Context;
#else
using Android.Media;
#endif

namespace RecompOne.Runtime.Host;

public static unsafe class Audio
{
#if ANDROID
    private static AudioTrack? _track;
    private static Thread? _mixerThread;
    private static Spu? _spu;
    private static volatile bool _running;
    private static float _masterVolume = 1.0f;
    private static short[] _sampleBuf = new short[256 * 2];

    public static void Initialize()
    {
        try
        {
            int minBufSize = AudioTrack.GetMinBufferSize(
                44100,
                ChannelOut.Stereo,
                Encoding.Pcm16bit);

            // Was pinned to a tenth of a second, which is audible as a lag behind the action.
            // Two times the device's own minimum keeps enough headroom to absorb a late mix
            // while roughly halving the delay; the floor stops a device reporting a tiny
            // minimum from leaving no margin at all.
            int bufSize = Math.Max(minBufSize * 2, 44100 * 2 * 2 / 20); // ~50 ms

            var builder = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)
                    .SetContentType(AudioContentType.Music)
                    .Build())
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetEncoding(Encoding.Pcm16bit)
                    .SetSampleRate(44100)
                    .SetChannelMask(ChannelOut.Stereo)
                    .Build())
                .SetBufferSizeInBytes(bufSize)
                .SetTransferMode(AudioTrackMode.Stream);

            // Asks the platform for the shorter mixing path. Oreo and up only, and the device
            // is free to decline, so it stays best-effort.
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            {
                try { builder = builder.SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)!; }
                catch (Exception ex) { Console.WriteLine($"[AndroidAudio] Low latency mode unavailable: {ex.Message}"); }
            }

            _track = builder.Build();

            _track.Play();

            _running = true;
            _mixerThread = new Thread(MixerLoop)
            {
                IsBackground = true,
                Name = "spu-mixer-android",
                Priority = System.Threading.ThreadPriority.Highest,
            };
            _mixerThread.Start();
            Console.WriteLine("[AndroidAudio] AudioTrack started successfully.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AndroidAudio] AudioTrack init failed: {ex.Message}");
        }
    }

    public static void Attach(Spu? spu)
    {
        if (spu == null) return;
        _spu = spu;
        spu.VoiceGain = Config.ConfigManager.Game.SpuVolume;
        spu.XaGain = Config.ConfigManager.Game.XaVolume;
    }

    // Mirrors the desktop backend: the mixer loop keeps running, it just stops
    // pulling samples until a new Spu is attached (used by Runtime.ResetForBoot).
    public static void Detach() => _spu = null;

    public static void SetMasterVolume(float volume)
    {
        _masterVolume = Math.Clamp(volume, 0f, 1f);
        if (_track != null)
        {
            _track.SetVolume(_masterVolume);
        }
    }

    private static volatile bool _isPaused;

    public static void Pause()
    {
        _isPaused = true;
        try { _track?.Pause(); } catch { }
    }

    public static void Resume()
    {
        _isPaused = false;
        try { _track?.Play(); } catch { }
    }

    private static void MixerLoop()
    {
        // Android's scheduler, not the CLR, decides whether this thread runs ahead of the
        // render thread, and a managed Highest means little to it. Without the audio priority
        // class the mixer gets preempted mid-frame and the track runs dry - heard as crackling.
        try { global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio); }
        catch (Exception ex) { Console.WriteLine($"[AndroidAudio] Could not raise mixer priority: {ex.Message}"); }

        while (_running)
        {
            if (_isPaused)
            {
                Thread.Sleep(50);
                continue;
            }
            var spu = _spu;
            if (spu != null && _track != null)
            {
                spu.Mix(_sampleBuf, 256);
                _track.Write(_sampleBuf, 0, _sampleBuf.Length);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
    }

    public static void Shutdown()
    {
        _running = false;
        _mixerThread?.Join();
        try
        {
            _track?.Stop();
            _track?.Release();
            _track = null;
        }
        catch { }
    }
#else
    static ALContext? _alc;
    static AL? _al;
    static ALDevice* _device;
    static ALCtx* _context;

    const int NumBuffers = 8;
    const int FramesPerBuffer = 256;

    static uint _source;
    static uint[] _buffers = new uint[NumBuffers];
    static short[] _sampleBuf = new short[FramesPerBuffer * 2];

    static Thread? _mixerThread;
    static Spu? _spu;
    static volatile bool _running;
    static float _masterVolume = 1.0f;

    public static void Initialize()
    {
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice("");
            if (_device == null)
            {
                Console.Error.WriteLine("[Host] no audio device, audio disabled");
                return;
            }
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);

            _source = _al.GenSource();
            _al.SetSourceProperty(_source, SourceFloat.Gain, _masterVolume);
            fixed (uint* ptr = _buffers)
                _al.GenBuffers(NumBuffers, ptr);

            for (int i = 0; i < _buffers.Length; i++)
            {
                _al.BufferData(_buffers[i], BufferFormat.Stereo16, _sampleBuf, 44100);
                uint b = _buffers[i];
                _al.SourceQueueBuffers(_source, 1, &b);
            }

            _al.SourcePlay(_source);

            _running = true;
            _mixerThread = new Thread(MixerLoop) { IsBackground = true, Name = "spu-mixer" };
            _mixerThread.Start();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] audio init failed: {e.Message}");
        }
    }

    public static void Attach(Spu? spu)
    {
        if (spu == null) return;
        _spu = spu;
        spu.VoiceGain = Config.ConfigManager.Game.SpuVolume;
        spu.XaGain = Config.ConfigManager.Game.XaVolume;
    }

    public static void Detach() => _spu = null;

    public static void SetMasterVolume(float volume)
    {
        _masterVolume = Math.Clamp(volume, 0f, 1f);
        if (_al != null && _source != 0)
            _al.SetSourceProperty(_source, SourceFloat.Gain, _masterVolume);
    }

    static void MixerLoop()
    {
        while (_running)
        {
            var spu = _spu;
            if (spu != null) FillBuffers(spu);
            Thread.Sleep(3);
        }
    }

    static void FillBuffers(Spu spu)
    {
        _al!.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        while (processed > 0)
        {
            uint buf = 0;
            _al.SourceUnqueueBuffers(_source, 1, &buf);

            spu.Mix(_sampleBuf, FramesPerBuffer);

            _al.BufferData(buf, BufferFormat.Stereo16, _sampleBuf, 44100);
            _al.SourceQueueBuffers(_source, 1, &buf);
            processed--;
        }

        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if (state != (int)SourceState.Playing)
            _al.SourcePlay(_source);
    }

    public static void Shutdown()
    {
        if (_alc == null) return;
        _running = false;
        _mixerThread?.Join();
        if (_al != null)
        {
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
            _al.DeleteBuffers(_buffers);
        }
        if (_context != null) _alc.DestroyContext(_context);
        if (_device != null) _alc.CloseDevice(_device);
    }
#endif
}
