namespace LlmHostMonitor;

public sealed class AddMachineForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _llm = new();
    private readonly TextBox _metrics = new();

    public MachineConfig Result { get; private set; } = new();

    public AddMachineForm(MachineConfig? existing = null)
    {
        Text = existing is null ? "Add machine" : "Edit machine";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 200);
        BackColor = Color.FromArgb(16, 22, 32);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 9.5f);

        void LabelAt(string text, int y)
        {
            Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                Location = new Point(16, y + 3),
                ForeColor = Color.FromArgb(160, 175, 195),
            });
        }

        LabelAt("Name", 18);
        StyleBox(_name, 110, 16, 320);
        _name.Text = existing?.Name ?? "";

        LabelAt("LLM base", 58);
        StyleBox(_llm, 110, 56, 320);
        _llm.Text = existing?.LlmBaseUrl ?? "http://hostname:8080";

        LabelAt("GPU metrics", 98);
        StyleBox(_metrics, 110, 96, 320);
        _metrics.Text = existing?.MetricsUrl ?? "http://hostname:9835/metrics";

        var ok = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 30),
            Location = new Point(250, 148),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 120, 90),
            ForeColor = Color.White,
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) ||
                string.IsNullOrWhiteSpace(_llm.Text) ||
                string.IsNullOrWhiteSpace(_metrics.Text))
            {
                MessageBox.Show(this, "Name, LLM base, and GPU metrics URL are required.", Text);
                DialogResult = DialogResult.None;
                return;
            }
            Result = new MachineConfig
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N")[..8],
                Name = _name.Text.Trim(),
                LlmBaseUrl = _llm.Text.Trim().TrimEnd('/'),
                MetricsUrl = _metrics.Text.Trim(),
                Enabled = existing?.Enabled ?? true,
            };
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(90, 30),
            Location = new Point(350, 148),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 48, 62),
            ForeColor = Color.WhiteSmoke,
        };
        cancel.FlatAppearance.BorderSize = 0;

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void StyleBox(TextBox box, int x, int y, int w)
    {
        box.Location = new Point(x, y);
        box.Width = w;
        box.BackColor = Color.FromArgb(10, 14, 20);
        box.ForeColor = Color.WhiteSmoke;
        box.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(box);
    }
}
