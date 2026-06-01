using System.Runtime.InteropServices;

namespace RockDeskAgent.Core;

/// <summary>
/// Lança processos na sessão interativa do usuário (Session N) a partir
/// do serviço (Session 0) — equivalente ao _run_in_user_session() do Python.
/// </summary>
public static class SessionHelper
{
    private static readonly ILogger Logger = AgentLogger.GetNamed(nameof(SessionHelper));

    // ── P/Invoke ──────────────────────────────────────────────────────
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll")] static extern bool WTSQueryUserToken(uint sid, out IntPtr token);
    [DllImport("userenv.dll")]  static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")]  static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int    cb, _0, _1, _2, _3, _4, _5, _6, _7, _8;
        public int    dwFlags;
        public short  wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpReserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUserW(
        IntPtr token, string? app, string? cmd,
        IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("advapi32.dll")] static extern bool DuplicateTokenEx(
        IntPtr tok, uint access, IntPtr attr, int level, int type, out IntPtr dup);

    const uint CREATE_NO_WINDOW          = 0x08000000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    /// <summary>
    /// Retorna o ID da sessão interativa ativa (onde o usuário está logado).
    /// </summary>
    public static uint GetInteractiveSessionId() => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Lança cmd na sessão interativa do usuário.
    /// Usa WTSQueryUserToken para obter o token do usuário logado.
    /// </summary>
    public static bool LaunchInUserSession(string cmd, bool waitForExit = false)
    {
        var sessionId = GetInteractiveSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            Logger.LogWarning("LaunchInUserSession: sem sessão interativa ativa.");
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            Logger.LogWarning("WTSQueryUserToken falhou (err={E})", Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            if (!DuplicateTokenEx(userToken, 0x10000000, IntPtr.Zero, 2, 1, out var dupToken))
            {
                Logger.LogWarning("DuplicateTokenEx falhou (err={E})", Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                CreateEnvironmentBlock(out var env, dupToken, false);
                try
                {
                    var si = new STARTUPINFO
                    {
                        cb        = Marshal.SizeOf<STARTUPINFO>(),
                        lpDesktop = "WinSta0\\Default",
                    };

                    bool ok = CreateProcessAsUserW(
                        dupToken, null, cmd,
                        IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                        env, null, ref si, out var pi);

                    if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);

                    if (!ok)
                    {
                        Logger.LogWarning("CreateProcessAsUserW falhou (err={E})", Marshal.GetLastWin32Error());
                        return false;
                    }

                    Logger.LogInformation("Processo lançado em Session {S}: {Cmd}", sessionId, cmd[..Math.Min(60, cmd.Length)]);

                    if (waitForExit)
                        WaitForSingleObject(pi.hProcess, 30_000);

                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                    return true;
                }
                finally { CloseHandle(dupToken); }
            }
            finally { CloseHandle(userToken); }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("LaunchInUserSession erro: {E}", ex.Message);
            return false;
        }
    }
}
