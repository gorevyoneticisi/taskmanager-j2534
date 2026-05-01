using System;
using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;

namespace TaskmanagerBridge
{
    /// <summary>
    /// Configuration dialog shown when PassThruOpen is called.
    /// Built entirely in code, no .designer file needed in a DLL project.
    /// Must be shown on an STA thread (PassThruAPI.ShowConfig handles this).
    /// </summary>
    internal class ConfigDialog : Form
    {
        private ComboBox cmbPort;
        private Button btnRefresh;
        private ComboBox cmbBaud;
        private RadioButton radStd;
        private RadioButton radExt;
        private CheckBox chkRemember;
        private Button btnConnect;
        private Button btnCancel;
        private Label lblStatus;
        private Panel picLed;

        public ConfigDialog()
        {
            BuildUI();
            PopulatePorts();
            RestoreSettings();
        }

        private void BuildUI()
        {
            Text = "Taskmanager J2534 Bridge: Configuration";
            Size = new Size(430, 350);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(28, 28, 28);
            ForeColor = Color.WhiteSmoke;
            Font = new Font("Segoe UI", 9f);

            // Header
            var lblHeader = new Label
            {
                Text = "STM32 J2534 Bridge",
                Location = new Point(14, 14),
                Size = new Size(380, 28),
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 188, 212)
            };

            var lblSub = new Label
            {
                Text = "Toyota Techstream · VW ODIS · Ford IDS · BMW ISTA · Any J2534 app",
                Location = new Point(14, 42),
                Size = new Size(390, 16),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(110, 110, 110)
            };

            // Separator
            var sep1 = new Panel { Location = new Point(14, 65), Size = new Size(390, 1), BackColor = Color.FromArgb(55, 55, 55) };

            // COM Port
            var lblPort = L("COM Port:", 14, 80);
            cmbPort = new ComboBox
            {
                Location = new Point(110, 77),
                Size = new Size(180, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.WhiteSmoke,
                FlatStyle = FlatStyle.Flat
            };
            btnRefresh = new Button
            {
                Text = "↺",
                Location = new Point(298, 77),
                Size = new Size(30, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Segoe UI", 11f),
                Cursor = Cursors.Hand
            };
            btnRefresh.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            btnRefresh.Click += (s, e) => PopulatePorts();

            // CAN Speed
            var lblBaud = L("CAN Speed:", 14, 116);
            cmbBaud = new ComboBox
            {
                Location = new Point(110, 113),
                Size = new Size(220, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.WhiteSmoke,
                FlatStyle = FlatStyle.Flat
            };
            cmbBaud.Items.AddRange(new object[]
            {
                "500 kbps  - Standard OBD2 / VW / Toyota",
                "250 kbps  - GM, older CAN networks",
                "1000 kbps - High speed / CAN FD"
            });
            cmbBaud.SelectedIndex = 0;

            // CAN ID mode
            var lblId = L("CAN ID Mode:", 14, 152);
            radStd = new RadioButton { Text = "11-bit Standard", Location = new Point(110, 150), Size = new Size(130, 20), Checked = true, ForeColor = Color.WhiteSmoke };
            radExt = new RadioButton { Text = "29-bit Extended", Location = new Point(250, 150), Size = new Size(130, 20), ForeColor = Color.WhiteSmoke };

            // Remember
            chkRemember = new CheckBox
            {
                Text = "Remember these settings (skip dialog next time)",
                Location = new Point(14, 182),
                Size = new Size(340, 20),
                ForeColor = Color.FromArgb(150, 150, 150)
            };

            // Separator
            var sep2 = new Panel { Location = new Point(14, 210), Size = new Size(390, 1), BackColor = Color.FromArgb(55, 55, 55) };

            // Status LED
            picLed = new Panel
            {
                Location = new Point(14, 222),
                Size = new Size(12, 12),
                BackColor = Color.FromArgb(80, 80, 80)
            };

            lblStatus = new Label
            {
                Text = "Ready. Select your FT232H COM port and click Connect.",
                Location = new Point(34, 220),
                Size = new Size(370, 18),
                ForeColor = Color.Silver
            };

            // Buttons
            btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(216, 278),
                Size = new Size(90, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.WhiteSmoke,
                Cursor = Cursors.Hand
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            btnConnect = new Button
            {
                Text = "Connect",
                Location = new Point(316, 278),
                Size = new Size(90, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 140),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnConnect.FlatAppearance.BorderColor = Color.FromArgb(0, 160, 180);
            btnConnect.Click += OnConnectClicked;

            Controls.AddRange(new Control[]
            {
                lblHeader, lblSub, sep1,
                lblPort, cmbPort, btnRefresh,
                lblBaud, cmbBaud,
                lblId, radStd, radExt,
                chkRemember, sep2,
                picLed, lblStatus,
                btnCancel, btnConnect
            });

            AcceptButton = btnConnect;
            CancelButton = btnCancel;
        }

        private void PopulatePorts()
        {
            string previous = cmbPort.SelectedItem?.ToString();
            cmbPort.Items.Clear();

            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports, StringComparer.OrdinalIgnoreCase);

            if (ports.Length == 0)
            {
                cmbPort.Items.Add("No ports found");
                cmbPort.SelectedIndex = 0;
                SetStatus("No COM ports detected. Check USB connection.", Color.OrangeRed);
                return;
            }

            foreach (string p in ports) cmbPort.Items.Add(p);

            if (previous != null && cmbPort.Items.Contains(previous))
                cmbPort.SelectedItem = previous;
            else if (cmbPort.Items.Contains(BridgeConfig.ComPort))
                cmbPort.SelectedItem = BridgeConfig.ComPort;
            else
                cmbPort.SelectedIndex = 0;

            SetStatus($"{ports.Length} port(s) found. FT232H is usually the highest COM number.", Color.Silver);
        }

        private void RestoreSettings()
        {
            switch (BridgeConfig.CanBaudRate)
            {
                case 250000: cmbBaud.SelectedIndex = 1; break;
                case 1000000: cmbBaud.SelectedIndex = 2; break;
                default: cmbBaud.SelectedIndex = 0; break;
            }
            radExt.Checked = BridgeConfig.UseExtendedId;
            radStd.Checked = !BridgeConfig.UseExtendedId;
            chkRemember.Checked = BridgeConfig.RememberSettings;
        }

        private void OnConnectClicked(object sender, EventArgs e)
        {
            string port = cmbPort.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(port) || port == "No ports found")
            {
                SetStatus("Please select a valid COM port.", Color.OrangeRed);
                return;
            }

            SetStatus($"Testing {port}...", Color.Yellow);
            btnConnect.Enabled = false;
            Application.DoEvents();

            try
            {
                using (var test = new SerialPort(port, 921600, Parity.None, 8, StopBits.One))
                {
                    test.Open();
                    // Port opened successfully; close immediately, PassThruAPI will open it for real
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Cannot open {port}: {ex.Message}", Color.OrangeRed);
                btnConnect.Enabled = true;
                return;
            }

            // Persist settings
            BridgeConfig.ComPort = port;
            BridgeConfig.RememberSettings = chkRemember.Checked;
            BridgeConfig.UseExtendedId = radExt.Checked;
            switch (cmbBaud.SelectedIndex)
            {
                case 1: BridgeConfig.CanBaudRate = 250000; break;
                case 2: BridgeConfig.CanBaudRate = 1000000; break;
                default: BridgeConfig.CanBaudRate = 500000; break;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void SetStatus(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;
            picLed.BackColor = color;
        }

        private Label L(string text, int x, int y) => new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(95, 20),
            ForeColor = Color.WhiteSmoke
        };
    }
}