using RockDeskAgent.Config;

namespace RockDeskAgent.Tray;

/// <summary>Ícone na bandeja do sistema.</summary>
public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("RockDesk Agent CS v" + AgentConfig.AgentVersion)
                  .Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Status do Serviço", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => { _tray.Visible = false; Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon    = SystemIcons.Application,
            Text    = "RockDesk Agent CS",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowStatus();
    }

    private static void ShowStatus()
    {
        var cfg = AgentConfig.Load();
        MessageBox.Show(
            $"Versão: {AgentConfig.AgentVersion}-cs\n" +
            $"Hostname: {cfg.Hostname}\n" +
            $"Device Key: {(cfg.DeviceKey.Length > 8 ? cfg.DeviceKey[..8] + "…" : cfg.DeviceKey)}\n" +
            $"Log: {AgentConfig.LogFile}",
            "RockDesk Agent CS", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
