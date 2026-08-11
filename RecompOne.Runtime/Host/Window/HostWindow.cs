using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Host;

public static class HostWindow
{
    static IView? _window;
    static GL? _gl;
    static ImGuiController? _imgui;
    static bool _headless;
    static Gpu? _gpu;

    static uint _displayTex;
    static uint _vramTex;
    static uint _ramTex;
    static Hle.GlCore? _glBackend;

    static byte[] _rgbDisplay = [];
    static byte[] _rgbVram = [];
    static byte[] _ramFront = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    static byte[] _ramBack = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    static Task? _ramTask;
    static volatile bool _ramReady;
    static int _ramFrame;

    static bool _layoutPending = true;
    static bool _closed;
    static DiscPickerPopup? _discPicker;

    static float _dpiScale = 1f;

    public static float DpiScale => _dpiScale;

    static unsafe float QueryDpiScale()
    {
#if !ANDROID
        try
        {
            var glfw = Silk.NET.GLFW.Glfw.GetApi();
            var monitor = glfw.GetPrimaryMonitor();
            if (monitor != null)
            {
                glfw.GetMonitorContentScale(monitor, out float xs, out float ys);
                float s = MathF.Max(xs, ys);
                if (s >= 0.5f && s <= 8f) return s;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Host] cant read scale: {e.Message}");
        }
#endif

        try
        {
            var fb = _window!.FramebufferSize;
            var size = _window.Size;
            if (size.X > 0 && fb.X > 0)
            {
                float s = (float)fb.X / size.X;
                if (s >= 0.5f && s <= 8f) return s;
            }
        }
        catch { }

        return 1f;
    }

    static GraphicsAPI MaxSupportedApi() =>
#if ANDROID
        new GraphicsAPI(ContextAPI.OpenGLES, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 0));
#else
        OperatingSystem.IsMacOS()
            ? new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 1))
            : new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 5));
#endif

    public static void Initialize(string title)
    {
        ConfigManager.Load();

        try
        {
#if ANDROID
            var viewOptions = ViewOptions.Default with
            {
                API = MaxSupportedApi(),
                VSync = ConfigManager.View.VSync,
            };
            _window = Silk.NET.Windowing.Window.GetView(viewOptions);
#else
            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(1280, 720),
                Title = title,
                VSync = ConfigManager.View.VSync,
                UpdatesPerSecond = 0,
                FramesPerSecond = 0,
                WindowState = ConfigManager.View.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
                API = MaxSupportedApi(),
            };
            _window = Window.Create(options);
#endif
            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.Closing += OnClosing;
            _window.Initialize();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] window unavailable {e.Message}");
            _headless = true;
        }
    }

    public static string Title
    {
        get => (_window as IWindow)?.Title ?? "";
        set { if (_window is IWindow w) w.Title = value ?? ""; }
    }

    public static void SetTitle(string title) => Title = title;

    static Silk.NET.Core.RawImage? _pendingIcon;

    public static void SetIcon(byte[] data)
    {
        try
        {
            var rgba = Decode(data, out int w, out int h);
            if (rgba == null)
            {
                Console.Error.WriteLine("[Host] icon format not supported");
                return;
            }
            SetIcon(rgba, w, h);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] failed to set icon: {e.Message}");
        }
    }

    public static void SetIcon(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            Console.Error.WriteLine("[Host] icon pixel buffer does not match its size");
            return;
        }

        var image = new Silk.NET.Core.RawImage(width, height, rgba);
        _pendingIcon = image;
        Apply(image);
    }

    public static void ClearIcon()
    {
        _pendingIcon = null;
        if (_window is not IWindow w) return;
        try { w.SetWindowIcon(ReadOnlySpan<Silk.NET.Core.RawImage>.Empty); }
        catch (Exception e) { Console.Error.WriteLine($"[Host] failed to clear icon: {e.Message}"); }
    }

    static void Apply(Silk.NET.Core.RawImage image)
    {
        if (_window is not IWindow w) return;
        try
        {
            var icons = new[] { image };
            w.SetWindowIcon(icons);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] failed to set icon: {e.Message}");
        }
    }

    static byte[]? Decode(byte[] data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4) return null;

        if (data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
        {
            var best = LargestIcoEntry(data);
            if (best == null) return null;
            data = best;
        }

        var img = StbImageSharp.ImageResult.FromMemory(data, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        if (img == null) return null;
        width = img.Width;
        height = img.Height;
        return img.Data;
    }

    static byte[]? LargestIcoEntry(byte[] ico)
    {
        int count = BitConverter.ToUInt16(ico, 4);
        int bestArea = -1;
        byte[]? best = null;

        for (int i = 0; i < count; i++)
        {
            int e = 6 + i * 16;
            if (e + 16 > ico.Length) break;

            int w = ico[e] == 0 ? 256 : ico[e];
            int h = ico[e + 1] == 0 ? 256 : ico[e + 1];
            int size = BitConverter.ToInt32(ico, e + 8);
            int offset = BitConverter.ToInt32(ico, e + 12);
            if (size <= 0 || offset < 0 || offset + size > ico.Length) continue;

            bool png = size > 8 && ico[offset] == 0x89 && ico[offset + 1] == 0x50 &&
                       ico[offset + 2] == 0x4E && ico[offset + 3] == 0x47;
            if (!png) continue;

            int area = w * h;
            if (area <= bestArea) continue;
            bestArea = area;
            best = ico.AsSpan(offset, size).ToArray();
        }

        return best;
    }

    public static void Present(Gpu? gpu)
    {
        _gpu = gpu;
        if (_headless || _window == null) return;
        try { _window.DoEvents(); }
        catch (Exception e) {
            Console.WriteLine(e.Message);
        }
        if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
        InputManager.Poll();
        if (InputManager.ConsumeTopBarToggle())
        {
            ConfigManager.View.HideTopBar = !ConfigManager.View.HideTopBar;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (InputManager.ConsumeFullscreenToggle())
        {
            ConfigManager.View.Fullscreen = !ConfigManager.View.Fullscreen;
            SetFullscreen(ConfigManager.View.Fullscreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        _window.DoRender();
    }

    internal static void Pump()
    {
        if (_headless || _window == null) return;
        try { _window.DoEvents(); } catch { }
        if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
        _window.DoRender();
    }

    public static void Shutdown()
    {
        if (!_headless && _window != null && !_window.IsClosing)
            _window.Close();
        InputManager.Shutdown();
    }

    public static void SetFullscreen(bool on)
    {
        if (_window is not IWindow w) return;
        w.WindowState = on ? WindowState.Fullscreen : WindowState.Normal;
        if (on) SetAutoIconify(false);
    }

    static unsafe void SetAutoIconify(bool on)
    {
#if !ANDROID
        try
        {
            var handle = _window?.Native?.Glfw;
            if (handle is not { } h) return;
            Silk.NET.GLFW.Glfw.GetApi().SetWindowAttrib(
                (Silk.NET.GLFW.WindowHandle*)h,
                Silk.NET.GLFW.WindowAttributeSetter.AutoIconify, on);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Host] auto-iconify unavailable: {e.Message}");
        }
#endif
    }

    public static bool IsKeyDown(Key k) => InputManager.IsKeyDown(k);

    public static void RequestDiscPath() => _discPicker?.Show();

    public static void WaitForValidDisc() // wait for disc path to be valid before running it!!
    {
        if (_headless || _window == null) return;

        while (StartupNotice.NeedsAck)
        {
            try { _window.DoEvents(); } catch { }
            if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
            InputManager.Poll();
            _window.DoRender();
        }

        while (true)
        {
            var path = ConfigManager.Game.CdPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && Runtime.ValidateDisc(path) == null)
                return;

            try { _window.DoEvents(); } catch { }
            if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
            InputManager.Poll();
            _window.DoRender();
        }
    }

    static void OnLoad()
    {
        var input = _window!.CreateInput();
        InputManager.Initialize(input);

        if (_pendingIcon is { } icon) Apply(icon);

        _gl = GL.GetApi(_window);
        _gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);

        var fb = _window!.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        _window.FramebufferResize += size => _gl?.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _displayTex = CreateTexture(_gl);
        _vramTex= CreateTexture(_gl);
        _ramTex = CreateTexture(_gl);

        Hle.GlVram.Scale = ConfigManager.View.NativeResolution ? 1 : 4;
        _glBackend = (Hle.GlCore)Hle.GpuBackendFactory.Create(_gl,
            Hle.GpuBackendFactory.Parse(ConfigManager.View.GpuBackend));
        _glBackend.InitGl();
        Hle.GpuHle.Active = _glBackend.Ready;
        Hle.GpuHle.Backend = _glBackend;
        Hle.GpuHle.NativeResolution = ConfigManager.View.NativeResolution;

        try
        {
            _imgui = new ImGuiController(_gl, _window, input, null, ConfigureImGui);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] ImGui initialization skipped: {e.Message}");
            _imgui = null;
        }

        PanelManager.Register(new OutputPanel());
        PanelManager.Register(new VramViewerPanel());
        PanelManager.Register(new CpuStatePanel());
        PanelManager.Register(new RamMapPanel());
        PanelManager.Register(new MemoryEditorPanel());
        PanelManager.Register(new SpuViewerPanel());
        PanelManager.Register(new CdDebugPanel());
        PanelManager.Register(new ConsolePanel());
        PanelManager.Register(new OverlayEventsPanel());
        PanelManager.Register(new SettingsPopup());
        PanelManager.Register(new Modding.ModsPopup());
        MainMenuBar.RegisterBuiltins();

        SettingsRegistry.Register(new InputSettingsSection());
        SettingsRegistry.Register(new DisplaySettingsSection());
        SettingsRegistry.Register(new PathsSettingsSection());
        SettingsRegistry.Register(new AudioSettingsSection());

        _discPicker = new DiscPickerPopup();
        PanelManager.Register(_discPicker);

        ConfigManager.ApplyViewToPanels(PanelManager.Panels);

        var cdPath = ConfigManager.Game.CdPath;
        if (string.IsNullOrWhiteSpace(cdPath) || !File.Exists(cdPath) || Runtime.ValidateDisc(cdPath) != null)
            _discPicker.Show();
    }

    static void ConfigureImGui()
    {
        _dpiScale = QueryDpiScale();
        Console.WriteLine($"[Host] display scale: {_dpiScale:0.##}x");

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        io.FontGlobalScale = Config.ConfigManager.View.UiScale;
        unsafe { io.NativePtr->IniFilename = null; }

        Icons.Load(13f * _dpiScale);
        ImGui.GetStyle().ScaleAllSizes(_dpiScale);

        if (Config.ConfigManager.ApplyImGuiLayout())
            _layoutPending = false;

        if (ConfigManager.View.Fullscreen) SetAutoIconify(false);
    }

    public static void SetVSync(bool on)
    {
        if (_window is IWindow w) w.VSync = on;
    }

    public enum AspectRatioMode
    {
        AutoDevice = 0,     // Dynamic aspect fit for any device (Portrait & Landscape)
        Original_4_3 = 1,   // 4:3 PSX ratio
        Widescreen_16_9 = 2, // 16:9 widescreen
        Stretch = 3         // Stretch fill
    }

    public static AspectRatioMode CurrentAspectRatio = AspectRatioMode.AutoDevice;
    public static volatile bool PendingGpuBackendReset;

    public static void RequestGpuReset()
    {
        PendingGpuBackendReset = true;
    }

    static uint _directVao, _directVbo, _directProg;
    static int _uDirectTex;

    static unsafe void RenderDirectDisplay(GL gl, int screenW, int screenH)
    {
        uint tex = OutputPanel.TextureId;
        if (tex == 0) tex = _displayTex;
        if (tex == 0) tex = _vramTex;
        if (tex == 0) return;

        if (_directProg == 0)
        {
            string vs = """
                #version 300 es
                precision highp float;
                precision highp int;
                layout(location = 0) in vec2 aPos;
                layout(location = 1) in vec2 aUv;
                out vec2 vUv;
                void main() {
                    vUv = aUv;
                    gl_Position = vec4(aPos, 0.0, 1.0);
                }
                """;
            string fs = """
                #version 300 es
                precision highp float;
                precision highp int;
                in vec2 vUv;
                uniform sampler2D uTex;
                out vec4 FragColor;
                void main() {
                    FragColor = texture(uTex, vUv);
                }
                """;
            _directProg = Hle.GlShaders.Build(gl, vs, fs, "direct_present");
            if (_directProg != 0)
            {
                _uDirectTex = gl.GetUniformLocation(_directProg, "uTex");
                _directVao = gl.GenVertexArray();
                _directVbo = gl.GenBuffer();
                gl.BindVertexArray(_directVao);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, _directVbo);
                float[] quad = {
                    -1f, -1f, 0f, 1f,
                     1f, -1f, 1f, 1f,
                    -1f,  1f, 0f, 0f,
                     1f,  1f, 1f, 0f
                };
                fixed (float* qp = quad)
                    gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), qp, BufferUsageARB.StaticDraw);
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            }
        }

        if (_directProg == 0) return;

        int vx, vy, vw, vh;
        if (screenW < screenH)
        {
            // --- PORTRAIT ORIENTATION ---
            // Fit game screen centered in vertical middle of portrait display
            vw = screenW;
            vh = (int)(vw * 3.0f / 4.0f);
            if (vh > (int)(screenH * 0.55f))
            {
                vh = (int)(screenH * 0.55f);
                vw = (int)(vh * 4.0f / 3.0f);
            }
            vx = (screenW - vw) / 2;
            vy = (screenH - vh) / 2; // Vertical middle of screen
        }
        else
        {
            // --- LANDSCAPE ORIENTATION ---
            float targetRatio = CurrentAspectRatio switch
            {
                AspectRatioMode.Widescreen_16_9 => 16.0f / 9.0f,
                AspectRatioMode.Stretch => (float)screenW / screenH,
                _ => 4.0f / 3.0f // AutoDevice & Original 4:3
            };

            if (CurrentAspectRatio == AspectRatioMode.Stretch)
            {
                vx = vy = 0;
                vw = screenW;
                vh = screenH;
            }
            else
            {
                float screenRatio = (float)screenW / screenH;
                if (screenRatio > targetRatio)
                {
                    vh = screenH;
                    vw = (int)(vh * targetRatio);
                    vx = (screenW - vw) / 2;
                    vy = 0;
                }
                else
                {
                    vw = screenW;
                    vh = (int)(vw / targetRatio);
                    vx = 0;
                    vy = (screenH - vh) / 2;
                }
            }
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)screenW, (uint)screenH);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        gl.Viewport(vx, vy, (uint)vw, (uint)vh);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.CullFace);

        gl.UseProgram(_directProg);
        gl.BindVertexArray(_directVao);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.Uniform1(_uDirectTex, 0);
        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        gl.BindVertexArray(0);
    }

    static void OnRender(double dt)
    {
        var gl = _gl!;
        if (_imgui != null)
        {
            try { _imgui.Update((float)dt); } catch { _imgui = null; }
        }

        if (PendingGpuBackendReset)
        {
            PendingGpuBackendReset = false;
            try
            {
                Hle.GlVram.Scale = ConfigManager.View.NativeResolution ? 1 : 4;
                _glBackend?.Dispose();
                _glBackend = (Hle.GlCore)Hle.GpuBackendFactory.Create(gl,
                    Hle.GpuBackendFactory.Parse(ConfigManager.View.GpuBackend));
                _glBackend.InitGl();
                Hle.GpuHle.Active = _glBackend.Ready;
                Hle.GpuHle.Backend = _glBackend;
                Hle.GpuHle.NativeResolution = ConfigManager.View.NativeResolution;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Host] GPU backend reset failed: {e.Message}");
            }
        }
    
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var fbDef = _window!.FramebufferSize;
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        Runtime.RamLog.Tick();
        Memory.RamLogger.TrackReads =
            PanelManager.Get<RamMapPanel>()?.IsOpen == true ||
            PanelManager.Get<MemoryEditorPanel>()?.IsOpen == true;

        var gpu = _gpu;
        if (gpu != null)
        {

            if (Hle.GpuHle.Active && _glBackend is { Ready: true } && gpu.DisplayEnabled)
            {
                var wf = _window!.FramebufferSize;
                var (tex, tw, th, aspect) = _glBackend.PresentDisplay(
                    gpu.DisplayX, gpu.DisplayY,
                    gpu.DisplayWidth, gpu.DisplayHeight,
                    gpu.Display24Bit,
                    outW: wf.X, outH: wf.Y);
                if (tex != 0) OutputPanel.SetTexture(tex, tw, th, aspect);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.Viewport(0, 0, (uint)wf.X, (uint)wf.Y);
            }
            else
            {
                UploadDisplayTexture(gl, gpu);
            }

            if (PanelManager.Get<VramViewerPanel>()?.IsOpen == true)
                UploadVramTexture(gl, gpu);
        }

        if (PanelManager.Get<RamMapPanel>()?.IsOpen == true)
        {
            QueueRamConvert();
            if (_ramReady) FlushRamTexture(gl);
        }

        if (_imgui != null)
        {
            if (!ConfigManager.View.HideTopBar)
                MainMenuBar.Draw();

            DrawDockspace();
            PanelManager.DrawPanels();
            MenuRegistry.DrawWindows();
            Modding.ModLoadingPopup.Draw();
            NoticePopup.Draw();
            if (StartupNotice.NeedsAck) StartupNotice.Draw();
#if ANDROID
            TouchControls.Draw();
#endif
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
            try { _imgui.Render(); } catch { _imgui = null; }
        }
        else
        {
            RenderDirectDisplay(gl, fbDef.X, fbDef.Y);
        }
    }

    static void DrawDockspace()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        const ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoDocking | 
                                           ImGuiWindowFlags.NoTitleBar |
                                           ImGuiWindowFlags.NoCollapse |
                                           ImGuiWindowFlags.NoResize |
                                           ImGuiWindowFlags.NoMove |
                                           ImGuiWindowFlags.NoBringToFrontOnFocus |
                                           ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("##DockHost", hostFlags);
        ImGui.PopStyleVar(3);
        uint dockId = ImGui.GetID("##MainDock");
        int openCount = PanelManager.Panels.Count(p => p.IsOpen && p is not IFloatingPanel);
        var dockFlags = openCount <= 1 ? (ImGuiDockNodeFlags)4096 : ImGuiDockNodeFlags.None;
        ImGui.DockSpace(dockId, Vector2.Zero, dockFlags);

        if (_layoutPending)
        {
            _layoutPending = false;
            DockBuilder.SetupCenterLayout(dockId, viewport.WorkSize, "Output");
        }

        ImGui.End();
    }

    static void OnClosing()
    {
        if (_closed) return;
        _closed = true;
        ConfigManager.SaveView(PanelManager.Panels);
        ConfigManager.SaveGame();
        PanelManager.Shutdown();
        _glBackend?.Dispose();
        _imgui?.Dispose();
        _gl?.DeleteTexture(_displayTex);
        _gl?.DeleteTexture(_vramTex);
        _gl?.DeleteTexture(_ramTex);
    }

    public static uint UploadPng(byte[] png)
    {
        try
        {
            var img = StbImageSharp.ImageResult.FromMemory(png, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (img == null || img.Width <= 0 || img.Height <= 0) return 0;

            int w = img.Width, h = img.Height;
            if (w == h) return UploadTexture(img.Data, w, h);

            int s = Math.Min(w, h);
            int ox = (w - s) / 2;
            int oy = (h - s) / 2;
            var square = new byte[s * s * 4];
            for (int y = 0; y < s; y++)
                Array.Copy(img.Data, ((oy + y) * w + ox) * 4, square, y * s * 4, s * 4);
            return UploadTexture(square, s, s);
        }
        catch { return 0; }
    }

    public static uint UploadTexture(byte[] rgba, int width, int height)
    {
        if (_gl == null || width <= 0 || height <= 0) return 0;
        int needed = width * height * 4;
        if (rgba.Length < needed) return 0;
        var tex = CreateTexture(_gl);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
            (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan(0, needed));
        return tex;
    }

    static uint CreateTexture(GL gl)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    static byte[] _rgbaDisplay = Array.Empty<byte>();

    static void UploadDisplayTexture(GL gl, Gpu gpu)
    {
        int w = gpu.DisplayWidth, h = gpu.DisplayHeight;
        if (!gpu.DisplayEnabled || w <= 0 || h <= 0) return;
        int neededRgb = w * h * 3;
        if (_rgbDisplay.Length < neededRgb) _rgbDisplay = new byte[neededRgb];
        ConvertDisplay(gpu, w, h);

        int neededRgba = w * h * 4;
        if (_rgbaDisplay.Length < neededRgba) _rgbaDisplay = new byte[neededRgba];

        for (int i = 0, j = 0; i < neededRgb; i += 3, j += 4)
        {
            _rgbaDisplay[j]     = _rgbDisplay[i];
            _rgbaDisplay[j + 1] = _rgbDisplay[i + 1];
            _rgbaDisplay[j + 2] = _rgbDisplay[i + 2];
            _rgbaDisplay[j + 3] = 255;
        }

        gl.BindTexture(TextureTarget.Texture2D, _displayTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)w, (uint)h, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _rgbaDisplay.AsSpan(0, neededRgba));
        OutputPanel.SetTexture(_displayTex, w, h);
    }

    static ushort[] _vramView = new ushort[Gpu.VramWidth * Gpu.VramHeight];
    static void UploadVramTexture(GL gl, Gpu gpu)
    {
        const int sz = Gpu.VramWidth * Gpu.VramHeight * 3;
        if (_rgbVram.Length < sz) _rgbVram = new byte[sz];
        ushort[] src;
        if (Hle.GpuHle.Active && _glBackend is { Ready: true })
        {
            _glBackend.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, _vramView);
            src = _vramView;
        }
        else src = gpu.Vram;
        ConvertVramToBuffer(src, _rgbVram);
        gl.BindTexture(TextureTarget.Texture2D, _vramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, Gpu.VramWidth, Gpu.VramHeight, 0, PixelFormat.Rgb, PixelType.UnsignedByte, _rgbVram.AsSpan(0, sz));
        VramViewerPanel.SetTexture(_vramTex, Gpu.VramWidth, Gpu.VramHeight);
    }

    static void QueueRamConvert()
    {
        if (_ramTask is { IsCompleted: false }) return;
        if (++_ramFrame < 6) return;
        _ramFrame = 0;
        var psMem = Runtime.Mem as Memory.PSMemory;
        if (psMem == null) return;
        var ram = psMem.RamBuffer;
        var back = _ramBack;
        _ramTask = Task.Run(() => Runtime.RamLog.BuildTexture(ram, back))
            .ContinueWith(_ =>
            {
                (_ramFront, _ramBack) = (_ramBack, _ramFront);
                _ramReady = true;
            }, TaskContinuationOptions.ExecuteSynchronously);
    }

    static void FlushRamTexture(GL gl)
    {
        _ramReady = false;
        gl.BindTexture(TextureTarget.Texture2D, _ramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
            Memory.RamLogger.Width, Memory.RamLogger.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _ramFront);
        RamMapPanel.SetTexture(_ramTex);
    }

    static void ConvertDisplay(Gpu gpu, int w, int h)
    {
        var vram = gpu.Vram;
        int dx = gpu.DisplayX, dy = gpu.DisplayY;
        int o = 0;
        if (gpu.Display24Bit)
        {
            for (int y = 0; y < h; y++)
            {
                int lineByte = ((dy + y) * Gpu.VramWidth + dx) * 2;
                for (int x = 0; x < w; x++)
                {
                    int bo = lineByte + x * 3;
                    _rgbDisplay[o++] = VramByte(vram, bo);
                    _rgbDisplay[o++] = VramByte(vram, bo + 1);
                    _rgbDisplay[o++] = VramByte(vram, bo + 2);
                }
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                int line = ((dy + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
                for (int x = 0; x < w; x++)
                {
                    ushort px = vram[line + ((dx + x) & (Gpu.VramWidth - 1))];
                    _rgbDisplay[o++] = (byte)((px & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 5) & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 10) & 0x1F) << 3);
                }
            }
        }
    }

    static void ConvertVramToBuffer(ushort[] vram, byte[] output)
    {
        int o = 0;
        for (int y = 0; y < Gpu.VramHeight; y++)
        for (int x = 0; x < Gpu.VramWidth; x++)
        {
            ushort px = vram[y * Gpu.VramWidth + x];
            output[o++] = (byte)((px & 0x1F) << 3);
            output[o++] = (byte)(((px >> 5) & 0x1F) << 3);
            output[o++] = (byte)(((px >> 10) & 0x1F) << 3);
        }
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }
}
