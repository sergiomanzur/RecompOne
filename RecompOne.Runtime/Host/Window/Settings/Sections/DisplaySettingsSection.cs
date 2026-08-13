using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class DisplaySettingsSection : ISettingsSection
{
    public string Id => "display";
    public string TitleKey => "settings.display";
    public int Order => 5;

    static readonly string[] Backends = ["auto", "gl45", "gl33"];

    public void Draw()
    {
        bool fullscreen = ConfigManager.View.Fullscreen;
        if (ImGui.Checkbox(Localization.T("settings.display.fullscreen"), ref fullscreen))
        {
            ConfigManager.View.Fullscreen = fullscreen;
            HostWindow.SetFullscreen(fullscreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }

        bool vsync = ConfigManager.View.VSync;
        if (ImGui.Checkbox(Localization.T("settings.display.vsync"), ref vsync))
        {
            ConfigManager.View.VSync = vsync;
            HostWindow.SetVSync(vsync);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("settings.display.vsync_hint"));

        bool native = ConfigManager.View.NativeResolution;
        if (ImGui.Checkbox(Localization.T("settings.display.native_resolution"), ref native))
        {
            ConfigManager.View.NativeResolution = native;
            Hle.GpuHle.NativeResolution = native;
            ConfigManager.SaveView(PanelManager.Panels);
            NoticePopup.Show(Localization.T("common.restart_required"));
        }
        if (ConfigManager.View.NativeResolution != (Hle.GlVram.Scale == 1))
            ImGui.TextDisabled(Localization.T("settings.display.restart_pending"));

        ImGui.Separator();

        int index = Array.IndexOf(Backends, ConfigManager.View.GpuBackend);
        if (index < 0) index = 0;
        if (ImGui.Combo(Localization.T("settings.display.backend"), ref index, Backends, Backends.Length))
        {
            ConfigManager.View.GpuBackend = Backends[index];
            ConfigManager.SaveView(PanelManager.Panels);
            NoticePopup.Show(Localization.T("common.restart_required"));
        }
        ImGui.TextDisabled(Localization.T("settings.display.backend_running", Hle.GpuBackendFactory.Selected));
    }
}
