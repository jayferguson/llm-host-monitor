namespace LlmHostMonitor;

public sealed class MachineCard : Panel
{
    private readonly Label _title;
    private readonly Label _model;
    private readonly Label _state;
    private readonly Label _error;
    private readonly Panel _gpus;
    private ContextMenuStrip? _menu;
    private bool _columnMode;
    private bool _inLayout;

    public string MachineId { get; }

    /// <summary>Card height that fits the header plus the GPU row or stack.</summary>
    public int PreferredContentHeight { get; private set; } = 52;

    /// <summary>Card width that fits the header and every GPU chip without clipping.</summary>
    public int PreferredContentWidth { get; private set; } = 280;

    public event EventHandler? CardClicked;

    public MachineCard(MachineConfig cfg)
    {
        MachineId = cfg.Id;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Margin = Padding.Empty;
        Padding = new Padding(8, 6, 8, 6);
        MinimumSize = new Size(80, 32);
        BackColor = Color.FromArgb(12, 18, 26);
        BorderStyle = BorderStyle.FixedSingle;

        _title = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = cfg.Name.ToUpperInvariant(),
            Location = new Point(8, 5),
        };
        _model = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(230, 236, 245),
            Text = ".",
            Location = new Point(80, 5),
        };
        _state = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = ".",
            TextAlign = ContentAlignment.MiddleRight,
        };
        _error = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.FromArgb(255, 120, 120),
            Text = "",
            Visible = false,
        };
        _gpus = new Panel
        {
            Location = new Point(6, 24),
            Height = 32,
            BackColor = Color.Transparent,
        };

        Controls.Add(_title);
        Controls.Add(_model);
        Controls.Add(_state);
        Controls.Add(_error);
        Controls.Add(_gpus);
        LayoutHeader();
    }

    public void AttachContextMenu(ContextMenuStrip menu)
    {
        _menu = menu;
        BindMenu(this);
    }

    public void SetColumnMode(bool column)
    {
        _columnMode = column;
        LayoutHeader();
    }

    private void BindMenu(Control c)
    {
        if (_menu is not null)
            c.ContextMenuStrip = _menu;
        c.MouseDown -= OnChildMouseDown;
        c.MouseDown += OnChildMouseDown;
        foreach (Control child in c.Controls)
            BindMenu(child);
    }

    private void OnChildMouseDown(object? sender, MouseEventArgs e) =>
        CardClicked?.Invoke(this, EventArgs.Empty);

    private void LayoutHeader()
    {
        if (_inLayout) return;
        _inLayout = true;
        try
        {
            var innerW = Math.Max(40, ClientSize.Width);
            var titleH = _title.Font.Height + 2;
            var stateW = TextWidth(_state) + 4;
            var stateH = _state.Font.Height + 2;
            _state.SetBounds(Math.Max(8, innerW - stateW - 8), 4, stateW, stateH);

            var titleMax = Math.Max(48, Math.Max(0, _state.Left - 16) / 2);
            var titleW = Math.Min(Math.Max(TextWidth(_title) + 2, 24), titleMax);
            _title.SetBounds(8, 4, titleW, titleH);

            var modelLeft = _title.Right + 8;
            var modelW = Math.Max(12, _state.Left - modelLeft - 6);
            _model.SetBounds(modelLeft, 4, modelW, _model.Font.Height + 2);

            var headerBottom = Math.Max(_title.Bottom, Math.Max(_model.Bottom, _state.Bottom));
            _error.SetBounds(8, headerBottom + 1, Math.Max(40, innerW - 16), _error.Font.Height + 2);
            var gpusTop = _error.Visible ? _error.Bottom + 2 : headerBottom + 2;

            _gpus.Location = new Point(4, gpusTop);
            _gpus.Width = Math.Max(40, innerW - 8);
            StretchGpuChips();

            var bottom = _gpus.Controls.Count == 0 ? gpusTop : _gpus.Bottom;
            // bottom is in client coordinates; add the non-client border so the
            // last pixel of the GPU row is not clipped by the card edge.
            PreferredContentHeight = HeightForClient(bottom + 4);
            PreferredContentWidth = Math.Max(PreferredContentWidth, WidthForClient(HeaderClientWidth()));
        }
        finally
        {
            _inLayout = false;
        }
    }

    private void StretchGpuChips()
    {
        var n = _gpus.Controls.Count;
        if (n == 0)
        {
            _gpus.Height = 0;
            PreferredContentWidth = WidthForClient(Math.Max(180, HeaderClientWidth()));
            return;
        }

        const int gap = 4;
        var chipH = 0;
        var mins = new int[n];
        for (var i = 0; i < n; i++)
        {
            chipH = Math.Max(chipH, ChipOuterHeight(_gpus.Controls[i]));
            mins[i] = MeasureChipWidth(_gpus.Controls[i]);
        }

        var avail = Math.Max(40, _gpus.ClientSize.Width);
        if (_columnMode)
        {
            var sumMin = 0;
            for (var i = 0; i < n; i++) sumMin += mins[i];
            var gaps = Math.Max(0, n - 1) * gap;
            var extra = Math.Max(0, avail - sumMin - gaps);
            var each = extra / n;
            var rem = extra % n;
            var x = 0;
            for (var i = 0; i < n; i++)
            {
                var chipW = mins[i] + each + (i == 0 ? rem : 0);
                var chip = _gpus.Controls[i];
                chip.SetBounds(x, 0, chipW, chipH);
                LayoutChip(chip, chipW);
                x += chipW + gap;
            }
            _gpus.Height = chipH;
            // _gpus is inset 4px on each side of the client area.
            PreferredContentWidth = WidthForClient(sumMin + gaps + 8);
            return;
        }

        var minW = 0;
        for (var i = 0; i < n; i++) minW = Math.Max(minW, mins[i]);
        var chipWFull = Math.Max(avail, minW);
        for (var i = 0; i < n; i++)
        {
            var chip = _gpus.Controls[i];
            chip.SetBounds(0, i * (chipH + gap), chipWFull, chipH);
            LayoutChip(chip, chipWFull);
        }
        _gpus.Height = n * chipH + Math.Max(0, n - 1) * gap;
        PreferredContentWidth = WidthForClient(minW + 8);
    }

    private int HeaderClientWidth() =>
        8 + TextWidth(_title) + 8 + 40 + 8 + TextWidth(_state) + 10;

    private int WidthForClient(int clientWidth)
    {
        var border = Width - ClientSize.Width;
        if (border < 2) border = 2;
        return clientWidth + border;
    }

    private int HeightForClient(int clientHeight)
    {
        var border = Height - ClientSize.Height;
        if (border < 2) border = 2;
        return clientHeight + border;
    }

    private static int ChipOuterHeight(Control chip) =>
        2 + ChipTextHeight(chip) + 1 + ChipMeterHeight(chip) + 2;

    private static int ChipTextHeight(Control chip)
    {
        var name = FindTagged(chip, "gpu-name") as Label;
        var power = FindTagged(chip, "gpu-power") as Label;
        return Math.Max(name?.Font.Height ?? 12, power?.Font.Height ?? 12) + 2;
    }

    private static int ChipMeterHeight(Control chip) =>
        Math.Max(MeterLineHeight(FindTagged(chip, "vram")), MeterLineHeight(FindTagged(chip, "util"))) + 2;

    private static int MeterLineHeight(Control? wrap)
    {
        var h = 12;
        if (wrap is null) return h;
        foreach (Control inner in wrap.Controls)
        {
            if (inner is Label lab)
                h = Math.Max(h, lab.Font.Height);
        }
        return h;
    }

    /// <summary>Width the chip needs so temp/power and both percents sit fully inside.</summary>
    private static int MeasureChipWidth(Control chip)
    {
        var name = FindTagged(chip, "gpu-name") as Label;
        var power = FindTagged(chip, "gpu-power") as Label;
        var nameCap = name is null ? 48 : Math.Max(36, name.Font.Height * 8);
        var nameW = name is null ? 48 : Math.Min(nameCap, Math.Max(36, TextWidth(name)));
        var powerW = PowerSlotWidth(power);
        var header = 6 + nameW + 6 + powerW + 4;

        var vramW = MeterMinWidth(FindTagged(chip, "vram"));
        var utilW = MeterMinWidth(FindTagged(chip, "util"));
        var meters = 6 + vramW + 6 + utilW + 4;
        return Math.Max(header, meters);
    }

    private static int PowerSlotWidth(Label? power)
    {
        if (power is null) return 64;
        // PreferredWidth (via TextWidth) sizes the temp/power string. +4 keeps
        // the last glyph inside the chip after the label is given an explicit width.
        return TextWidth(power) + 4;
    }

    private static int MeterMinWidth(Control? wrap)
    {
        if (wrap is null) return 96;
        SplitMeter(wrap, out var cap, out _, out var val);
        var capW = cap is null ? 28 : TextWidth(cap) + 2;
        var valW = PercentSlotWidth(val);
        const int minTrack = 16;
        return capW + 3 + minTrack + 3 + valW;
    }

    private static int PercentSlotWidth(Label? val)
    {
        if (val is null) return 36;
        var full = TextRenderer.MeasureText("100%", val.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        return Math.Max(TextWidth(val), full) + 4;
    }

    private static void LayoutChip(Control chip, int width)
    {
        var name = FindTagged(chip, "gpu-name") as Label;
        var power = FindTagged(chip, "gpu-power") as Label;
        var textH = ChipTextHeight(chip);
        var powerW = PowerSlotWidth(power);
        if (power is not null)
        {
            power.AutoSize = false;
            power.TextAlign = ContentAlignment.MiddleRight;
            var powerLeft = Math.Max(2, width - powerW - 4);
            power.SetBounds(powerLeft, 2, powerW, textH);
        }
        if (name is not null)
        {
            name.AutoSize = false;
            name.AutoEllipsis = true;
            var right = (power?.Left ?? width) - 4;
            name.SetBounds(6, 2, Math.Max(8, right - 6), textH);
        }

        var vram = FindTagged(chip, "vram");
        var util = FindTagged(chip, "util");
        if (vram is null || util is null) return;

        var meterTop = 2 + textH + 1;
        var meterH = ChipMeterHeight(chip);
        const int leftPad = 6;
        const int rightPad = 4;
        const int gap = 6;
        var inner = Math.Max(0, width - leftPad - rightPad);
        var vramMin = MeterMinWidth(vram);
        var utilMin = MeterMinWidth(util);
        var extra = Math.Max(0, inner - vramMin - utilMin - gap);
        var vramW = vramMin + extra / 2;
        var utilW = Math.Max(utilMin, inner - gap - vramW);
        // Keep the util chip, including its percent, inside the GPU chip.
        if (leftPad + vramW + gap + utilW > width - rightPad)
            utilW = Math.Max(8, width - rightPad - leftPad - vramW - gap);

        vram.SetBounds(leftPad, meterTop, vramW, meterH);
        util.SetBounds(leftPad + vramW + gap, meterTop, utilW, meterH);
        SizeMeter(vram);
        SizeMeter(util);
    }

    private static void SizeMeter(Control wrap)
    {
        SplitMeter(wrap, out var cap, out var track, out var val);
        var line = Math.Max(1, wrap.Height);
        var capW = 0;
        if (cap is not null)
        {
            capW = TextWidth(cap) + 2;
            cap.AutoSize = false;
            cap.SetBounds(0, 0, capW, line);
        }
        var valW = PercentSlotWidth(val);
        if (val is not null)
        {
            val.AutoSize = false;
            val.TextAlign = ContentAlignment.MiddleRight;
            var valLeft = Math.Max(0, wrap.Width - valW);
            val.SetBounds(valLeft, 0, valW, line);
        }
        if (track is not null)
        {
            var left = capW + 3;
            var right = (val?.Left ?? wrap.Width) - 3;
            var trackW = Math.Max(2, right - left);
            const int trackH = 6;
            track.SetBounds(left, Math.Max(0, (line - trackH) / 2), trackW, trackH);
            if (track.Tag is double pct && track.Controls.Count > 0)
            {
                var fill = track.Controls[0];
                fill.Height = track.Height;
                fill.Width = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100.0 * track.Width);
            }
        }
    }

    private static void SplitMeter(Control wrap, out Label? cap, out Panel? track, out Label? val)
    {
        cap = null;
        track = null;
        val = null;
        foreach (Control inner in wrap.Controls)
        {
            if (inner is Panel panel && panel.Tag is double)
                track = panel;
            else if (inner is Label lab)
            {
                if (lab.TextAlign == ContentAlignment.MiddleRight) val = lab;
                else if (cap is null) cap = lab;
                else val ??= lab;
            }
        }
    }

    private static int TextWidth(Label label)
    {
        if (label.Text.Length == 0) return 0;
        var measured = Measured(label);
        var preferred = label.PreferredWidth;
        // PreferredWidth is the text width while AutoSize is on. After the label
        // is given an explicit width, it can echo that width and grow every pass.
        if (preferred <= 0 || (!label.AutoSize && Math.Abs(preferred - label.Width) <= 1))
            return measured;
        return Math.Max(preferred, measured);
    }

    private static int Measured(Label label) =>
        TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;

    public void Apply(HostStatus status)
    {
        SetText(_title, status.Name.ToUpperInvariant());
        var model = string.IsNullOrWhiteSpace(status.ModelLabel)
            ? (string.IsNullOrWhiteSpace(status.Model) ? "-" : status.Model)
            : status.ModelLabel;
        SetText(_model, FormatHeaderModel(model, status.TotalVramBytes, status.ContextTokens));

        string state;
        Color border;
        Color bg;
        Color stateColor;

        if (!status.MetricsOk && !status.LlmOk)
        {
            state = "DOWN";
            border = Color.FromArgb(180, 60, 70);
            bg = Color.FromArgb(36, 16, 20);
            stateColor = border;
        }
        else if (status.Active)
        {
            state = "ACTIVE";
            border = Color.FromArgb(62, 207, 142);
            bg = Color.FromArgb(13, 36, 24);
            stateColor = border;
        }
        else
        {
            state = "IDLE";
            border = Color.FromArgb(42, 53, 72);
            bg = Color.FromArgb(12, 18, 26);
            stateColor = Color.FromArgb(140, 160, 180);
        }

        if (BackColor != bg) BackColor = bg;
        Tag = border;
        SetText(_state, state);
        if (_state.ForeColor != stateColor) _state.ForeColor = stateColor;
        if (_title.ForeColor != stateColor) _title.ForeColor = stateColor;
        var modelColor = status.Active ? Color.FromArgb(216, 255, 232) : Color.FromArgb(230, 236, 245);
        if (_model.ForeColor != modelColor) _model.ForeColor = modelColor;

        if (!string.IsNullOrWhiteSpace(status.Error) && (!status.MetricsOk || !status.LlmOk))
        {
            SetText(_error, status.Error);
            if (!_error.Visible) _error.Visible = true;
        }
        else if (_error.Visible)
        {
            _error.Visible = false;
            SetText(_error, "");
        }

        SyncGpuChips(status.Gpus);
        LayoutHeader();
    }

    private static string FormatHeaderModel(string model, long? totalVramBytes, int? contextTokens)
    {
        var parts = new List<string> { model };
        if (totalVramBytes is long bytes && bytes > 0)
        {
            var gib = bytes / (1024.0 * 1024.0 * 1024.0);
            parts.Add(gib >= 10 ? $"{gib:0} GB" : $"{gib:0.#} GB");
        }
        if (contextTokens is int ctx && ctx > 0)
        {
            if (ctx >= 1024 && ctx % 1024 == 0) parts.Add($"{ctx / 1024}k ctx");
            else if (ctx >= 1000) parts.Add($"{ctx / 1000.0:0.#}k ctx");
            else parts.Add($"{ctx} ctx");
        }
        return string.Join("  ", parts);
    }

    private static void SetText(Label label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal))
            label.Text = text;
    }

    private bool SyncGpuChips(IReadOnlyList<GpuInfo> gpus)
    {
        var needRebuild = _gpus.Controls.Count != gpus.Count;
        if (!needRebuild)
        {
            for (var i = 0; i < gpus.Count; i++)
            {
                var name = FindTagged(_gpus.Controls[i], "gpu-name") as Label;
                if (name is null || !string.Equals(name.Text, gpus[i].Name, StringComparison.Ordinal))
                {
                    needRebuild = true;
                    break;
                }
            }
        }

        if (needRebuild)
        {
            _gpus.SuspendLayout();
            _gpus.Controls.Clear();
            foreach (var g in gpus)
                _gpus.Controls.Add(BuildGpuChip(g));
            _gpus.ResumeLayout(false);
            BindMenu(_gpus);
            return true;
        }

        for (var i = 0; i < gpus.Count; i++)
            UpdateGpuChip(_gpus.Controls[i], gpus[i]);
        return false;
    }

    private static void UpdateGpuChip(Control chip, GpuInfo g)
    {
        foreach (Control c in chip.Controls)
        {
            if (c is Label meta && meta.Tag as string == "gpu-power")
            {
                var text = (g.TempC is double t ? $"{t:0}\u00b0C" : "-") + "  " + (g.PowerW is double w ? $"{w:0}W" : "-");
                if (!string.Equals(meta.Text, text, StringComparison.Ordinal))
                {
                    meta.Text = text;
                    meta.ForeColor = TempColor(g.TempC);
                }
            }
            if (c is Panel wrap && wrap.Tag as string == "vram")
                UpdateMeter(wrap, g.VramPct, "VRAM");
            if (c is Panel wrap2 && wrap2.Tag as string == "util")
                UpdateMeter(wrap2, g.UtilPct, "util");
        }
    }

    private static void UpdateMeter(Control wrap, double? pct, string kind)
    {
        foreach (Control inner in wrap.Controls)
        {
            if (inner is Panel track)
            {
                track.Tag = pct ?? 0.0;
                var fillW = pct is double p ? (int)Math.Round(Math.Clamp(p, 0, 100) / 100.0 * track.Width) : 0;
                if (track.Controls.Count > 0)
                {
                    var fill = track.Controls[0];
                    if (fill.Width != fillW) fill.Width = fillW;
                    var tone = Tone(kind, pct);
                    if (fill.BackColor != tone) fill.BackColor = tone;
                }
            }
            if (inner is Label val && val.TextAlign == ContentAlignment.MiddleRight)
            {
                var text = pct is double v ? $"{v:0}%" : "-";
                if (!string.Equals(val.Text, text, StringComparison.Ordinal))
                    val.Text = text;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var c = Tag as Color? ?? Color.FromArgb(42, 53, 72);
        using var pen = new Pen(c, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    private static Control BuildGpuChip(GpuInfo g)
    {
        var panel = new Panel
        {
            Height = 36,
            Margin = Padding.Empty,
            BackColor = Color.FromArgb(8, 12, 18),
        };
        var title = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 228, 238),
            Text = g.Name,
            Tag = "gpu-name",
        };
        var meta = new Label
        {
            AutoSize = true,
            Font = new Font("Consolas", 7.5f),
            ForeColor = TempColor(g.TempC),
            Text = (g.TempC is double t ? $"{t:0}\u00b0C" : "-") + "  " + (g.PowerW is double w ? $"{w:0}W" : "-"),
            TextAlign = ContentAlignment.MiddleRight,
            Tag = "gpu-power",
        };
        panel.Controls.Add(title);
        panel.Controls.Add(meta);
        panel.Controls.Add(Meter("VRAM", g.VramPct, "vram"));
        panel.Controls.Add(Meter("util", g.UtilPct, "util"));
        return panel;
    }

    private static Control Meter(string label, double? pct, string tag)
    {
        var wrap = new Panel
        {
            Size = new Size(120, 14),
            BackColor = Color.Transparent,
            Tag = tag,
        };
        var lab = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 7f),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var track = new Panel
        {
            Size = new Size(40, 6),
            BackColor = Color.FromArgb(30, 40, 55),
            Tag = pct ?? 0.0,
        };
        var fillW = pct is double p ? (int)Math.Round(Math.Clamp(p, 0, 100) / 100.0 * track.Width) : 0;
        var fill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(fillW, track.Height),
            BackColor = Tone(label, pct),
        };
        track.Controls.Add(fill);
        var val = new Label
        {
            AutoSize = true,
            Font = new Font("Consolas", 7f),
            ForeColor = Color.FromArgb(200, 210, 220),
            Text = pct is double v ? $"{v:0}%" : "-",
            TextAlign = ContentAlignment.MiddleRight,
        };
        wrap.Controls.Add(lab);
        wrap.Controls.Add(track);
        wrap.Controls.Add(val);
        return wrap;
    }

    private static Color Tone(string kind, double? pct)
    {
        var v = pct ?? 0;
        if (kind.Equals("VRAM", StringComparison.OrdinalIgnoreCase))
        {
            if (v >= 90) return Color.FromArgb(230, 90, 90);
            if (v >= 70) return Color.FromArgb(230, 170, 70);
            return Color.FromArgb(70, 160, 220);
        }
        if (v >= 80) return Color.FromArgb(230, 90, 90);
        if (v >= 40) return Color.FromArgb(62, 207, 142);
        return Color.FromArgb(90, 120, 150);
    }

    private static Color TempColor(double? c)
    {
        if (c is null) return Color.FromArgb(140, 160, 180);
        if (c >= 80) return Color.FromArgb(230, 90, 90);
        if (c >= 70) return Color.FromArgb(230, 170, 70);
        return Color.FromArgb(140, 160, 180);
    }

    private static Control? FindTagged(Control parent, string tag)
    {
        foreach (Control c in parent.Controls)
            if (c.Tag as string == tag) return c;
        return null;
    }
}
