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

    private const float TitlePt = 9f;
    private const float ModelPt = 9f;
    private const float StatePt = 8f;
    private const float ErrorPt = 8f;
    private const float GpuNamePt = 7.5f;
    private const float GpuPowerPt = 7.5f;
    private const float MeterLabelPt = 7f;
    private const float MeterValuePt = 7f;

    /// <summary>Current scale. Layout always multiplies the design pixels by this, never by the previous scale.</summary>
    private double _scale = 1d;

    private int Px(int baseline) =>
        (int)Math.Round(baseline * _scale, MidpointRounding.AwayFromZero);

    private Font ScaledFont(string family, float sizePt, FontStyle style) =>
        new(family, (float)(sizePt * _scale), style, GraphicsUnit.Point);

    public void ApplyScale(double scale)
    {
        _scale = AppConfig.NormalizeUiScale(scale);
        Padding = new Padding(Px(8), Px(6), Px(8), Px(6));
        MinimumSize = new Size(Px(80), Px(32));
        SetScaledFont(_title, "Segoe UI", TitlePt, FontStyle.Bold);
        SetScaledFont(_model, "Segoe UI", ModelPt, FontStyle.Bold);
        SetScaledFont(_state, "Segoe UI", StatePt, FontStyle.Bold);
        SetScaledFont(_error, "Segoe UI", ErrorPt, FontStyle.Regular);
        foreach (Control chip in _gpus.Controls)
            ScaleChipFonts(chip);
        LayoutHeader();
    }

    private void SetScaledFont(Control control, string family, float sizePt, FontStyle style)
    {
        var size = (float)(sizePt * _scale);
        var current = control.Font;
        if (string.Equals(current.FontFamily.Name, family, StringComparison.Ordinal)
            && current.Style == style
            && current.Unit == GraphicsUnit.Point
            && Math.Abs(current.SizeInPoints - size) < 0.01f)
            return;

        var next = ScaledFont(family, sizePt, style);
        control.Font = next;
        if (ReferenceEquals(control.Font, current))
        {
            next.Dispose();
            return;
        }
        if (!current.IsSystemFont && !ReferenceEquals(current, control.Parent?.Font))
            current.Dispose();
    }

    private void ScaleChipFonts(Control chip)
    {
        if (FindTagged(chip, "gpu-name") is Label name)
            SetScaledFont(name, "Segoe UI", GpuNamePt, FontStyle.Bold);
        if (FindTagged(chip, "gpu-power") is Label power)
            SetScaledFont(power, "Consolas", GpuPowerPt, FontStyle.Regular);
        ScaleMeterFonts(FindTagged(chip, "vram"));
        ScaleMeterFonts(FindTagged(chip, "util"));
    }

    private void ScaleMeterFonts(Control? wrap)
    {
        if (wrap is null) return;
        foreach (Control inner in wrap.Controls)
        {
            if (inner is not Label lab) continue;
            var value = lab.TextAlign == ContentAlignment.MiddleRight;
            SetScaledFont(lab, value ? "Consolas" : "Segoe UI", value ? MeterValuePt : MeterLabelPt, FontStyle.Regular);
        }
    }

    public MachineCard(MachineConfig cfg)
    {
        MachineId = cfg.Id;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Margin = Padding.Empty;
        Padding = new Padding(Px(8), Px(6), Px(8), Px(6));
        MinimumSize = new Size(Px(80), Px(32));
        BackColor = Color.FromArgb(12, 18, 26);
        BorderStyle = BorderStyle.FixedSingle;

        _title = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = ScaledFont("Segoe UI", TitlePt, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = cfg.Name.ToUpperInvariant(),
            Location = new Point(Px(8), Px(5)),
        };
        _model = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = ScaledFont("Segoe UI", ModelPt, FontStyle.Bold),
            ForeColor = Color.FromArgb(230, 236, 245),
            Text = ".",
            Location = new Point(Px(80), Px(5)),
        };
        _state = new Label
        {
            AutoSize = false,
            Font = ScaledFont("Segoe UI", StatePt, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = ".",
            TextAlign = ContentAlignment.MiddleRight,
        };
        _error = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = ScaledFont("Segoe UI", ErrorPt, FontStyle.Regular),
            ForeColor = Color.FromArgb(255, 120, 120),
            Text = "",
            Visible = false,
        };
        _gpus = new Panel
        {
            Location = new Point(Px(6), Px(24)),
            Height = Px(32),
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
            var innerW = Math.Max(Px(40), ClientSize.Width);
            var titleH = _title.Font.Height + Px(2);
            var stateW = TextWidth(_state) + Px(4);
            var stateH = _state.Font.Height + Px(2);
            _state.SetBounds(Math.Max(Px(8), innerW - stateW - Px(8)), Px(4), stateW, stateH);

            var titleMax = Math.Max(Px(48), Math.Max(0, _state.Left - Px(16)) / 2);
            var titleW = Math.Min(Math.Max(TextWidth(_title) + Px(2), Px(24)), titleMax);
            _title.SetBounds(Px(8), Px(4), titleW, titleH);

            var modelLeft = _title.Right + Px(8);
            var modelW = Math.Max(Px(12), _state.Left - modelLeft - Px(6));
            _model.SetBounds(modelLeft, Px(4), modelW, _model.Font.Height + Px(2));

            var headerBottom = Math.Max(_title.Bottom, Math.Max(_model.Bottom, _state.Bottom));
            _error.SetBounds(Px(8), headerBottom + Px(1), Math.Max(Px(40), innerW - Px(16)), _error.Font.Height + Px(2));
            var gpusTop = _error.Visible ? _error.Bottom + Px(2) : headerBottom + Px(2);

            _gpus.Location = new Point(Px(4), gpusTop);
            _gpus.Width = Math.Max(Px(40), innerW - Px(8));
            StretchGpuChips();

            var bottom = _gpus.Controls.Count == 0 ? gpusTop : _gpus.Bottom;
            // bottom is in client coordinates; add the non-client border so the
            // last pixel of the GPU row is not clipped by the card edge.
            PreferredContentHeight = HeightForClient(bottom + Px(4));
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
            PreferredContentWidth = WidthForClient(Math.Max(Px(180), HeaderClientWidth()));
            return;
        }

        var gap = Px(4);
        var chipH = 0;
        var mins = new int[n];
        for (var i = 0; i < n; i++)
        {
            chipH = Math.Max(chipH, ChipOuterHeight(_gpus.Controls[i]));
            mins[i] = MeasureChipWidth(_gpus.Controls[i]);
        }

        var avail = Math.Max(Px(40), _gpus.ClientSize.Width);
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
            PreferredContentWidth = WidthForClient(sumMin + gaps + Px(8));
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
        PreferredContentWidth = WidthForClient(minW + Px(8));
    }

    private int HeaderClientWidth() =>
        Px(8) + TextWidth(_title) + Px(8) + Px(40) + Px(8) + TextWidth(_state) + Px(10);

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

    private int ChipOuterHeight(Control chip) =>
        Px(2) + ChipTextHeight(chip) + Px(1) + ChipMeterHeight(chip) + Px(2);

    private int ChipTextHeight(Control chip)
    {
        var name = FindTagged(chip, "gpu-name") as Label;
        var power = FindTagged(chip, "gpu-power") as Label;
        return Math.Max(name?.Font.Height ?? Px(12), power?.Font.Height ?? Px(12)) + Px(2);
    }

    private int ChipMeterHeight(Control chip) =>
        Math.Max(MeterLineHeight(FindTagged(chip, "vram")), MeterLineHeight(FindTagged(chip, "util"))) + Px(2);

    private int MeterLineHeight(Control? wrap)
    {
        var h = Px(12);
        if (wrap is null) return h;
        foreach (Control inner in wrap.Controls)
        {
            if (inner is Label lab)
                h = Math.Max(h, lab.Font.Height);
        }
        return h;
    }

    /// <summary>Width the chip needs so temp/power and both percents sit fully inside.</summary>
    private int MeasureChipWidth(Control chip)
    {
        var name = FindTagged(chip, "gpu-name") as Label;
        var power = FindTagged(chip, "gpu-power") as Label;
        var nameCap = name is null ? Px(48) : Math.Max(Px(36), name.Font.Height * 8);
        var nameW = name is null ? Px(48) : Math.Min(nameCap, Math.Max(Px(36), TextWidth(name)));
        var powerW = PowerSlotWidth(power);
        var header = Px(6) + nameW + Px(6) + powerW + Px(4);

        var vramW = MeterMinWidth(FindTagged(chip, "vram"));
        var utilW = MeterMinWidth(FindTagged(chip, "util"));
        var meters = Px(6) + vramW + Px(6) + utilW + Px(4);
        return Math.Max(header, meters);
    }

    private int PowerSlotWidth(Label? power)
    {
        if (power is null) return Px(64);
        // PreferredWidth (via TextWidth) sizes the temp/power string. +4 keeps
        // the last glyph inside the chip after the label is given an explicit width.
        return TextWidth(power) + Px(4);
    }

    private int MeterMinWidth(Control? wrap)
    {
        if (wrap is null) return Px(96);
        SplitMeter(wrap, out var cap, out _, out var val);
        var capW = cap is null ? Px(28) : TextWidth(cap) + Px(2);
        var valW = PercentSlotWidth(val);
        var minTrack = Px(16);
        return capW + Px(3) + minTrack + Px(3) + valW;
    }

    private int PercentSlotWidth(Label? val)
    {
        if (val is null) return Px(36);
        var full = TextRenderer.MeasureText("100%", val.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        return Math.Max(TextWidth(val), full) + Px(4);
    }

    private void LayoutChip(Control chip, int width)
    {
        var name = FindTagged(chip, "gpu-name") as Label;
        var power = FindTagged(chip, "gpu-power") as Label;
        var textH = ChipTextHeight(chip);
        var powerW = PowerSlotWidth(power);
        if (power is not null)
        {
            power.AutoSize = false;
            power.TextAlign = ContentAlignment.MiddleRight;
            var powerLeft = Math.Max(Px(2), width - powerW - Px(4));
            power.SetBounds(powerLeft, Px(2), powerW, textH);
        }
        if (name is not null)
        {
            name.AutoSize = false;
            name.AutoEllipsis = true;
            var right = (power?.Left ?? width) - Px(4);
            name.SetBounds(Px(6), Px(2), Math.Max(Px(8), right - Px(6)), textH);
        }

        var vram = FindTagged(chip, "vram");
        var util = FindTagged(chip, "util");
        if (vram is null || util is null) return;

        var meterTop = Px(2) + textH + Px(1);
        var meterH = ChipMeterHeight(chip);
        var leftPad = Px(6);
        var rightPad = Px(4);
        var gap = Px(6);
        var inner = Math.Max(0, width - leftPad - rightPad);
        var vramMin = MeterMinWidth(vram);
        var utilMin = MeterMinWidth(util);
        var extra = Math.Max(0, inner - vramMin - utilMin - gap);
        var vramW = vramMin + extra / 2;
        var utilW = Math.Max(utilMin, inner - gap - vramW);
        // Keep the util chip, including its percent, inside the GPU chip.
        if (leftPad + vramW + gap + utilW > width - rightPad)
            utilW = Math.Max(Px(8), width - rightPad - leftPad - vramW - gap);

        vram.SetBounds(leftPad, meterTop, vramW, meterH);
        util.SetBounds(leftPad + vramW + gap, meterTop, utilW, meterH);
        SizeMeter(vram);
        SizeMeter(util);
    }

    private void SizeMeter(Control wrap)
    {
        SplitMeter(wrap, out var cap, out var track, out var val);
        var line = Math.Max(1, wrap.Height);
        var capW = 0;
        if (cap is not null)
        {
            capW = TextWidth(cap) + Px(2);
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
            var left = capW + Px(3);
            var right = (val?.Left ?? wrap.Width) - Px(3);
            var trackW = Math.Max(Px(2), right - left);
            var trackH = Px(6);
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

    private Control BuildGpuChip(GpuInfo g)
    {
        var panel = new Panel
        {
            Height = Px(36),
            Margin = Padding.Empty,
            BackColor = Color.FromArgb(8, 12, 18),
        };
        var title = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = ScaledFont("Segoe UI", GpuNamePt, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 228, 238),
            Text = g.Name,
            Tag = "gpu-name",
        };
        var meta = new Label
        {
            AutoSize = true,
            Font = ScaledFont("Consolas", GpuPowerPt, FontStyle.Regular),
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

    private Control Meter(string label, double? pct, string tag)
    {
        var wrap = new Panel
        {
            Size = new Size(Px(120), Px(14)),
            BackColor = Color.Transparent,
            Tag = tag,
        };
        var lab = new Label
        {
            AutoSize = false,
            Font = ScaledFont("Segoe UI", MeterLabelPt, FontStyle.Regular),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var track = new Panel
        {
            Size = new Size(Px(40), Px(6)),
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
            Font = ScaledFont("Consolas", MeterValuePt, FontStyle.Regular),
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
