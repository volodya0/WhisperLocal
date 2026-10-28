using System.Diagnostics;
using System.Drawing.Drawing2D;
using WhisperLocal.Native;

namespace WhisperLocal.Ui;

/// <summary>
/// Small indicator at the bottom of the screen, the counterpart of the macOS one; never takes focus.
/// Recording and processing show a live seconds counter; afterwards a short summary (with a Text button that opens
/// the transcript) stays for a moment, longer while the mouse is over it. Click-through except in that state.
/// </summary>
internal sealed class RecordingOverlay : Form
{
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // Sizes at 100 % scaling
    private const int BarHeight = 36;
    private const int TimerWidth = 210;
    private const int ResultWidth = 360;
    private const int ButtonWidth = 64;

    private static readonly Color Red = Color.FromArgb(255, 90, 90);
    private static readonly Color Orange = Color.FromArgb(255, 180, 60);

    private enum Mode { Hidden, Recording, Processing, Result }

    private readonly Label _label;
    private readonly Button _button;
    private readonly System.Windows.Forms.Timer _timer = new();

    private Mode _mode = Mode.Hidden;
    private double _fontScale;
    private long _since;
    private TimeSpan _resultFor;

    public RecordingOverlay(Action showText)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.92;

        _label = new Label
        {
            AutoEllipsis = true,
            ForeColor = Color.White,
            BackColor = BackColor,
        };
        _button = new Button
        {
            Text = "Text",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(64, 64, 68),
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        _button.FlatAppearance.BorderSize = 0;
        _button.Click += (_, _) => showText();
        Controls.Add(_label);
        Controls.Add(_button);

        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void ShowRecording(long startTimestamp) => ShowCounter(Mode.Recording, startTimestamp);

    public void ShowProcessing(long startTimestamp) => ShowCounter(Mode.Processing, startTimestamp);

    /// <summary>A summary line for <paramref name="duration"/> (longer while hovered); the Text button if there is text.</summary>
    public void ShowResult(string summary, Color color, bool hasText, TimeSpan duration)
    {
        _mode = Mode.Result;
        _since = Stopwatch.GetTimestamp();
        _resultFor = duration;
        Present(ResultWidth, hasText);
        SetLabel(summary, color);
        SetClickThrough(false);
        _timer.Interval = 200;
        _timer.Start();
    }

    public new void Hide()
    {
        _mode = Mode.Hidden;
        _timer.Stop();
        base.Hide();
    }

    private void ShowCounter(Mode mode, long startTimestamp)
    {
        _mode = mode;
        _since = startTimestamp;
        Present(TimerWidth, button: false);
        SetClickThrough(true);
        Tick();
        _timer.Interval = 100;
        _timer.Start();
    }

    private void Tick()
    {
        var elapsed = Stopwatch.GetElapsedTime(_since);
        switch (_mode)
        {
            case Mode.Recording:
                SetLabel($"● Recording  {elapsed.TotalSeconds:F1} s", Red);
                break;
            case Mode.Processing:
                SetLabel($"… Processing  {elapsed.TotalSeconds:F1} s", Orange);
                break;
            case Mode.Result when elapsed >= _resultFor && !Bounds.Contains(Cursor.Position):
                Hide();
                break;
        }
    }

    private void SetLabel(string text, Color color)
    {
        _label.Text = text;
        _label.ForeColor = color;
    }

    /// <summary>Sizes and shows the bar at the bottom centre of the screen with the active window.</summary>
    private void Present(int width, bool button)
    {
        var screen = Screen.FromHandle(NativeMethods.GetForegroundWindow());
        var scale = DeviceDpi / 96.0;
        int S(int v) => (int)Math.Round(v * scale);

        var area = screen.WorkingArea;
        Bounds = new Rectangle(area.Left + (area.Width - S(width)) / 2, area.Bottom - S(BarHeight) - S(40), S(width), S(BarHeight));
        var oldRegion = Region;
        using (var path = RoundedRect(new Rectangle(0, 0, Width, Height), S(10)))
            Region = new Region(path);
        oldRegion?.Dispose();

        if (scale != _fontScale)
        {
            _fontScale = scale;
            _label.Font = new Font("Segoe UI Semibold", 13f * (float)scale, GraphicsUnit.Pixel);
            _button.Font = new Font("Segoe UI", 12f * (float)scale, GraphicsUnit.Pixel);
        }
        _label.TextAlign = button ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter;
        _label.Bounds = new Rectangle(S(12), 0, Width - S(24) - (button ? S(ButtonWidth + 8) : 0), Height);
        _button.Visible = button;
        _button.Bounds = new Rectangle(Width - S(ButtonWidth) - S(10), (Height - S(24)) / 2, S(ButtonWidth), S(24));

        if (!Visible)
            Show();
    }

    /// <summary>The bar sizes itself for the screen it is shown on; WinForms must not rescale it on top of that.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    private void SetClickThrough(bool on)
    {
        var style = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        style = on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, style);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}
