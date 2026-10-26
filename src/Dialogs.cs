using System;
using System.Drawing;
using System.Windows.Forms;

namespace NexLimit
{
    class ThemedDialog : Form
    {
        protected readonly TableLayoutPanel Body = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Padding = new Padding(16), Dock = DockStyle.Fill };
        protected readonly FlowLayoutPanel Buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };

        public ThemedDialog(string title)
        {
            Text = title; Font = Theme.UiFont; Icon = Theme.AppIcon; BackColor = Theme.Bg;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.None; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Controls.Add(Body);
        }

        protected Button AddButton(string text, DialogResult r, bool accent = false)
        {
            var b = new Button { Text = text, DialogResult = r, AutoSize = true, MinimumSize = new Size(Theme.Px(88), 0), Tag = accent ? "accent" : null };
            Buttons.Controls.Add(b); return b;
        }

        protected void Finish()
        {
            Body.Controls.Add(Buttons, 0, Body.RowCount); Body.SetColumnSpan(Buttons, 3);
            Theme.Apply(this);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.TitleBar(this); }
        protected static Label L(string t, string role = null) { return new Label { Text = t, AutoSize = true, Margin = new Padding(3, 7, 6, 3), Tag = role }; }
    }

    /// <summary>Exact speed limit in KB/s.</summary>
    sealed class LimitDialog : ThemedDialog
    {
        readonly NumericUpDown value = new NumericUpDown { Minimum = 1, Maximum = 10000000, Width = Theme.Px(110), ThousandsSeparator = true };
        public long Bps { get { return (long)value.Value * 1024; } }

        public LimitDialog(string what, long current) : base(what)
        {
            value.Value = Math.Max(1, current > 0 ? current / 1024 : 512);
            Body.Controls.Add(L("Limit to"), 0, 0); Body.Controls.Add(value, 1, 0); Body.Controls.Add(L("KB/s   (1024 KB/s = 1 MB/s)", "muted"), 2, 0);
            Body.RowCount = 1;
            AcceptButton = AddButton("Set limit", DialogResult.OK, true); CancelButton = AddButton("Cancel", DialogResult.Cancel);
            Finish();
        }
    }

    /// <summary>Per-app quota for one direction. Yes = set, No = remove, Retry = reset usage.</summary>
    sealed class QuotaDialog : ThemedDialog
    {
        readonly NumericUpDown amount = new NumericUpDown { Minimum = 1, Maximum = 1000000, Width = Theme.Px(90), ThousandsSeparator = true };
        readonly ComboBox unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.Px(60) };
        readonly ComboBox period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.Px(120) };
        public long Bytes { get { return (long)amount.Value << (unit.SelectedIndex == 1 ? 30 : 20); } }
        public bool Monthly { get { return period.SelectedIndex == 1; } }

        public QuotaDialog(string app, bool upload, long current, bool monthly, long used, DateTime periodStart) : base((upload ? "Upload" : "Download") + " quota for " + app)
        {
            unit.Items.AddRange(new object[] { "MB", "GB" }); period.Items.AddRange(new object[] { "per day", "per month" });
            bool gb = current == 0 || current % (1L << 30) == 0;
            unit.SelectedIndex = gb ? 1 : 0; amount.Value = current > 0 ? Math.Max(1, Math.Min(1000000, current >> (gb ? 30 : 20))) : 1;
            period.SelectedIndex = monthly ? 1 : 0;
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row.Controls.AddRange(new Control[] { amount, unit, period });
            Body.Controls.Add(L("Allow"), 0, 0); Body.Controls.Add(row, 1, 0); Body.SetColumnSpan(row, 2);
            var info = L("Used so far" + (monthly ? " since " + periodStart.ToString("d MMM") : " today") + ": " + Fmt.Size(used) + ".\n"
                + "When it's used up, NexLimit blocks this app's " + (upload ? "uploads" : "downloads") + " until the period ends.\n"
                + "The period (day or month) is shared by the app's download and upload quota.", "muted");
            info.MaximumSize = new Size(Theme.Px(420), 0);
            Body.Controls.Add(info, 0, 1); Body.SetColumnSpan(info, 3); Body.RowCount = 2;
            AcceptButton = AddButton("Set quota", DialogResult.Yes, true);
            CancelButton = AddButton("Cancel", DialogResult.Cancel);
            if (current > 0) { AddButton("Remove quota", DialogResult.No); AddButton("Reset usage", DialogResult.Retry); }
            Finish();
        }
    }

    /// <summary>Shown when the PC quota blocks the internet. Yes = allow for today, Retry = reset quota, OK = keep blocked.</summary>
    sealed class QuotaAlert : ThemedDialog
    {
        public QuotaAlert(string which, long limit) : base("NexLimit: data quota used up")
        {
            TopMost = true; StartPosition = FormStartPosition.CenterScreen;
            var head = new Label { Text = "Internet blocked", Font = Theme.Title, AutoSize = true, Tag = "bad" };
            var text = L("Your " + which + " data quota of " + Fmt.Size(limit) + " is used up, so NexLimit has blocked internet access. "
                + "Your local network still works.\n\nYou can allow internet for the rest of today, reset the quota, or leave it blocked.");
            text.MaximumSize = new Size(Theme.Px(440), 0);
            Body.Controls.Add(head, 0, 0); Body.SetColumnSpan(head, 3);
            Body.Controls.Add(text, 0, 1); Body.SetColumnSpan(text, 3); Body.RowCount = 2;
            var keep = AddButton("Keep blocked", DialogResult.OK);
            AddButton("Reset quota", DialogResult.Retry);
            AddButton("Allow for today", DialogResult.Yes, true);
            AcceptButton = keep; CancelButton = keep;
            Finish();
        }
    }
}
