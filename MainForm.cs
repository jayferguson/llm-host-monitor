using System.Runtime.InteropServices;

namespace LlmHostMonitor;

public sealed class MainForm : Form
{
    private readonly Panel _list;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _alwaysItem;
    private readonly ToolStripMenuItem _columnsItem;
    private readonly ToolStripMenuItem _rowsItem;
    private readonly System.Windows.Forms.Timer _timer;
    private AppConfig _config = new();
    private readonly Dictionary<string, MachineCard> _cards = new();
    private MachineCard? _selected;
    private int _generation;
    private bool _busy;
    private bool _fitting;

    private const int CardGap = 4;

    public MainForm()
    {
        Text = "LLM Host Monitor";
        MinimumSize = new Size(280, 64);
        Size = new Size(640, 140);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(8, 11, 16);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        _alwaysItem = new ToolStripMenuItem("Always on top");
        _columnsItem = new ToolStripMenuItem("Layout: columns (side by side)");
        _rowsItem = new ToolStripMenuItem("Layout: rows (stacked)");
        _menu = BuildMenu();
        ContextMenuStrip = _menu;

        _list = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            Padding = new Padding(4, 1, 4, 1),
            BackColor = Color.FromArgb(8, 11, 16),
            ContextMenuStrip = _menu,
        };
        _list.Resize += (_, _) =>
        {
            if (_fitting) return;
            FitToContent();
        };
        _list.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_list.GetChildAtPoint(e.Location) is not null) return;
            _menu.Show(_list, e.Location);
            RaiseContextMenu();
        };

        Controls.Add(_list);

        _config = MachineStore.Load();
        SetAlwaysOnTop(_config.AlwaysOnTop, save: false);
        RebuildCards();
        _ = Handle;

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(2, _config.PollSeconds) * 1000 };
        _timer.Tick += async (_, _) => await PollOnceAsync();
        Shown += async (_, _) =>
        {
            FitToContent();
            await PollOnceAsync();
            _timer.Start();
        };
        FormClosed += (_, _) => _timer.Stop();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Add machine...", null, (_, _) => AddMachine());
        menu.Items.Add("Edit selected...", null, (_, _) => EditSelected());
        menu.Items.Add("Remove selected...", null, (_, _) => RemoveSelected());
        menu.Items.Add("Refresh now", null, async (_, _) => await PollOnceAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_alwaysItem);
        menu.Items.Add(_columnsItem);
        menu.Items.Add(_rowsItem);

        _alwaysItem.Click += (_, _) => SetAlwaysOnTop(!TopMost, save: true);
        _columnsItem.Click += (_, _) => SetHorizontalLayout(true, save: true, rebuild: true);
        _rowsItem.Click += (_, _) => SetHorizontalLayout(false, save: true, rebuild: true);
        menu.Opening += (_, _) =>
        {
            _alwaysItem.Checked = TopMost;
            _columnsItem.Checked = _config.HorizontalLayout;
            _rowsItem.Checked = !_config.HorizontalLayout;
        };
        // Form, list, and card menus (AttachContextMenu assigns this strip)
        // all show the same window. TopMost puts the owner in a higher band
        // than the popup, so lift the menu once its HWND exists.
        menu.Opened += (_, _) => RaiseContextMenu();
        return menu;
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private void RaiseContextMenu()
    {
        if (_menu.IsDisposed || !_menu.IsHandleCreated)
            return;
        SetWindowPos(_menu.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }

    private void SetAlwaysOnTop(bool on, bool save)
    {
        TopMost = on;
        if (save && _config.AlwaysOnTop != on)
        {
            _config.AlwaysOnTop = on;
            MachineStore.Save(_config);
        }
    }

    private void SetHorizontalLayout(bool horizontal, bool save, bool rebuild)
    {
        var changed = _config.HorizontalLayout != horizontal;
        if (!changed)
            return;
        _config.HorizontalLayout = horizontal;
        if (save)
            MachineStore.Save(_config);
        if (rebuild)
            RebuildCards();
    }

    private void RebuildCards()
    {
        _list.SuspendLayout();
        _list.Controls.Clear();
        _cards.Clear();
        _selected = null;
        var horizontal = _config.HorizontalLayout;
        foreach (var m in _config.Machines.Where(x => x.Enabled))
        {
            var card = new MachineCard(m);
            card.SetColumnMode(horizontal);
            card.AttachContextMenu(_menu);
            card.CardClicked += (_, _) => SelectCard(card);
            _cards[m.Id] = card;
            _list.Controls.Add(card);
        }
        _list.ResumeLayout(true);
        FitToContent();
    }

    private void SelectCard(MachineCard card) => _selected = card;

    private List<MachineCard> OrderedCards()
    {
        var list = new List<MachineCard>(_cards.Count);
        foreach (var m in _config.Machines)
        {
            if (m.Enabled && _cards.TryGetValue(m.Id, out var card))
                list.Add(card);
        }
        return list;
    }

    private void ResizeCards()
    {
        var cards = OrderedCards();
        if (cards.Count == 0) return;

        var left = _list.Padding.Left;
        var top = _list.Padding.Top;
        if (_config.HorizontalLayout)
        {
            var avail = Math.Max(0, _list.ClientSize.Width - _list.Padding.Horizontal);
            var sum = 0;
            foreach (var card in cards)
                sum += Math.Max(1, card.PreferredContentWidth);
            var gaps = CardGap * Math.Max(0, cards.Count - 1);
            var extra = Math.Max(0, avail - sum - gaps);
            var each = extra / cards.Count;
            var rem = extra % cards.Count;
            var rowH = 0;
            foreach (var card in cards)
                rowH = Math.Max(rowH, card.PreferredContentHeight);
            var x = left;
            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                var w = card.PreferredContentWidth + each + (i == 0 ? rem : 0);
                card.SetBounds(x, top, w, rowH);
                card.SetColumnMode(true);
                x += w + CardGap;
            }
        }
        else
        {
            var inner = Math.Max(80, _list.ClientSize.Width - _list.Padding.Horizontal);
            var y = top;
            foreach (var card in cards)
            {
                var w = Math.Max(inner, card.PreferredContentWidth);
                var h = card.PreferredContentHeight;
                card.SetBounds(left, y, w, h);
                card.SetColumnMode(false);
                y += h;
            }
        }
    }

    /// <summary>
    /// Outer window size that hugs the cards. Height matches the content
    /// (capped to the work area). Width grows when chips need it and never
    /// shrinks a window the user has already widened.
    /// </summary>
    private void FitToContent()
    {
        if (_fitting) return;
        _fitting = true;
        try
        {
            for (var pass = 0; pass < 3; pass++)
            {
                ResizeCards();
                if (!ApplyWindowFit()) break;
            }
        }
        finally
        {
            _fitting = false;
        }
    }

    /// <returns>True when the outer size changed and the cards should be measured again.</returns>
    private bool ApplyWindowFit()
    {
        var pad = _list.Padding;
        var contentH = ContentHeight();
        var contentW = ContentWidth();
        var rawH = ChromeHeight() + pad.Vertical + contentH;
        var rawW = ChromeWidth() + pad.Horizontal + contentW;

        var area = IsHandleCreated
            ? Screen.FromControl(this).WorkingArea
            : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var maxH = Math.Max(120, area.Height - 8);
        var maxW = Math.Max(320, area.Width - 8);

        var needsH = rawW > maxW;
        var needsV = rawH > maxH;
        if (needsV)
            rawW += SystemInformation.VerticalScrollBarWidth;
        if (needsH || rawW > maxW)
            rawH += SystemInformation.HorizontalScrollBarHeight;

        var targetH = Math.Min(Math.Max(48, rawH), maxH);
        var targetW = Math.Min(Math.Max(Math.Max(Width, 160), rawW), maxW);
        var overflow = rawW > maxW || rawH > maxH;
        _list.AutoScroll = overflow;
        _list.AutoScrollMinSize = overflow
            ? new Size(contentW + pad.Horizontal, contentH + pad.Vertical)
            : Size.Empty;

        var target = new Size(targetW, targetH);
        var changed = Size != target;
        MinimumSize = new Size(Math.Min(160, target.Width), Math.Min(48, target.Height));
        if (changed)
            Size = target;

        var lockW = Math.Max(200, Math.Min(rawW, maxW));
        var lockH = Math.Max(48, Math.Min(rawH, maxH));
        var locked = new Size(lockW, lockH);
        if (MinimumSize != locked)
            MinimumSize = locked;
        return changed;
    }

    private int ContentHeight()
    {
        var cards = OrderedCards();
        if (cards.Count == 0) return 36;
        if (_config.HorizontalLayout)
            return cards.Max(c => c.PreferredContentHeight);
        return cards.Sum(c => c.PreferredContentHeight);
    }

    private int ContentWidth()
    {
        var cards = OrderedCards();
        if (cards.Count == 0) return 280;
        if (_config.HorizontalLayout)
            return cards.Sum(c => c.PreferredContentWidth) + CardGap * Math.Max(0, cards.Count - 1);
        return cards.Max(c => c.PreferredContentWidth);
    }

    private int ChromeHeight()
    {
        if (!IsHandleCreated) return SystemInformation.CaptionHeight + SystemInformation.FrameBorderSize.Height * 2;
        var delta = Height - ClientSize.Height;
        return delta > 0 ? delta : SystemInformation.CaptionHeight + SystemInformation.FrameBorderSize.Height * 2;
    }

    private int ChromeWidth()
    {
        if (!IsHandleCreated) return SystemInformation.FrameBorderSize.Width * 2;
        var delta = Width - ClientSize.Width;
        return delta > 0 ? delta : SystemInformation.FrameBorderSize.Width * 2;
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
            if (gen != _generation || IsDisposed) return;
            foreach (var st in results)
            {
                if (_cards.TryGetValue(st.MachineId, out var card))
                    card.Apply(st);
            }
            var up = results.Count(r => r.MetricsOk || r.LlmOk);
            var nextTitle = $"LLM Host Monitor — {up}/{results.Length} up";
            if (!string.Equals(Text, nextTitle, StringComparison.Ordinal))
                Text = nextTitle;
            FitToContent();
        }
        finally
        {
            _busy = false;
        }
    }
}
