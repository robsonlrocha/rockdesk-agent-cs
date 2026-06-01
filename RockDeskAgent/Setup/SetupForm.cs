using RockDeskAgent.Api;
using RockDeskAgent.Config;

namespace RockDeskAgent.Setup;

/// <summary>Wizard de registro do agente — equivalente ao run_setup() do Python.</summary>
public class SetupForm : Form
{
    private TextBox _codeBox = null!;
    private Button  _btnReg  = null!;
    private Label   _status  = null!;

    public SetupForm()
    {
        Text            = "RockDesk Agent — Registro";
        Size            = new Size(420, 240);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.FromArgb(248, 247, 250);

        var lbl = new Label
        {
            Text      = "Código de registro:",
            Location  = new Point(20, 30),
            Size      = new Size(370, 20),
            Font      = new Font("Segoe UI", 10)
        };

        _codeBox = new TextBox
        {
            Location    = new Point(20, 55),
            Size        = new Size(370, 30),
            Font        = new Font("Courier New", 13),
            TextAlign   = HorizontalAlignment.Center,
            CharacterCasing = CharacterCasing.Upper
        };

        _btnReg = new Button
        {
            Text      = "Registrar",
            Location  = new Point(130, 105),
            Size      = new Size(150, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(105, 108, 255),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        _btnReg.FlatAppearance.BorderSize = 0;
        _btnReg.Click += OnRegister;

        _status = new Label
        {
            Text      = "",
            Location  = new Point(20, 155),
            Size      = new Size(370, 40),
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.Red,
            TextAlign = ContentAlignment.MiddleCenter
        };

        Controls.AddRange(new Control[] { lbl, _codeBox, _btnReg, _status });
        _codeBox.KeyPress += (_, e) => { if (e.KeyChar == '\r') OnRegister(null, null!); };
    }

    private async void OnRegister(object? _, EventArgs __)
    {
        var code = _codeBox.Text.Trim().ToUpper();
        if (code.Length < 4) { _status.Text = "Código inválido."; return; }

        _btnReg.Enabled = false;
        _status.ForeColor = Color.DimGray;
        _status.Text = "Verificando...";

        try
        {
            var cfg = AgentConfig.Load();
            var api = new PortalClient(cfg);
            var hostname = Environment.MachineName;
            var r = await api.VerifyCodeAsync(code, hostname);

            if (r?["success"]?.GetValue<bool>() == true)
            {
                cfg.DeviceKey = r["device_key"]?.GetValue<string>() ?? "";
                cfg.DeviceId  = r["device_id"]?.GetValue<int>()     ?? 0;
                cfg.Hostname  = hostname;
                cfg.Save();

                _status.ForeColor = Color.Green;
                _status.Text = "✓ Registrado com sucesso!";

                // Pergunta se quer instalar o serviço
                await Task.Delay(800);
                if (MessageBox.Show("Instalar como serviço Windows?", "RockDesk Agent",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    var exe = Environment.ProcessPath ?? "";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName  = exe, Arguments = "install",
                        Verb      = "runas",
                        UseShellExecute = true
                    });
                }
                Close();
            }
            else
            {
                _status.ForeColor = Color.Red;
                _status.Text = r?["error"]?.GetValue<string>() ?? "Código inválido ou expirado.";
            }
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Red;
            _status.Text = $"Erro: {ex.Message}";
        }
        finally { _btnReg.Enabled = true; }
    }
}
