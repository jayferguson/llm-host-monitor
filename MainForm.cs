namespace LlmHostMonitor;

public sealed class MainForm : Form
{
    private readonly Panel _list;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _timer;
    private AppConfig _config = new();
    private readonly Dictionary<string, MachineCard> _cards = new();
    private int _generation;
    private bool _busy;
    private static readonly Font IconFont = new(PickIconFamily(), 11f);
    private readonly Button _pinBtn;
    private readonly Button _layoutBtn;
    private readonly ToolTip _pinTips;
    private readonly ToolTip _layoutTips;

    private void SetAlwaysOnTop(bool on, bool save)
    {
        TopMost = on;
        _pinBtn.Text = on ? "\uE841" : "\uE718";
        _pinBtn.BackColor = on ? Color.FromArgb(90, 74, 30) : Color.FromArgb(32, 42, 58);
        _pinTips.SetToolTip(_pinBtn, on ? "Always on top: on (click to turn off)" : "Always on top: off (click to turn on)");
        if (save && _config.AlwaysOnTop != on)
        {
            _config.AlwaysOnTop = on;
            MachineStore.Save(_config);
        }
    }

    private void SetHorizontalLayout(bool horizontal, bool save, bool rebuild)
    {
        // Horizontal = systems side by side (each a column). Vertical = stacked (each a row).
        _layoutBtn.Text = horizontal ? "\uE80A" : "\uE8A9";
        _layoutBtn.BackColor = horizontal ? Color.FromArgb(40, 70, 100) : Color.FromArgb(32, 42, 58);
        _layoutTips.SetToolTip(_layoutBtn,
            horizontal
                ? "Layout: columns (side by side). Click for rows (stacked)."
                : "Layout: rows (stacked). Click for columns (side by side).");
        if (save && _config.HorizontalLayout != horizontal)
        {
            _config.HorizontalLayout = horizontal;
            MachineStore.Save(_config);
        }
        if (rebuild)
        {
            FitWindowForLayout(horizontal);
            RebuildCards();
        }
    }

    private void FitWindowForLayout(bool horizontal)
    {
        var n = Math.Max(1, _config.Machines.Count(m => m.Enabled));
        if (horizontal)
        {
            // Wide enough for side-by-side hosts with GPUs in a row; short to cut empty space.
            var w = Math.Max(640, 48 + n * 360);
            Size = new Size(w, 160);
            MinimumSize = new Size(480, 120);
        }
        else
        {
            var h = Math.Max(220, 80 + n * 90);
            Size = new Size(Math.Max(Width, 640), h);
            MinimumSize = new Size(420, 180);
        }
    }

    /// <summary>
    /// Shrink (or grow) the window height to the tallest card's content so horizontal
    /// mode has no empty band under the hosts.
    /// </summary>
    private void FitToContent()
    {
        if (!_config.HorizontalLayout || _cards.Count == 0) return;
        var contentH = _cards.Values.Max(c => c.PreferredContentHeight);
        if (contentH < 56) contentH = 56;
        const int toolbarH = 44;
        var listPad = _list.Padding.Vertical;
        var chrome = Height - ClientSize.Height;
        var target = chrome + toolbarH + listPad + contentH + 4;
        var n = _cards.Count;
        var minW = Math.Max(MinimumSize.Width, 48 + n * 340);
        if (Width < minW) Width = minW;
        if (Height != target)
            Height = Math.Max(MinimumSize.Height, target);
    }

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
        _pinBtn = MakeIconBtn("\uE718", 152, Color.FromArgb(230, 200, 90));
        _pinTips = tips;
        _pinBtn.Click += (_, _) => SetAlwaysOnTop(!TopMost, save: true);

        _layoutBtn = MakeIconBtn("\uE8A9", 188, Color.FromArgb(180, 200, 230));
        _layoutTips = tips;
        _layoutBtn.Click += (_, _) => SetHorizontalLayout(!_config.HorizontalLayout, save: true, rebuild: true);

        _status = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(140, 160, 180),
            Location = new Point(232, 12),
            Text = "starting.",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        toolbar.Controls.Add(addBtn);
        toolbar.Controls.Add(editBtn);
        toolbar.Controls.Add(removeBtn);
        toolbar.Controls.Add(refreshBtn);
        toolbar.Controls.Add(_pinBtn);
        toolbar.Controls.Add(_layoutBtn);
        toolbar.Controls.Add(_status);
        toolbar.Resize += (_, _) =>
        {
            _status.Left = Math.Max(232, toolbar.Width - _status.PreferredWidth - 12);
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
        SetAlwaysOnTop(_config.AlwaysOnTop, save: false);
        SetHorizontalLayout(_config.HorizontalLayout, save: false, rebuild: false);
        if (_config.HorizontalLayout)
            FitWindowForLayout(true);
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
        var horizontal = _config.HorizontalLayout;
        var machines = _config.Machines.Where(x => x.Enabled).ToList();
        // Dock.Top/Left: first added takes the leading edge. Reverse so config order
        // reads top-to-bottom (vertical) or left-to-right (horizontal).
        var order = machines.AsEnumerable().Reverse();
        foreach (var m in order)
        {
            var card = new MachineCard(m)
            {
                Margin = horizontal ? new Padding(0, 0, 6, 0) : new Padding(0, 0, 0, 6),
            };
            card.SetColumnMode(horizontal);
            if (horizontal)
            {
                card.Dock = DockStyle.Left;
                card.Width = 340;
            }
            else
            {
                card.Dock = DockStyle.Top;
                card.Height = 72;
            }
            card.Click += (_, _) => SelectCard(card);
            foreach (Control c in card.Controls) c.Click += (_, _) => SelectCard(card);
            _cards[m.Id] = card;
            _list.Controls.Add(card);
        }
        _list.ResumeLayout(true);
        ResizeCards();
        FitToContent();
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
        var horizontal = _config.HorizontalLayout;
        var n = _list.Controls.OfType<MachineCard>().Count();
        if (n == 0) return;

        if (horizontal)
        {
            const int gap = 6;
            var availW = Math.Max(240, _list.ClientSize.Width - _list.Padding.Horizontal - Math.Max(0, n - 1) * gap);
            var cardW = Math.Max(280, availW / n);
            foreach (Control c in _list.Controls)
            {
                if (c is MachineCard card)
                {
                    card.Width = cardW;
                    // Height comes from Dock.Left filling the list; FitToContent sizes the form.
                    card.SetColumnMode(true);
                }
            }
        }
        else
        {
            var inner = Math.Max(280, _list.ClientSize.Width - _list.Padding.Horizontal);
            foreach (Control c in _list.Controls)
            {
                if (c is MachineCard card)
                {
                    card.Width = inner;
                    card.SetColumnMode(false);
                }
            }
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
            FitToContent();
        }
        finally
        {
            _busy = false;
        }
    }
}
