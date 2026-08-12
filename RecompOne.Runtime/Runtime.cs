using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public enum RunMode { Retail, Devkit }

public static class Runtime
{
    public static CpuContext? Cpu { get; private set; }
    public static IMemory? Mem { get; private set; }
    public static Gpu? Gpu;
    public static Spu? Spu;
    public static Cdrom.CdController? Cd;

    public static RunMode Mode { get; private set; } = RunMode.Retail;
    public static void SetMode(RunMode mode) => Mode = mode; //devkit vs retail, devkits reads from sim and has more ram
    public static string CdPath => Config.ConfigManager.Game.CdPath;

    public static Func<string, string?>? DiscValidator;
    public static string? ValidateDisc(string path)
    {
        try { return DiscValidator?.Invoke(path); }
        catch (Exception e) { return e.Message; }
    }
    
    public static Config.ViewConfig View => Config.ConfigManager.View;
    public static void SaveView() => Config.ConfigManager.SaveView(Host.Window.PanelManager.Panels);
    
    public static Hardware.MemoryCard CardA = new("carda.sav") { Enabled = true };
    public static Hardware.MemoryCard CardB = new("cardb.sav") { Enabled = true };

    static void LoadMemoryCards()
    {
        var g = Config.ConfigManager.Game;
        CardA = new(Fallback(g.CardAPath, "carda.sav")) { Enabled = g.CardAEnabled };
        CardB = new(Fallback(g.CardBPath, "cardb.sav")) { Enabled = g.CardBEnabled };

        static string Fallback(string path, string def) => string.IsNullOrWhiteSpace(path) ? def : path;
    }
    public static readonly Memory.RamLogger RamLog = new();
    public static readonly Dispatch.OverlayEventLog OverlayLog = new();

    public static void Initialize(string title)
    {
        Diagnostics.ConsoleMirror.Install();
        HostWindow.Initialize(title);
        LoadMemoryCards();
        Audio.Initialize();
        Audio.SetMasterVolume(Config.ConfigManager.Game.Muted ? 0f : Config.ConfigManager.Game.MasterVolume);
        if (Event.HasAnyListeners<RuntimeReadyEvent>())
        {
            Event.Dispatch(new RuntimeReadyEvent());
        }
    }

    public static void WaitForValidDisc() => HostWindow.WaitForValidDisc();

    public static string Title
    {
        get => HostWindow.Title;
        set => HostWindow.Title = value;
    }

    public static void SetTitle(string title) => HostWindow.SetTitle(title);

    public static void SetIcon(byte[] data) => HostWindow.SetIcon(data);

    public static void SetIcon(byte[] rgba, int width, int height) => HostWindow.SetIcon(rgba, width, height);

    public static void ClearIcon() => HostWindow.ClearIcon();
    
    public static void ShowNotice(string message) => Host.Window.NoticePopup.Show(message);
    public static void SetStartupNotice(string message, string title = "Notice", string ackKey = "StartupNoticeAck") => Host.Window.StartupNotice.Set(message, title, ackKey);

    public static void SetContext(CpuContext c, IMemory m)
    {
        Cpu = c;
        Mem = m;
    }

    public static Action? OnBeforePresentFrame;

    public static void PresentFrame()
    {
        OnBeforePresentFrame?.Invoke();
        HostWindow.Present(Gpu);
        Audio.Attach(Spu);
        FrameClock.Throttle();
        Sdk.LibCd.Tick();
        if (Mem != null) { Bios.BiosB.RefreshPad(Mem); Sdk.LibPad.Refresh(Mem); } //is this correct?
        DispatchIrq(0); //using this to dispatch irqs too if necessary, probably not needed after the rest of stuff is reimplemented
    }

    public static void DispatchIrq(int irq)
    {
        if (Cpu != null && Mem != null)
            Interrupts.Deliver(irq, Cpu, Mem);
    }

    public static void Shutdown()
    {
        Audio.Shutdown();
        HostWindow.Shutdown();
    }
}
