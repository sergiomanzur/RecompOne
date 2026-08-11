using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Host;

public static class TouchControls
{
    public static bool Enabled = true;

    public static void Draw()
    {
        if (!Enabled) return;
        try
        {
            // Set style for transparent controls
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.3f, 0.4f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.5f, 0.5f, 0.6f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.7f, 0.7f, 0.8f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 30f); // Make buttons round

        var vp = ImGui.GetMainViewport();
        var size = vp.Size;

        // Transparent window for touch overlay
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        if (ImGui.Begin("##TouchOverlay", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            float btnSize = 65f * HostWindow.DpiScale;
            
            // D-Pad left side
            float dx = 60f * HostWindow.DpiScale;
            float dy = size.Y - 220f * HostWindow.DpiScale;

            // Up
            ImGui.SetCursorPos(new Vector2(dx + btnSize, dy));
            ImGui.Button("U", new Vector2(btnSize, btnSize));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Up);

            // Left
            ImGui.SetCursorPos(new Vector2(dx, dy + btnSize));
            ImGui.Button("L", new Vector2(btnSize, btnSize));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Left);

            // Right
            ImGui.SetCursorPos(new Vector2(dx + btnSize * 2, dy + btnSize));
            ImGui.Button("R", new Vector2(btnSize, btnSize));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Right);

            // Down
            ImGui.SetCursorPos(new Vector2(dx + btnSize, dy + btnSize * 2));
            ImGui.Button("D", new Vector2(btnSize, btnSize));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Down);

            // Action Buttons right side
            float ax = size.X - 240f * HostWindow.DpiScale;
            float ay = size.Y - 220f * HostWindow.DpiScale;

            // Triangle (Up)
            ImGui.SetCursorPos(new Vector2(ax + btnSize, ay));
            ImGui.Button("Y", new Vector2(btnSize, btnSize));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Triangle);

            // Square (Left)
            ImGui.SetCursorPos(new Vector2(ax, ay + btnSize));
            ImGui.Button("X", new Vector2(btnSize, btnSize));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Square);

            // Circle (Right)
            ImGui.SetCursorPos(new Vector2(ax + btnSize * 2, ay + btnSize));
            ImGui.Button("B", new Vector2(btnSize, btnSize));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Circle);

            // Cross (Down)
            ImGui.SetCursorPos(new Vector2(ax + btnSize, ay + btnSize * 2));
            ImGui.Button("A", new Vector2(btnSize, btnSize));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Cross);

            // Shoulder buttons L1, R1, L2, R2
            float shY = 40f * HostWindow.DpiScale;
            float shW = 100f * HostWindow.DpiScale;
            float shH = 50f * HostWindow.DpiScale;

            // L1
            ImGui.SetCursorPos(new Vector2(20f * HostWindow.DpiScale, shY));
            ImGui.Button("L1", new Vector2(shW, shH));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.L1);

            // L2
            ImGui.SetCursorPos(new Vector2(20f * HostWindow.DpiScale, shY + shH + 10f));
            ImGui.Button("L2", new Vector2(shW, shH));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.L2);

            // R1
            ImGui.SetCursorPos(new Vector2(size.X - shW - 20f * HostWindow.DpiScale, shY));
            ImGui.Button("R1", new Vector2(shW, shH));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.R1);

            // R2
            ImGui.SetCursorPos(new Vector2(size.X - shW - 20f * HostWindow.DpiScale, shY + shH + 10f));
            ImGui.Button("R2", new Vector2(shW, shH));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.R2);

            // Select & Start in the center bottom
            float cW = 80f * HostWindow.DpiScale;
            float cH = 40f * HostWindow.DpiScale;
            float cX = (size.X - cW * 2 - 20f) * 0.5f;
            float cY = size.Y - 60f * HostWindow.DpiScale;

            ImGui.SetCursorPos(new Vector2(cX, cY));
            ImGui.Button("Select", new Vector2(cW, cH));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Select);

            ImGui.SetCursorPos(new Vector2(cX + cW + 20f, cY));
            ImGui.Button("Start", new Vector2(cW, cH));
            if (ImGui.IsItemActive())
                Controller.State &= unchecked((ushort)~Controller.Start);
        }
        ImGui.End();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
        }
        catch { }
    }
}
