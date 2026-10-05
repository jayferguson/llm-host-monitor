using System.Runtime.InteropServices;

namespace LlmHostMonitor;

public sealed class MainForm : Form, IMessageFilter
{
    private readonly Panel _list;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _alwaysItem;
    private readonly ToolStripMenuItem _hideTitleItem;
    private readonly ToolStripMenuItem _columnsItem;
    private readonly ToolStripMenuItem _rowsItem;
    private readonly System.Windows.Forms.Timer _timer;
    private AppConfig _config = new();
    private readonly Dictionary<string, MachineCard> _cards = new();
    private MachineCard? _selected;
    private int _generation;
    private bool _busy;
    private bool _fitting;
    private bool _dragPending;
    private bool _dragMoved;
    private Point _dragOrigin;

    private const int CardGap = 4;

    public MainForm()
    {
        Text = "LLM Host Monitor";
        MinimumSize = new Size(280, 64);
        Size = new Size(640, 140);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = CanvasColor;
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        KeyPreview = true;

        _alwaysItem = new ToolStripMenuItem("Always on top");
        _hideTitleItem = new ToolStripMenuItem("Hide title bar");
        _columnsItem = new ToolStripMenuItem("Layout: columns (side by side)");
        _rowsItem = new ToolStripMenuItem("Layout: rows (stacked)");
        _menu = BuildMenu();
        ContextMenuStrip = _menu;

        _list = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            Padding = new Padding(4, 1, 4, 1),
            BackColor = CanvasColor,
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
            // A borderless drag starts from MouseMove; don't also open the menu.
            if (_dragMoved)
            {
                _dragMoved = false;
                return;
            }
            if (_list.GetChildAtPoint(e.Location) is not null) return;
            _menu.Show(_list, e.Location);
            RaiseContextMenu();
        };

        Controls.Add(_list);

        _config = MachineStore.Load();
        SetAlwaysOnTop(_config.AlwaysOnTop, save: false);
        SetHideTitleBar(_config.HideTitleBar, save: false);
        WireBorderlessDrag(this);
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
        // The wheel is delivered to the child under the cursor. Take it here so
        // every card sees it, and so the list does not scroll at the same time.
        Application.AddMessageFilter(this);
        FormClosed += (_, _) =>
        {
            Application.RemoveMessageFilter(this);
            _timer.Stop();
        };
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
        menu.Items.Add(_hideTitleItem);
        menu.Items.Add(_columnsItem);
        menu.Items.Add(_rowsItem);
        menu.Items.Add("Larger text", null, (_, _) => IncreaseUiScale());
        menu.Items.Add("Smaller text", null, (_, _) => DecreaseUiScale());
        menu.Items.Add("Reset text size", null, (_, _) => ResetUiScale());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Close", null, (_, _) => Close());

        _alwaysItem.Click += (_, _) => SetAlwaysOnTop(!TopMost, save: true);
        _hideTitleItem.Click += (_, _) => SetHideTitleBar(!_config.HideTitleBar, save: true);
        _columnsItem.Click += (_, _) => SetHorizontalLayout(true, save: true, rebuild: true);
        _rowsItem.Click += (_, _) => SetHorizontalLayout(false, save: true, rebuild: true);
        menu.Opening += (_, _) =>
        {
            _alwaysItem.Checked = TopMost;
            _hideTitleItem.Checked = _config.HideTitleBar;
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
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_NCHITTEST = 0x84;
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int BorderlessPad = 3;
    private const int BorderlessGrip = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly Color CanvasColor = Color.FromArgb(8, 11, 16);
    private static readonly Color BorderlessEdgeColor = Color.FromArgb(40, 46, 56);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

    private void SetHideTitleBar(bool on, bool save)
    {
        var changed = _config.HideTitleBar != on;
        _config.HideTitleBar = on;
        if (save && changed)
            MachineStore.Save(_config);
        ApplyBorderlessChrome();
        if (save)
            FitToContent();
    }

    /// <summary>
    /// Borderless client area has no caption, so keep the client origin put
    /// and put TopMost back after the style change (that change clears it).
    /// </summary>
    private void ApplyBorderlessChrome()
    {
        var on = _config.HideTitleBar;
        Point? origin = null;
        if (Visible && IsHandleCreated)
            origin = PointToScreen(Point.Empty);
        var topMost = TopMost;
        var wasFitting = _fitting;
        _fitting = true;
        try
        {
            FormBorderStyle = on ? FormBorderStyle.None : FormBorderStyle.Sizable;
            Padding = on ? new Padding(BorderlessPad) : Padding.Empty;
            BackColor = on ? BorderlessEdgeColor : CanvasColor;
            TopMost = topMost;
            if (topMost && IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            if (origin is Point saved && IsHandleCreated)
            {
                var now = PointToScreen(Point.Empty);
                if (now != saved)
                    Location = new Point(Location.X + saved.X - now.X, Location.Y + saved.Y - now.Y);
            }
        }
        finally
        {
            _fitting = wasFitting;
        }
    }

    private void WireBorderlessDrag(Control control)
    {
        control.MouseDown -= OnBorderlessMouseDown;
        control.MouseMove -= OnBorderlessMouseMove;
        control.MouseUp -= OnBorderlessMouseUp;
        control.MouseDown += OnBorderlessMouseDown;
        control.MouseMove += OnBorderlessMouseMove;
        control.MouseUp += OnBorderlessMouseUp;
        control.ControlAdded -= OnBorderlessControlAdded;
        control.ControlAdded += OnBorderlessControlAdded;
        foreach (Control child in control.Controls)
            WireBorderlessDrag(child);
    }

    private void OnBorderlessControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is Control added)
            WireBorderlessDrag(added);
    }

    private void OnBorderlessMouseDown(object? sender, MouseEventArgs e)
    {
        if (!_config.HideTitleBar || e.Button != MouseButtons.Left)
            return;
        _dragPending = true;
        _dragMoved = false;
        _dragOrigin = ScreenPoint(sender, e);
    }

    private void OnBorderlessMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragPending || !_config.HideTitleBar || e.Button != MouseButtons.Left)
            return;
        var now = ScreenPoint(sender, e);
        var drag = SystemInformation.DragSize;
        if (Math.Abs(now.X - _dragOrigin.X) <= drag.Width && Math.Abs(now.Y - _dragOrigin.Y) <= drag.Height)
            return;
        _dragPending = false;
        _dragMoved = true;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void OnBorderlessMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            _dragPending = false;
    }

    private static Point ScreenPoint(object? sender, MouseEventArgs e) =>
        sender is Control control ? control.PointToScreen(e.Location) : Cursor.Position;

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

    private void IncreaseUiScale() => SetUiScale(_config.UiScale * AppConfig.UiScaleStep);

    private void DecreaseUiScale() => SetUiScale(_config.UiScale / AppConfig.UiScaleStep);

    private void ResetUiScale() => SetUiScale(1d);

    private void SetUiScale(double scale)
    {
        scale = AppConfig.NormalizeUiScale(scale);
        if (Math.Abs(scale - _config.UiScale) < 1e-9)
            return;
        _config.UiScale = scale;
        MachineStore.Save(_config);
        ApplyUiScale();
    }

    private void ApplyUiScale()
    {
        var scale = AppConfig.NormalizeUiScale(_config.UiScale);
        _config.UiScale = scale;
        var wasFitting = _fitting;
        _fitting = true;
        try
        {
            foreach (var card in _cards.Values)
                card.ApplyScale(scale);
        }
        finally
        {
            _fitting = wasFitting;
        }
        FitToContent();
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
            card.ApplyScale(_config.UiScale);
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
        var frame = Padding;
        var contentH = ContentHeight();
        var contentW = ContentWidth();
        var rawH = ChromeHeight() + pad.Vertical + frame.Vertical + contentH;
        var rawW = ChromeWidth() + pad.Horizontal + frame.Horizontal + contentW;

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
        if (_config.HideTitleBar) return 0;
        if (!IsHandleCreated) return SystemInformation.CaptionHeight + SystemInformation.FrameBorderSize.Height * 2;
        var delta = Height - ClientSize.Height;
        return delta > 0 ? delta : SystemInformation.CaptionHeight + SystemInformation.FrameBorderSize.Height * 2;
    }

    private int ChromeWidth()
    {
        if (_config.HideTitleBar) return 0;
        if (!IsHandleCreated) return SystemInformation.FrameBorderSize.Width * 2;
        var delta = Width - ClientSize.Width;
        return delta > 0 ? delta : SystemInformation.FrameBorderSize.Width * 2;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var mods = keyData & Keys.Modifiers;
        if ((mods & Keys.Control) == Keys.Control && (mods & Keys.Alt) == 0)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Oemplus:
                case Keys.Add:
                    IncreaseUiScale();
                    return true;
                case Keys.OemMinus:
                case Keys.Subtract:
                    DecreaseUiScale();
                    return true;
                case Keys.D0:
                case Keys.NumPad0:
                    ResetUiScale();
                    return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (TryScaleFromWheel(e.Delta))
        {
            if (e is HandledMouseEventArgs handled)
                handled.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }

    public bool PreFilterMessage(ref Message m)
    {
        // Plain wheel (or Ctrl+wheel) anywhere over the focused window changes text size.
        if (m.Msg != WM_MOUSEWHEEL || IsDisposed || !ContainsFocus || !Bounds.Contains(Cursor.Position))
            return false;
        var delta = (short)(unchecked((int)(long)m.WParam) >> 16);
        return TryScaleFromWheel(delta);
    }

    private bool TryScaleFromWheel(int delta)
    {
        if ((ModifierKeys & Keys.Alt) == Keys.Alt)
            return false;
        if (delta > 0)
            IncreaseUiScale();
        else if (delta < 0)
            DecreaseUiScale();
        else
            return false;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && _config.HideTitleBar)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)HTCLIENT)
                m.Result = BorderlessResizeHit(m.LParam);
            return;
        }
        base.WndProc(ref m);
    }

    private IntPtr BorderlessResizeHit(IntPtr lParam)
    {
        var lp = unchecked((int)lParam.ToInt64());
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        var pt = PointToClient(screen);
        var onLeft = pt.X < BorderlessGrip;
        var onRight = pt.X >= ClientSize.Width - BorderlessGrip;
        var onTop = pt.Y < BorderlessGrip;
        var onBottom = pt.Y >= ClientSize.Height - BorderlessGrip;
        if (onTop && onLeft) return (IntPtr)HTTOPLEFT;
        if (onTop && onRight) return (IntPtr)HTTOPRIGHT;
        if (onBottom && onLeft) return (IntPtr)HTBOTTOMLEFT;
        if (onBottom && onRight) return (IntPtr)HTBOTTOMRIGHT;
        if (onLeft) return (IntPtr)HTLEFT;
        if (onRight) return (IntPtr)HTRIGHT;
        if (onTop) return (IntPtr)HTTOP;
        if (onBottom) return (IntPtr)HTBOTTOM;
        return (IntPtr)HTCLIENT;
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
