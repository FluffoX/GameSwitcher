using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using static GameSwitcher.Strings;

namespace GameSwitcher.Gui
{
    // Play time per day or month (stacked by game) and per game, from the "Days" entries in playtime.json
    class StatsDialog : Form
    {
        // Categorical colors that read well on light and dark backgrounds; the rest is gray
        static readonly Color[] Palette =
        {
            Color.FromArgb(78, 121, 167), Color.FromArgb(242, 142, 43), Color.FromArgb(89, 161, 79),
            Color.FromArgb(225, 87, 89), Color.FromArgb(118, 183, 178), Color.FromArgb(237, 201, 72)
        };
        static readonly Color OtherColor = Color.FromArgb(150, 150, 150);

        readonly Dictionary<string, (string Name, Dictionary<DateTime, double> Days)> games = new Dictionary<string, (string, Dictionary<DateTime, double>)>();
        readonly bool dark;
        readonly ComboBox cmbRange = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Chart chart = new Chart { Dock = DockStyle.Fill };
        readonly ListView table = new ListView { Dock = DockStyle.Right, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        readonly Label lblTotal = new Label { AutoSize = true }, lblHint = new Label { AutoSize = true, Tag = "keepColor", ForeColor = Color.Gray };

        public StatsDialog(Form owner, Dictionary<string, Dictionary<string, object>> playtime, bool dark)
        {
            this.dark = dark;
            Font = owner.Font; Icon = owner.Icon;
            Text = T("stats.title");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false;
            ClientSize = new Size(LogicalToDeviceUnits(980), LogicalToDeviceUnits(520));
            MinimumSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(360));

            foreach (var g in playtime ?? new Dictionary<string, Dictionary<string, object>>())
            {
                var days = new Dictionary<DateTime, double>();
                if (g.Value.Dict("Days") is Dictionary<string, object> d)
                    foreach (var day in d)
                        if (DateTime.TryParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                            days[date] = d.Double(day.Key);
                if (days.Count > 0) games[g.Key] = (g.Value.Str("Name"), days);
            }

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
            var lblRange = new Label { AutoSize = true, Text = T("stats.range"), Margin = new Padding(3, 7, 3, 3) };
            cmbRange.Width = LogicalToDeviceUnits(200);
            foreach (string r in new[] { "7", "30", "365" }) cmbRange.Items.Add(new Item { Code = r, Name = T("stats.range" + r) });
            cmbRange.SelectedIndex = 0;
            lblTotal.Margin = new Padding(24, 7, 3, 3);
            lblHint.Margin = new Padding(24, 7, 3, 3);
            top.Controls.AddRange(new Control[] { lblRange, cmbRange, lblTotal, lblHint });

            table.Width = LogicalToDeviceUnits(330);
            table.Columns.Add(T("col.game"), LogicalToDeviceUnits(185));
            table.Columns.Add(T("col.playtime"), LogicalToDeviceUnits(115), HorizontalAlignment.Right);

            var area = new ChartArea("main");
            chart.ChartAreas.Add(area);
            chart.Legends.Add(new Legend("legend") { Docking = Docking.Bottom, Alignment = StringAlignment.Center });
            area.AxisX.MajorGrid.Enabled = false;
            area.AxisX.Interval = 1;
            area.AxisY.Title = T("stats.hours");
            area.AxisY.LabelStyle.Format = "0.#";

            Controls.AddRange(new Control[] { chart, table, top });
            cmbRange.SelectedIndexChanged += (s, e) => Fill();
            Shown += (s, e) => { Theme.Apply(this, dark, new ContextMenuStrip[0]); ApplyChartColors(); };
            Fill();
        }

        void ApplyChartColors()
        {
            Color back = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
            Color text = dark ? Color.FromArgb(235, 235, 235) : SystemColors.ControlText;
            Color grid = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);
            chart.BackColor = back;
            var area = chart.ChartAreas[0];
            area.BackColor = back;
            foreach (var axis in new[] { area.AxisX, area.AxisY })
            {
                axis.LineColor = grid; axis.MajorGrid.LineColor = grid; axis.MajorTickMark.LineColor = grid;
                axis.LabelStyle.ForeColor = text; axis.TitleForeColor = text;
                axis.LabelStyle.Font = Font; axis.TitleFont = Font;
            }
            chart.Legends[0].BackColor = back; chart.Legends[0].ForeColor = text; chart.Legends[0].Font = Font;
        }

        void Fill()
        {
            int range = int.Parse(((Item)cmbRange.SelectedItem).Code);
            bool monthly = range > 31;
            DateTime today = DateTime.Today;
            DateTime first = monthly ? new DateTime(today.Year, today.Month, 1).AddMonths(-11) : today.AddDays(-(range - 1));
            var buckets = new List<DateTime>();
            for (DateTime d = first; d <= today; d = monthly ? d.AddMonths(1) : d.AddDays(1)) buckets.Add(d);
            DateTime Bucket(DateTime day) => monthly ? new DateTime(day.Year, day.Month, 1) : day;

            // seconds per game in the range, largest first; the six largest get their own color
            var totals = games.Select(g => (g.Key, g.Value.Name, Seconds: g.Value.Days.Where(d => d.Key >= first && d.Key <= today).Sum(d => d.Value)))
                              .Where(g => g.Seconds >= 60).OrderByDescending(g => g.Seconds).ToList();
            var groups = totals.Take(Palette.Length).Select((g, i) => (Name: g.Name, Keys: new List<string> { g.Key }, Color: Palette[i])).ToList();
            if (totals.Count > Palette.Length)
                groups.Add((T("stats.other"), totals.Skip(Palette.Length).Select(g => g.Key).ToList(), OtherColor));

            chart.Series.Clear();
            foreach (var g in groups)
            {
                var series = new Series(g.Name) { ChartType = SeriesChartType.StackedColumn, ChartArea = "main", Legend = "legend", Color = g.Color };
                series["PointWidth"] = "0.7";
                foreach (DateTime b in buckets)
                {
                    double secs = g.Keys.Sum(k => games[k].Days.Where(d => d.Key >= first && d.Key <= today && Bucket(d.Key) == b).Sum(d => d.Value));
                    string label = monthly ? b.ToString("MMM yy", Culture) : range > 7 ? b.ToString(DayPattern, Culture) : b.ToString("ddd ", Culture) + b.ToString(DayPattern, Culture);
                    int i = series.Points.AddXY(label, Math.Round(secs / 3600, 2));
                    series.Points[i].ToolTip = g.Name + ": " + FormatDuration((long)secs);
                }
                chart.Series.Add(series);
            }
            chart.ChartAreas[0].AxisX.Interval = range == 30 ? 3 : 1;

            table.BeginUpdate();
            table.Items.Clear();
            foreach (var g in totals)
            {
                var item = new ListViewItem(g.Name);
                item.SubItems.Add(FormatDuration((long)g.Seconds));
                table.Items.Add(item);
            }
            table.EndUpdate();
            double sum = totals.Sum(g => g.Seconds);
            lblTotal.Text = T("stats.total", FormatDuration((long)sum));
            lblHint.Text = games.Count == 0 ? T("stats.noData") : "";
        }

        // Dates in the language of the window; the short date without the year, e.g. dd.MM or M/d
        static CultureInfo Culture => GetCulture();
        static CultureInfo GetCulture() { try { return CultureInfo.GetCultureInfo(Code); } catch { return CultureInfo.CurrentCulture; } }
        static string DayPattern => System.Text.RegularExpressions.Regex.Replace(Culture.DateTimeFormat.ShortDatePattern, @"[^dM]*y+[^dM]*", "");

        static string FormatDuration(long seconds)
        {
            long h = seconds / 3600, m = seconds % 3600 / 60;
            return h > 0 ? T("time.hm", h, m) : T("time.m", m);
        }
    }
}
