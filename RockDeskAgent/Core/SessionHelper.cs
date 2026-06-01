using System.Runtime.InteropServices;

namespace RockDeskAgent.Core;

/// <summary>
/// Lança processos na sessão interativa do usuário (Session N) a partir
/// do serviço (Session 0) — equivalente ao _run_in_user_session() do Python.
///
/// Estratégia:
/// 1. Tenta token SYSTEM do winlogon.exe da sessão N → acessa Winlogon desktop,
///    captura tela bloqueada e permite SendSAS(TRUE).
/// 2. Fallback: token do usuário via WTSQueryUserToken (tela desbloqueada).
/// </summary>
public static class SessionHelper
{
    private static readonly ILogger Logger = AgentLogger.GetNamed(nameof(SessionHelper));

    // ── P/Invoke ──────────────────────────────────────────────────────
    [DllImport("kernel32.dll")] static extern uint   WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll")] static extern bool   WTSQueryUserToken(uint sid, out IntPtr token);
    [DllImport("userenv.dll")]  static extern bool   CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")]  static extern bool   DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll")] static extern bool   CloseHandle(IntPtr h);
    [DllImport("advapi32.dll")] static extern bool   OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool   DuplicateTokenEx(IntPtr tok, uint access,
                                                         IntPtr attr, int level, int type, out IntPtr dup);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool   ProcessIdToSessionId(uint pid, out uint sid);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W pe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W pe);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int    cb, _0, _1, _2, _3, _4, _5, _6, _7, _8, dwFlags;
        public short  wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpReserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUserW(IntPtr token, string? app, string? cmd,
        IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir,
        ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    const uint TH32CS_SNAPPROCESS         = 0x00000002;
    const uint PROCESS_QUERY_INFORMATION  = 0x0400;
    const uint TOKEN_DUPLICATE            = 0x0002;
    const uint TOKEN_QUERY                = 0x0008;
    const uint TOKEN_ASSIGN_PRIMARY       = 0x0001;
    const uint CREATE_NO_WINDOW           = 0x08000000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    /// <summary>Retorna o ID da sessão interativa ativa.</summary>
    public static uint GetInteractiveSessionId() => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Obtém token SYSTEM duplicado do winlogon.exe na sessão alvo.
    /// SYSTEM tem SeTcbPrivilege: pode acessar Winlogon desktop e chamar SendSAS.
    /// </summary>
    public static IntPtr GetWinlogonToken(uint sessionId)
    {
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return IntPtr.Zero;

        uint targetPid = 0;
        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref pe); ok; ok = Process32NextW(snap, ref pe))
            {
                if (!pe.szExeFile.Equals("winlogon.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ProcessIdToSessionId(pe.th32ProcessID, out uint sid)) continue;
                if (sid == sessionId) { targetPid = pe.th32ProcessID; break; }
            }
        }
        finally { CloseHandle(snap); }

        if (targetPid == 0) { Logger.LogDebug("winlogon.exe não encontrado na sessão {S}", sessionId); return IntPtr.Zero; }

        var hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, targetPid);
        if (hProc == IntPtr.Zero) { Logger.LogDebug("OpenProcess(winlogon) falhou"); return IntPtr.Zero; }

        try
        {
            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out var hTok))
            { Logger.LogDebug("OpenProcessToken(winlogon) falhou"); return IntPtr.Zero; }
            try
            {
                if (!DuplicateTokenEx(hTok, 0x10000000, IntPtr.Zero, 2, 1, out var dup))
                { Logger.LogDebug("DuplicateTokenEx(winlogon) falhou"); return IntPtr.Zero; }
                Logger.LogDebug("Token SYSTEM obtido de winlogon PID={P} Session={S}", targetPid, sessionId);
                return dup;
            }
            finally { CloseHandle(hTok); }
        }
        finally { CloseHandle(hProc); }
    }

    /// <summary>
    /// Lança cmd na sessão interativa do usuário.
    /// Usa token SYSTEM do winlogon (1ª tentativa) ou token do usuário (fallback).
    /// O token SYSTEM permite: capturar tela bloqueada + SendSAS.
    /// </summary>
    public static bool LaunchInUserSession(string cmd)
    {
        var sessionId = GetInteractiveSessionId();
        if (sessionId is 0 or 0xFFFFFFFF)
        {
            Logger.LogWarning("LaunchInUserSession: nenhuma sessão interativa ativa.");
            return false;
        }

        // Tenta token SYSTEM do winlogon primeiro (acesso total incluindo Winlogon desktop)
        var token = GetWinlogonToken(sessionId);
        var tokenSource = "SYSTEM (winlogon)";

        if (token == IntPtr.Zero)
        {
            // Fallback: token do usuário logado
            if (!WTSQueryUserToken(sessionId, out token))
            {
                Logger.LogWarning("WTSQueryUserToken e winlogon token falharam. Sessão {S}", sessionId);
                return false;
            }
            tokenSource = "usuário (WTSQueryUserToken)";

            // Para o token do usuário, precisa duplicar como primary
            if (!DuplicateTokenEx(token, 0x10000000, IntPtr.Zero, 2, 1, out var dup))
            {
                CloseHandle(token);
                Logger.LogWarning("DuplicateTokenEx(usuário) falhou");
                return false;
            }
            CloseHandle(token);
            token = dup;
        }

        try
        {
            CreateEnvironmentBlock(out var env, token, false);
            try
            {
                // Detecta o desktop ativo para lançar no lugar certo
                var desktopName = GetInputDesktopName();
                var lpDesktop   = $"WinSta0\\{desktopName}";

                var si = new STARTUPINFO
                {
                    cb        = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop = lpDesktop,
                };

                bool ok = CreateProcessAsUserW(token, null, cmd,
                    IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                    env, null, ref si, out var pi);

                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);

                if (!ok)
                {
                    Logger.LogWarning("CreateProcessAsUserW falhou (err={E})", Marshal.GetLastWin32Error());
                    return false;
                }
                Logger.LogInformation("Processo lançado: Session={S} Token={T} Desktop={D} PID={P}",
                    sessionId, tokenSource, lpDesktop, pi.dwProcessId);
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("LaunchInUserSession: {E}", ex.Message);
                return false;
            }
        }
        finally { CloseHandle(token); }
    }

    // ── Helpers ───────────────────────────────────────────────────────
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint f, bool i, uint a);
    [DllImport("user32.dll")] static extern bool   CloseDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr OpenWindowStationW(string n, bool i, uint a);
    [DllImport("user32.dll")] static extern bool   SetProcessWindowStation(IntPtr h);
    [DllImport("user32.dll")] static extern bool   CloseWindowStation(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr o, uint cls, [Out] char[] buf, uint len, ref uint needed);

    public static string GetInputDesktopName()
    {
        try
        {
            // Garante que o processo está em WinSta0
            var hWS = OpenWindowStationW("WinSta0", false, 0x037F);
            if (hWS != IntPtr.Zero) { SetProcessWindowStation(hWS); CloseWindowStation(hWS); }

            var hDesk = OpenInputDesktop(0, false, 0x01FF);
            if (hDesk == IntPtr.Zero) return "Default";
            var buf = new char[256]; uint len = 0;
            GetUserObjectInformationW(hDesk, 2, buf, (uint)(buf.Length * 2), ref len);
            CloseDesktop(hDesk);
            return new string(buf, 0, (int)(len / 2)).TrimEnd('\0');
        }
        catch { return "Default"; }
    }
}
