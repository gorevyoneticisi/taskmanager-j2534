using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TaskmanagerOBD2Reader
{
    internal class MainWindow : Form
    {
        // Connection bar
        private Label     lblVoltage;
        private Label     lblVin;
        private Button    btnConnect;
        private Button    btnDisconnect;
        private Label     lblConnStatus;
        private Panel     ledConn;

        // Live data tab
        private DataGridView gridPids;

        // Fault codes tab
        private ListView  listDtc;
        private Button    btnReadDtc;
        private Button    btnClearDtc;
        private Label     lblDtcStatus;

        // State
        private ObdSession _session;
        private Thread     _pollThread;
        private volatile bool _polling;
        private readonly object _sessionLock = new object();

        // Colors
        private static readonly Color BG       = Color.FromArgb(28, 28, 28);
        private static readonly Color BG2      = Color.FromArgb(40, 40, 40);
        private static readonly Color BG3      = Color.FromArgb(55, 55, 55);
        private static readonly Color ACCENT   = Color.FromArgb(0, 188, 212);
        private static readonly Color FG       = Color.WhiteSmoke;
        private static readonly Color FG_DIM   = Color.FromArgb(140, 140, 140);
        private static readonly Color GREEN    = Color.FromArgb(76, 175, 80);
        private static readonly Color ORANGE   = Color.FromArgb(255, 152, 0);
        private static readonly Color RED      = Color.FromArgb(244, 67, 54);

        public MainWindow()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            Text            = "Taskmanager OBD2 Reader";
            Size            = new Size(700, 560);
            MinimumSize     = new Size(700, 560);
            BackColor       = BG;
            ForeColor       = FG;
            Font            = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterScreen;

            // Header
            var lblTitle = new Label
            {
                Text     = "Taskmanager OBD2 Reader",
                Location = new Point(14, 14),
                Size     = new Size(400, 26),
                Font     = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = ACCENT
            };
            var lblSub = new Label
            {
                Text      = "Live data + fault codes via STM32 J2534 bridge",
                Location  = new Point(16, 40),
                Size      = new Size(400, 16),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                ForeColor = FG_DIM
            };

            // Connection bar
            ledConn = new Panel
            {
                Location  = new Point(14, 70),
                Size      = new Size(12, 12),
                BackColor = BG3
            };
            lblConnStatus = new Label
            {
                Text      = "Disconnected",
                Location  = new Point(32, 68),
                Size      = new Size(150, 18),
                ForeColor = FG_DIM
            };
            lblVoltage = new Label
            {
                Text      = "Battery: --",
                Location  = new Point(195, 68),
                Size      = new Size(120, 18),
                ForeColor = FG_DIM
            };
            lblVin = new Label
            {
                Text      = "VIN: --",
                Location  = new Point(325, 68),
                Size      = new Size(220, 18),
                ForeColor = FG_DIM,
                Font      = new Font("Segoe UI", 7.5f)
            };
            btnConnect = Btn("Connect", 560, 62, 80, 28, Color.FromArgb(0, 120, 140));
            btnConnect.Click += OnConnectClick;
            btnDisconnect = Btn("Disconnect", 560, 62, 80, 28, BG3);
            btnDisconnect.Visible = false;
            btnDisconnect.Click += OnDisconnectClick;

            var sep = new Panel { Location = new Point(0, 98), Size = new Size(700, 1), BackColor = BG3 };
            sep.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // Tab control
            var tabs = new TabControl
            {
                Location  = new Point(10, 108),
                Size      = new Size(664, 398),
                Anchor    = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            tabs.DrawMode   = TabDrawMode.OwnerDrawFixed;
            tabs.DrawItem  += DrawTabHeader;
            tabs.BackColor  = BG;
            tabs.Font       = new Font("Segoe UI", 9f);

            var tabLive  = new TabPage("  Live Data  ") { BackColor = BG, ForeColor = FG };
            var tabDtc   = new TabPage("  Fault Codes  ") { BackColor = BG, ForeColor = FG };

            BuildLiveDataTab(tabLive);
            BuildDtcTab(tabDtc);

            tabs.TabPages.Add(tabLive);
            tabs.TabPages.Add(tabDtc);

            Controls.AddRange(new Control[]
            {
                lblTitle, lblSub,
                ledConn, lblConnStatus, lblVoltage, lblVin,
                btnConnect, btnDisconnect,
                sep, tabs
            });

            FormClosing += (s, e) => StopPolling();
        }

        private void BuildLiveDataTab(TabPage tab)
        {
            gridPids = new DataGridView
            {
                Location            = new Point(6, 8),
                Anchor              = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Size                = new Size(650, 348),
                BackgroundColor     = BG2,
                GridColor           = BG3,
                DefaultCellStyle    = { BackColor = BG2, ForeColor = FG, SelectionBackColor = BG3, SelectionForeColor = FG },
                ColumnHeadersDefaultCellStyle = { BackColor = BG3, ForeColor = ACCENT, Font = new Font("Segoe UI", 9f, FontStyle.Bold) },
                RowHeadersVisible   = false,
                AllowUserToAddRows  = false,
                AllowUserToDeleteRows = false,
                ReadOnly            = true,
                SelectionMode       = DataGridViewSelectionMode.FullRowSelect,
                BorderStyle         = BorderStyle.None,
                CellBorderStyle     = DataGridViewCellBorderStyle.SingleHorizontal,
                EnableHeadersVisualStyles = false
            };
            gridPids.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sensor", HeaderText = "Sensor",    Width = 200, SortMode = DataGridViewColumnSortMode.NotSortable });
            gridPids.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value",  HeaderText = "Value",     Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = { Font = new Font("Consolas", 10f, FontStyle.Bold) } });
            gridPids.Columns.Add(new DataGridViewTextBoxColumn { Name = "Unit",   HeaderText = "Unit",      Width = 80,  SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = { ForeColor = FG_DIM } });
            gridPids.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bar",    HeaderText = "Level",     Width = 200, SortMode = DataGridViewColumnSortMode.NotSortable });

            foreach (var pid in PidDecoder.Pids)
                gridPids.Rows.Add(pid.Name, "--", pid.Unit, "");

            tab.Controls.Add(gridPids);
        }

        private void BuildDtcTab(TabPage tab)
        {
            listDtc = new ListView
            {
                Location     = new Point(6, 8),
                Size         = new Size(650, 268),
                Anchor       = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor    = BG2,
                ForeColor    = FG,
                View         = View.Details,
                FullRowSelect = true,
                GridLines    = false,
                BorderStyle  = BorderStyle.None,
                Font         = new Font("Segoe UI", 9f)
            };
            listDtc.Columns.Add("Code",        90);
            listDtc.Columns.Add("Description", 540);

            btnReadDtc = Btn("Read Fault Codes", 6, 284, 150, 30, Color.FromArgb(0, 120, 140));
            btnReadDtc.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnReadDtc.Click += OnReadDtcClick;

            btnClearDtc = Btn("Clear Fault Codes", 164, 284, 150, 30, Color.FromArgb(150, 50, 50));
            btnClearDtc.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnClearDtc.Click += OnClearDtcClick;

            lblDtcStatus = new Label
            {
                Location  = new Point(6, 322),
                Size      = new Size(650, 18),
                ForeColor = FG_DIM,
                Anchor    = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            tab.Controls.AddRange(new Control[] { listDtc, btnReadDtc, btnClearDtc, lblDtcStatus });
        }

        // ── Event handlers ──────────────────────────────────────────────────────

        private void OnConnectClick(object sender, EventArgs e)
        {
            try
            {
                var s = new ObdSession();
                s.Open();
                lock (_sessionLock) { _session = s; }

                SetConnected(true);
                TryReadVinAndVoltage();
                StartPolling();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection failed:\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnDisconnectClick(object sender, EventArgs e)
        {
            StopPolling();
            lock (_sessionLock)
            {
                _session?.Dispose();
                _session = null;
            }
            SetConnected(false);
            ClearGrid();
        }

        private void OnReadDtcClick(object sender, EventArgs e)
        {
            ObdSession s;
            lock (_sessionLock) { s = _session; }
            if (s == null) { lblDtcStatus.Text = "Not connected."; return; }

            // Stop the poll thread before touching the session — concurrent J2534
            // calls on the same channel from two threads corrupt request/response pairing.
            bool wasPolling = _polling;
            if (wasPolling) StopPolling();

            listDtc.Items.Clear();
            List<string> dtcs;
            try
            {
                dtcs = s.ReadDtcs();
            }
            catch (Exception ex)
            {
                lblDtcStatus.Text = $"Error: {ex.Message}";
                if (wasPolling) StartPolling();
                return;
            }
            finally
            {
                if (wasPolling) StartPolling();
            }

            if (dtcs.Count == 0)
            {
                lblDtcStatus.ForeColor = GREEN;
                lblDtcStatus.Text = "No fault codes stored.";
                return;
            }

            foreach (string code in dtcs)
            {
                var item = new ListViewItem(code) { ForeColor = ORANGE };
                item.SubItems.Add(PidDecoder.DescribeDtc(code));
                listDtc.Items.Add(item);
            }
            lblDtcStatus.ForeColor = ORANGE;
            lblDtcStatus.Text = $"{dtcs.Count} fault code(s) found.";
        }

        private void OnClearDtcClick(object sender, EventArgs e)
        {
            ObdSession s;
            lock (_sessionLock) { s = _session; }
            if (s == null) { lblDtcStatus.Text = "Not connected."; return; }

            if (MessageBox.Show("Clear all stored fault codes?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            bool wasPolling = _polling;
            if (wasPolling) StopPolling();

            bool ok;
            try
            {
                ok = s.ClearDtcs();
            }
            catch (Exception ex)
            {
                lblDtcStatus.Text = $"Error: {ex.Message}";
                if (wasPolling) StartPolling();
                return;
            }
            finally
            {
                if (wasPolling) StartPolling();
            }

            listDtc.Items.Clear();
            lblDtcStatus.ForeColor = ok ? GREEN : RED;
            lblDtcStatus.Text = ok ? "Fault codes cleared." : "Clear command failed.";
        }

        // ── Polling ─────────────────────────────────────────────────────────────

        private void StartPolling()
        {
            _polling = true;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "OBD2_Poll" };
            _pollThread.Start();
        }

        private void StopPolling()
        {
            _polling = false;
            _pollThread?.Join(1500);
            _pollThread = null;
        }

        private void PollLoop()
        {
            while (_polling)
            {
                ObdSession s;
                lock (_sessionLock) { s = _session; }
                if (s == null) break;

                var values = new double?[PidDecoder.Pids.Length];
                for (int i = 0; i < PidDecoder.Pids.Length && _polling; i++)
                {
                    var pid = PidDecoder.Pids[i];
                    if (s.RequestPid(pid.Pid, out byte a, out byte b))
                        values[i] = pid.Decode(a, b);
                }

                if (!_polling) break;

                try
                {
                    double v = s.ReadBatteryVoltage();
                    BeginInvoke((Action)(() => lblVoltage.Text = v > 0 ? $"Battery: {v:F1} V" : "Battery: --"));
                }
                catch { }

                BeginInvoke((Action)(() => UpdateGrid(values)));

                Thread.Sleep(250);
            }
        }

        private void UpdateGrid(double?[] values)
        {
            for (int i = 0; i < PidDecoder.Pids.Length && i < gridPids.Rows.Count; i++)
            {
                var pid = PidDecoder.Pids[i];
                var row = gridPids.Rows[i];
                if (values[i].HasValue)
                {
                    double val = values[i].Value;
                    string txt = pid.Format(val);
                    row.Cells["Value"].Value      = txt;
                    row.Cells["Bar"].Value        = BuildBar(pid.Pid, val);
                    row.Cells["Value"].Style.ForeColor = ValueColor(pid.Pid, val);
                }
                else
                {
                    row.Cells["Value"].Value = "--";
                    row.Cells["Bar"].Value   = "";
                }
            }
        }

        private static string BuildBar(byte pid, double val)
        {
            double max = pid switch
            {
                0x0C => 8000,   // RPM
                0x0D => 260,    // Speed km/h
                0x05 => 150,    // Coolant
                0x11 => 100,    // Throttle %
                0x04 => 100,    // Load %
                0x2F => 100,    // Fuel %
                _    => 100
            };
            double pct = Math.Max(0, Math.Min(1, val / max));
            int bars = (int)(pct * 20);
            return new string('#', bars) + new string('.', 20 - bars);
        }

        private Color ValueColor(byte pid, double val)
        {
            switch (pid)
            {
                case 0x05:
                    if (val > 110) return RED;
                    if (val > 95)  return ORANGE;
                    if (val < 60)  return Color.CornflowerBlue;
                    return GREEN;
                case 0x0D:
                    if (val > 200) return RED;
                    if (val > 130) return ORANGE;
                    return FG;
                case 0x0C:
                    if (val > 6500) return RED;
                    if (val > 5000) return ORANGE;
                    return FG;
                default:
                    return FG;
            }
        }

        private void ClearGrid()
        {
            foreach (DataGridViewRow row in gridPids.Rows)
            {
                row.Cells["Value"].Value = "--";
                row.Cells["Bar"].Value   = "";
                row.Cells["Value"].Style.ForeColor = FG;
            }
        }

        private void TryReadVinAndVoltage()
        {
            // Run on a background thread — VIN read blocks up to 1500 ms and
            // must not freeze the UI. Poll loop hasn't started yet so no race.
            new Thread(() =>
            {
                ObdSession s;
                lock (_sessionLock) { s = _session; }
                if (s == null) return;
                try
                {
                    string vin = s.ReadVin();
                    if (!string.IsNullOrEmpty(vin))
                        BeginInvoke((Action)(() => lblVin.Text = $"VIN: {vin}"));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        // ── UI helpers ──────────────────────────────────────────────────────────

        private void SetConnected(bool connected)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => SetConnected(connected))); return; }
            btnConnect.Visible    = !connected;
            btnDisconnect.Visible =  connected;
            ledConn.BackColor     = connected ? GREEN : BG3;
            lblConnStatus.ForeColor = connected ? GREEN : FG_DIM;
            lblConnStatus.Text    = connected ? "Connected" : "Disconnected";
            if (!connected)
            {
                lblVoltage.Text = "Battery: --";
                lblVin.Text     = "VIN: --";
            }
        }

        private void DrawTabHeader(object sender, DrawItemEventArgs e)
        {
            var tc = (TabControl)sender;
            bool selected = e.Index == tc.SelectedIndex;
            e.Graphics.FillRectangle(new SolidBrush(selected ? BG2 : BG3), e.Bounds);
            e.Graphics.DrawString(tc.TabPages[e.Index].Text, Font,
                new SolidBrush(selected ? ACCENT : FG_DIM), e.Bounds.X + 4, e.Bounds.Y + 5);
        }

        private Button Btn(string text, int x, int y, int w, int h, Color bg)
        {
            var b = new Button
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = FG,
                Cursor    = Cursors.Hand,
                Font      = new Font("Segoe UI", 8.5f)
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            return b;
        }
    }
}
