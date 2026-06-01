using System.Runtime.InteropServices;

namespace RockDeskAgent.Remote;

/// <summary>Injeção de mouse, teclado e SendSAS via Windows API.</summary>
public static class InputInjector
{
    #region P/Invoke

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inp, int sz);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("sas.dll",    EntryPoint = "SendSAS")]
    static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool fKeyboardInitiated);

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT  { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT  { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT   mi;
        [FieldOffset(0)] public KEYBDINPUT   ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }

    const uint INPUT_MOUSE    = 0;
    const uint INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE       = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    const uint MOUSEEVENTF_MIDDOWN    = 0x0020;
    const uint MOUSEEVENTF_MIDUP      = 0x0040;
    const uint MOUSEEVENTF_WHEEL      = 0x0800;
    const uint MOUSEEVENTF_ABSOLUTE   = 0x8000;
    const uint KEYEVENTF_KEYUP        = 0x0002;
    const uint KEYEVENTF_EXTENDEDKEY  = 0x0001;

    #endregion

    private static readonly ILogger Logger = AgentLogger.GetNamed(nameof(InputInjector));

    public static void MouseMove(int x, int y, int screenW, int screenH)
    {
        if (screenW <= 0 || screenH <= 0) return;
        int ax = (int)((x / (double)screenW) * 65535);
        int ay = (int)((y / (double)screenH) * 65535);
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            u = { mi = new MOUSEINPUT
            {
                dx = ax, dy = ay,
                flags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE
            }}
        });
    }

    // Mapeamento: protocolo 1=left, 2=right, 3=middle
    public static void MouseDown(int x, int y, int btn, int screenW, int screenH)
    {
        MouseMove(x, y, screenW, screenH);
        uint flag = btn switch { 2 => MOUSEEVENTF_RIGHTDOWN, 3 => MOUSEEVENTF_MIDDOWN,
                                  _ => MOUSEEVENTF_LEFTDOWN };
        int ax = (int)((x / (double)screenW) * 65535);
        int ay = (int)((y / (double)screenH) * 65535);
        Send(new INPUT { type = INPUT_MOUSE,
            u = { mi = new MOUSEINPUT { dx = ax, dy = ay,
                flags = MOUSEEVENTF_ABSOLUTE | flag }} });
    }

    public static void MouseUp(int x, int y, int btn, int screenW, int screenH)
    {
        uint flag = btn switch { 2 => MOUSEEVENTF_RIGHTUP, 3 => MOUSEEVENTF_MIDUP,
                                  _ => MOUSEEVENTF_LEFTUP };
        int ax = (int)((x / (double)screenW) * 65535);
        int ay = (int)((y / (double)screenH) * 65535);
        Send(new INPUT { type = INPUT_MOUSE,
            u = { mi = new MOUSEINPUT { dx = ax, dy = ay,
                flags = MOUSEEVENTF_ABSOLUTE | flag }} });
    }

    public static void MouseScroll(int delta)
    {
        uint d = (uint)(delta * 120);
        Send(new INPUT { type = INPUT_MOUSE,
            u = { mi = new MOUSEINPUT { data = d, flags = MOUSEEVENTF_WHEEL } } });
    }

    public static void KeyDown(int vk)
    {
        Send(new INPUT { type = INPUT_KEYBOARD,
            u = { ki = new KEYBDINPUT { vk = (ushort)vk } } });
    }

    public static void KeyUp(int vk)
    {
        Send(new INPUT { type = INPUT_KEYBOARD,
            u = { ki = new KEYBDINPUT { vk = (ushort)vk, flags = KEYEVENTF_KEYUP } } });
    }

    /// <summary>
    /// Simula Ctrl+Alt+Del.
    /// O subprocess roda com token SYSTEM do winlogon (SeTcbPrivilege) →
    /// SendSAS(TRUE) funciona diretamente a partir do desktop de input ativo.
    /// Também cria sas.trigger como fallback para o serviço SCM.
    /// </summary>
    public static bool TrySendSAS()
    {
        // 1. Garante que o thread está no input desktop antes de chamar SendSAS
        var desktopBefore = ScreenCapture.SwitchToInputDesktop();
        Logger.LogInformation("TrySendSAS: desktop antes='{D}'", desktopBefore);

        // 2. Chama SendSAS(TRUE) — com token SYSTEM do winlogon, SeTcbPrivilege ativo
        bool sent = false;
        try
        {
            SendSAS(true);
            Logger.LogInformation("SendSAS(TRUE) executado.");
            sent = true;
        }
        catch (Exception ex) { Logger.LogWarning("SendSAS(TRUE): {E}", ex.Message); }

        // 3. Polling rápido por troca de desktop (100ms por 3s)
        // A tela de segurança do Windows aparece no Winlogon desktop.
        // Detectamos a mudança imediatamente para o CaptureLoop trocar.
        if (sent)
        {
            Task.Run(() =>
            {
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(100);
                    var current = ScreenCapture.GetInputDesktopName();
                    if (current != desktopBefore && current != "Unknown")
                    {
                        Logger.LogInformation("Desktop mudou após SAS: '{B}'→'{C}'", desktopBefore, current);
                        DesktopChangedAfterSas?.Invoke(current);
                        break;
                    }
                }
            });
            return true;
        }

        // 4. Fallback: trigger file → serviço SCM chama SendSAS
        try
        {
            var triggerFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RockDeskAgent", "sas.trigger");
            Directory.CreateDirectory(Path.GetDirectoryName(triggerFile)!);
            File.WriteAllText(triggerFile, "1");
            return true;
        }
        catch { return false; }
    }

    /// <summary>Disparado quando o desktop muda após SendSAS.</summary>
    public static event Action<string>? DesktopChangedAfterSas;

    /// <summary>
    /// Abre o Gerenciador de Tarefas na sessão do usuário.
    /// Definitivamente visível no viewer pois usa BitBlt normal.
    /// Complementa o SendSAS (Windows Security overlay não é capturável pelo BitBlt).
    /// </summary>
    public static bool TryOpenTaskManager()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "taskmgr.exe",
                UseShellExecute = true,
                CreateNoWindow  = false,
            });
            Logger.LogInformation("Task Manager aberto.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("TryOpenTaskManager: {E}", ex.Message);
            return false;
        }
    }

    private static void Send(INPUT inp) =>
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
}
