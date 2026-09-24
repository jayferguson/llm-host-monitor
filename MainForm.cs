namespace LlmHostMonitor;

public sealed class MainForm : Form
{
    private readonly Panel _list;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _timer;
    private AppConfig _config;
    private readonly Dictionary<string, MachineCard> _cards = new();
    private int _generation;
    private bool _busy;
    private static readonly Font IconFont = new(PickIconFamily(), 11f);

    public MainForm()
    {
        Text = "LLM Host Monitor";
        MinimumSize = new Size(420, 180);
        Size = new Size(640, 280);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(8, 11, 16);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(14, 18, 26),
            Padding = new Padding(8, 6, 8, 6),
        };

        var tips = new ToolTip { AutoPopDelay = 4000, InitialDelay = 400, ReshowDelay = 200 };

        var addBtn = MakeIconBtn("\uE710", 8, Color.FromArgb(62, 207, 142));
        tips.SetToolTip(addBtn, "Add machine");
        addBtn.Click += (_, _) => AddMachine();
        var editBtn = MakeIconBtn("\uE70F", 44, Color.WhiteSmoke);
        tips.SetToolTip(editBtn, "Edit selected");
        editBtn.Click += (_, _) => EditSelected();
        var removeBtn = MakeIconBtn("\uE74D", 80, Color.FromArgb(230, 120, 120));
        tips.SetToolTip(removeBtn, "Remove selected");
        removeBtn.Click += (_, _) => RemoveSelected();
        var refreshBtn = MakeIconBtn("\uE72C", 116, Color.FromArgb(140, 190, 230));
        tips.SetToolTip(refreshBtn, "Refresh now");
        refreshBtn.Click += async (_, _) => await PollOnceAsync();

        _status = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(140, 160, 180),
            Location = new Point(160, 12),
            Text = "starting.",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        toolbar.Controls.Add(addBtn);
        toolbar.Controls.Add(editBtn);
        toolbar.Controls.Add(removeBtn);
        toolbar.Controls.Add(refreshBtn);
        toolbar.Controls.Add(_status);
        toolbar.Resize += (_, _) =>
        {
            _status.Left = Math.Max(160, toolbar.Width - _status.PreferredWidth - 12);
        };

        _list = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(8),
            BackColor = Color.FromArgb(8, 11, 16),
        };
        _list.Resize += (_, _) => ResizeCards();

        Controls.Add(_list);
        Controls.Add(toolbar);

        _config = MachineStore.Load();
        RebuildCards();

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(2, _config.PollSeconds) * 1000 };
        _timer.Tick += async (_, _) => await PollOnceAsync();
        Shown += async (_, _) =>
        {
            await PollOnceAsync();
            _timer.Start();
        };
        FormClosed += (_, _) => _timer.Stop();
    }

    private static string PickIconFamily()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            using var probe = new Font(name, 11f);
            if (string.Equals(probe.FontFamily.Name, name, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return "Segoe UI Symbol";
    }

    private static Button MakeIconBtn(string glyph, int x, Color glyphColor)
    {
        var b = new Button
        {
            Text = glyph,
            Font = IconFont,
            Location = new Point(x, 6),
            Size = new Size(32, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(32, 42, 58),
            ForeColor = glyphColor,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 62, 84);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(24, 32, 44);
        return b;
    }

    private void RebuildCards()
    {
        _list.SuspendLayout();
        _list.Controls.Clear();
        _cards.Clear();
        foreach (var m in _config.Machines.Where(x => x.Enabled).Reverse())
        {
            var card = new MachineCard(m)
            {
                Dock = DockStyle.Top,
                Height = 72,
                Margin = new Padding(0, 0, 0, 6),
            };
            card.Click += (_, _) => SelectCard(card);
            foreach (Control c in card.Controls) c.Click += (_, _) => SelectCard(card);
            _cards[m.Id] = card;
            _list.Controls.Add(card);
        }
        _list.ResumeLayout(true);
        ResizeCards();
    }

    private MachineCard? _selected;

    private void SelectCard(MachineCard card)
    {
        _selected = card;
        foreach (var c in _cards.Values)
            c.Padding = new Padding(8, 6, 8, 6);
        card.Padding = new Padding(7, 5, 7, 5);
    }

    private void ResizeCards()
    {
        var inner = Math.Max(280, _list.ClientSize.Width - _list.Padding.Horizontal);
        foreach (Control c in _list.Controls)
        {
            if (c is MachineCard)
                c.Width = inner;
        }
    }

    private void AddMachine()
    {
        using var dlg = new AddMachineForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.Machines.Add(dlg.Result);
        MachineStore.Save(_config);
        RebuildCards();
        _ = PollOnceAsync();
    }

    private void EditSelected()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, "Select a machine card first.", Text);
            return;
        }
        var existing = _config.Machines.FirstOrDefault(m => m.Id == _selected.MachineId);
        if (existing is null) return;
        using var dlg = new AddMachineForm(existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var i = _config.Machines.FindIndex(m => m.Id == existing.Id);
        _config.Machines[i] = dlg.Result;
        MachineStore.Save(_config);
        RebuildCards();
        _ = PollOnceAsync();
    }

    private void RemoveSelected()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, "Select a machine card first.", Text);
            return;
        }
        var existing = _config.Machines.FirstOrDefault(m => m.Id == _selected.MachineId);
        if (existing is null) return;
        if (MessageBox.Show(this, $"Remove {existing.Name}?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        _config.Machines.RemoveAll(m => m.Id == existing.Id);
        MachineStore.Save(_config);
        _selected = null;
        RebuildCards();
    }

    private async Task PollOnceAsync()
    {
        if (_busy) return;
        _busy = true;
        var gen = ++_generation;
        try
        {
            var machines = _config.Machines.Where(m => m.Enabled).ToList();
            var tasks = machines.Select(async m =>
            {
                try { return await MetricsClient.ProbeAsync(m, CancellationToken.None); }
                catch (Exception ex)
                {
                    return new HostStatus
                    {
                        MachineId = m.Id,
                        Name = m.Name,
                        Error = ex.GetBaseException().Message,
                        CheckedAt = DateTimeOffset.Now,
                    };
                }
            });
            var results = await Task.WhenAll(tasks);
            if (gen != _generation) return;
            foreach (var st in results)
            {
                if (_cards.TryGetValue(st.MachineId, out var card))
                    card.Apply(st);
            }
            var up = results.Count(r => r.MetricsOk || r.LlmOk);
            var nextStatus = $"{up}/{results.Length} up";
            if (!string.Equals(_status.Text, nextStatus, StringComparison.Ordinal))
                _status.Text = nextStatus;
        }
        finally
        {
            _busy = false;
        }
    }
}
