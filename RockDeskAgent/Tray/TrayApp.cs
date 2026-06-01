using System.Diagnostics;
using System.ServiceProcess;
using RockDeskAgent.Config;

namespace RockDeskAgent.Tray;

/// <summary>
/// Ícone na bandeja do sistema — igual ao agente Python.
/// Menu de contexto: Abrir | Verificar Atualização | Sair
/// </summary>
public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir",              null, (_, _) => ShowStatusWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Verificar Atualização", null, (_, _) => CheckUpdate());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair",               null, (_, _) => Exit());

        _tray = new NotifyIcon
        {
            Icon             = SystemIcons.Application,
            Text             = $"RockDesk Agent CS v{AgentConfig.AgentVersion}",
            Visible          = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowStatusWindow();

        // Mostra a janela de status automaticamente ao iniciar
        ShowStatusWindow();
    }

    private static void ShowStatusWindow()
    {
        // Evita abrir múltiplas instâncias da janela
        foreach (Form f in Application.OpenForms)
            if (f is StatusForm) { f.Focus(); return; }
        new StatusForm().Show();
    }

    private static void CheckUpdate()
    {
        // Abre o portal no browser para download manual da nova versão
        Process.Start(new ProcessStartInfo(AgentConfig.PortalDownloadUrlCS) { UseShellExecute = true });
    }

    private void Exit()
    {
        _tray.Visible = false;
        Application.Exit();
    }
}

/// <summary>
/// Janela de status — idêntica à do agente Python:
/// versão, hostname, device key, status do serviço, botões.
/// </summary>
public class StatusForm : Form
{
    public StatusForm()
    {
        var cfg = AgentConfig.Load();
        Text            = "RockDesk Agent";
        Size            = new Size(480, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.FromArgb(245, 245, 249);

        // ── Header ──────────────────────────────────────────────────────
        var header = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 70,
            BackColor = Color.FromArgb(105, 108, 255),
        };
        var lblTitle = new Label
        {
            Text      = "RockDesk Agent",
            Font      = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.White,
            Location  = new Point(16, 10),
            AutoSize  = true,
        };
        var lblVer = new Label
        {
            Text      = $"v{AgentConfig.AgentVersion}-cs",
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(200, 210, 255),
            Location  = new Point(18, 44),
            AutoSize  = true,
        };
        header.Controls.AddRange(new Control[] { lblTitle, lblVer });

        // ── Info grid ──────────────────────────────────────────────────
        int y = 90;
        void Row(string label, string value, Color? color = null)
        {
            Controls.Add(new Label
            {
                Text      = label,
                Font      = new Font("Segoe UI", 9, FontStyle.Bold),
                Location  = new Point(24, y),
                Size      = new Size(140, 22),
                ForeColor = Color.FromArgb(80, 80, 100),
            });
            Controls.Add(new Label
            {
                Text      = value,
                Font      = new Font("Segoe UI", 9),
                Location  = new Point(170, y),
                Size      = new Size(280, 22),
                ForeColor = color ?? Color.FromArgb(30, 30, 50),
            });
            y += 28;
        }

        var dk = cfg.DeviceKey;
        var dkShow = dk.Length > 8 ? dk[..8] + "…" : (dk.Length > 0 ? dk : "—");
        var svcStatus = GetServiceStatus();
        var svcColor  = svcStatus.StartsWith("Rodando") ? Color.Green : Color.Red;

        Row("Versão instalada:", $"v{AgentConfig.AgentVersion}-cs");
        Row("Hostname:",         cfg.Hostname.Length > 0 ? cfg.Hostname : Environment.MachineName);
        Row("Device Key:",       dkShow);
        Row("Status do serviço:", svcStatus, svcColor);

        // ── Botões ─────────────────────────────────────────────────────
        var btnUpdate = new Button
        {
            Text      = "Verificar Atualização",
            Location  = new Point(24, y + 30),
            Size      = new Size(160, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(105, 108, 255),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9),
        };
        btnUpdate.FlatAppearance.BorderSize = 0;
        btnUpdate.Click += (_, _) =>
            Process.Start(new ProcessStartInfo(AgentConfig.PortalDownloadUrlCS)
                { UseShellExecute = true });

        var btnLogs = new Button
        {
            Text      = "Ver Logs",
            Location  = new Point(196, y + 30),
            Size      = new Size(100, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(120, 120, 140),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9),
        };
        btnLogs.FlatAppearance.BorderSize = 0;
        btnLogs.Click += (_, _) =>
        {
            if (File.Exists(AgentConfig.LogFile))
                Process.Start(new ProcessStartInfo("notepad.exe", AgentConfig.LogFile)
                    { UseShellExecute = true });
        };

        var btnClose = new Button
        {
            Text      = "Fechar",
            Location  = new Point(308, y + 30),
            Size      = new Size(100, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(220, 220, 230),
            ForeColor = Color.FromArgb(50, 50, 70),
            Font      = new Font("Segoe UI", 9),
        };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.Click += (_, _) => Close();

        Controls.Add(header);
        Controls.AddRange(new Control[] { btnUpdate, btnLogs, btnClose });
    }

    private static string GetServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(AgentConfig.SvcName);
            return sc.Status == ServiceControllerStatus.Running
                ? "● Serviço rodando"
                : $"● {sc.Status}";
        }
        catch { return "Serviço não instalado"; }
    }
}
