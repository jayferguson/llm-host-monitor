namespace LlmHostMonitor;

public sealed class MachineCard : Panel
{
    private readonly Label _title;
    private readonly Label _model;
    private readonly Label _state;
    private readonly Label _error;
    private readonly Panel _gpus;
    private HostStatus? _last;

    public string MachineId { get; }

    public MachineCard(MachineConfig cfg)
    {
        MachineId = cfg.Id;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Margin = new Padding(0, 0, 0, 6);
        Padding = new Padding(8, 6, 8, 6);
        MinimumSize = new Size(280, 56);
        BackColor = Color.FromArgb(12, 18, 26);
        BorderStyle = BorderStyle.FixedSingle;

        _title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = cfg.Name.ToUpperInvariant(),
            Location = new Point(8, 6),
        };
        _model = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(230, 236, 245),
            Text = ".",
            Location = new Point(80, 6),
        };
        _state = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = ".",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _error = new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.FromArgb(255, 120, 120),
            Text = "",
            Location = new Point(8, 26),
            Height = 16,
            Visible = false,
        };
        _gpus = new Panel
        {
            Location = new Point(6, 28),
            Height = 32,
            BackColor = Color.Transparent,
        };

        Controls.Add(_title);
        Controls.Add(_model);
        Controls.Add(_state);
        Controls.Add(_error);
        Controls.Add(_gpus);
        Resize += (_, _) => LayoutHeader();
        LayoutHeader();
    }

    private void LayoutHeader()
    {
        _title.Location = new Point(8, 6);
        _model.Location = new Point(_title.Right + 10, 6);
        _state.Location = new Point(Math.Max(_model.Right + 8, Width - _state.PreferredWidth - 12), 7);
        var maxModel = Math.Max(40, _state.Left - _model.Left - 8);
        _model.MaximumSize = new Size(maxModel, 0);
        _error.Width = Math.Max(80, Width - 20);
        _error.Location = new Point(8, 26);
        var gpusTop = _error.Visible ? 44 : 28;
        _gpus.Location = new Point(6, gpusTop);
        _gpus.Width = Math.Max(80, ClientSize.Width - 12);
        StretchGpuChips();
        var bottom = _gpus.Controls.Count == 0 ? gpusTop + 4 : _gpus.Bottom;
        Height = Math.Max(56, bottom + 8);
    }

    private void StretchGpuChips()
    {
        var n = _gpus.Controls.Count;
        if (n == 0)
        {
            _gpus.Height = 4;
            return;
        }
        const int gap = 4;
        const int chipH = 32;
        var avail = Math.Max(160, _gpus.ClientSize.Width);
        for (var i = 0; i < n; i++)
        {
            var chip = _gpus.Controls[i];
            chip.Margin = Padding.Empty;
            chip.Location = new Point(0, i * (chipH + gap));
            chip.Size = new Size(avail, chipH);
            ResizeChipContents(chip, avail);
        }
        _gpus.Height = n * chipH + Math.Max(0, n - 1) * gap;
    }

    private static void ResizeChipContents(Control chip, int width)
    {
        foreach (Control c in chip.Controls)
        {
            if (c is Label lab && lab.Tag as string == "gpu-name")
                lab.Width = Math.Max(50, width - 86);
            if (c is Label pwr && pwr.Tag as string == "gpu-power")
                pwr.Left = Math.Max(80, width - 40);
            if (c is Panel wrap && wrap.Tag as string == "vram")
            {
                wrap.Width = Math.Max(70, (width - 96) / 2);
                SizeMeter(wrap);
            }
            if (c is Panel wrap2 && wrap2.Tag as string == "util")
            {
                var vram = FindTagged(chip, "vram");
                wrap2.Left = (vram?.Right ?? 96) + 6;
                wrap2.Width = Math.Max(70, width - wrap2.Left - 8);
                SizeMeter(wrap2);
            }
        }
    }

    private static Control? FindTagged(Control parent, string tag)
    {
        foreach (Control c in parent.Controls)
            if (c.Tag as string == tag) return c;
        return null;
    }

    private static void SizeMeter(Control wrap)
    {
        foreach (Control inner in wrap.Controls)
        {
            if (inner is Panel track && track.Tag is double pct)
            {
                track.Width = Math.Max(30, wrap.Width - 72);
                if (track.Controls.Count > 0)
                    track.Controls[0].Width = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100.0 * track.Width);
            }
            if (inner is Label val && val.TextAlign == ContentAlignment.MiddleRight)
                val.Left = Math.Max(40, wrap.Width - 32);
        }
    }

    public void Apply(HostStatus status)
    {
        _last = status;
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

        var structureChanged = SyncGpuChips(status.Gpus);
        if (structureChanged || _error.Visible != _lastErrorVisible)
        {
            _lastErrorVisible = _error.Visible;
            LayoutHeader();
        }
    }

    private bool _lastErrorVisible;


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
        // Rebuild only when GPU count/identity changes; otherwise update meters in place.
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
            _gpus.ResumeLayout(true);
            StretchGpuChips();
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
            Width = 280,
            Height = 32,
            Margin = new Padding(0, 0, 6, 0),
            BackColor = Color.FromArgb(8, 12, 18),
        };
        var title = new Label
        {
            AutoSize = false,
            Width = 90,
            Height = 14,
            Location = new Point(6, 2),
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
            Location = new Point(200, 2),
            Tag = "gpu-power",
        };
        panel.Controls.Add(title);
        panel.Controls.Add(meta);
        panel.Controls.Add(Meter("VRAM", g.VramPct, 6, 16, "vram"));
        panel.Controls.Add(Meter("util", g.UtilPct, 150, 16, "util"));
        return panel;
    }

    private static Control Meter(string label, double? pct, int x, int y, string tag)
    {
        var wrap = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(140, 14),
            BackColor = Color.Transparent,
            Tag = tag,
        };
        var lab = new Label
        {
            AutoSize = false,
            Size = new Size(32, 12),
            Font = new Font("Segoe UI", 7f),
            ForeColor = Color.FromArgb(140, 160, 180),
            Text = label,
            Location = new Point(0, 0),
        };
        var track = new Panel
        {
            Location = new Point(34, 3),
            Size = new Size(72, 6),
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
            AutoSize = false,
            Size = new Size(30, 12),
            Location = new Point(108, 0),
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
}

