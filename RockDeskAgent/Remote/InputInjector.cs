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
    /// Sinaliza Ctrl+Alt+Del:
    /// 1. Cria arquivo sas.trigger — o SERVIÇO (SCM) monitora e chama SendSAS(TRUE)
    ///    (subprocess não é SCM service, então SendSAS direto seria ignorado)
    /// 2. Tenta SendSAS diretamente como fallback (pode funcionar com SasGeneration=1)
    /// </summary>
    public static bool TrySendSAS()
    {
        var triggerFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RockDeskAgent", "sas.trigger");

        // Método 1: file trigger → serviço SCM chama SendSAS (confiável)
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(triggerFile)!);
            File.WriteAllText(triggerFile, "1");
            Logger.LogInformation("SAS trigger criado → serviço enviará SendSAS.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("SAS trigger falhou: {E}", ex.Message);
        }

        // Método 2: tentativa direta (funciona se SasGeneration=1 no registro)
        try
        {
            SendSAS(true);
            Logger.LogInformation("SendSAS(TRUE) direto OK.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug("SendSAS direto: {E}", ex.Message);
            return true; // trigger foi criado, serviço vai enviar
        }
    }

    private static void Send(INPUT inp) =>
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
}
