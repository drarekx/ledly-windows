using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Ledly.Windows.App;

/// <summary>
/// The app window: a WebView2 showing the web UI the API serves on loopback. Closing it hides it
/// (the app keeps running in the notification area); size and position are remembered.
/// </summary>
sealed class MainWindow : Form
{
    static readonly Color Background = Color.FromArgb(0x12, 0x12, 0x14);

    readonly IPlatform _platform;
    readonly string _boundsFile;
    readonly WebView2 _web;
    readonly System.Windows.Forms.Timer _retry = new() { Interval = 1000 };
    bool _initialized;
    bool _quitting;

    public MainWindow(IPlatform platform)
    {
        _platform = platform;
        _boundsFile = Path.Combine(platform.DataDirectory, "window.json");
        Text = "Ledly";
        Icon = TrayIcons.AppIcon();
        BackColor = Background;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        MinimumSize = new Size(LogicalToDeviceUnits(860), LogicalToDeviceUnits(600));
        Size = new Size(LogicalToDeviceUnits(1180), LogicalToDeviceUnits(760));

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
        Controls.Add(_web);
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            Navigate();
        };
        LoadBounds();
    }

    /// <summary>True when the WebView2 runtime is installed (Edge's, shipped with Windows 11 and most Windows 10).</summary>
    public static bool RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    public void ShowAndActivate()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        _web.Visible = true;
        Activate();
        BringToFront();
        if (!_initialized) _ = InitializeAsync();
        else if (!IsCurrentOrigin(_web.Source)) Navigate();
    }

    async Task InitializeAsync()
    {
        _initialized = true;
        try
        {
            // Browser cache: local, not roaming like the settings.
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ledly", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, cache);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (IsLocal(e.Uri)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.NavigationCompleted += (_, e) =>
            {
                // The server may still be starting (or moving to a new port): try again.
                if (!e.IsSuccess && !_quitting) _retry.Start();
            };
            core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) || core.DocumentTitle.StartsWith("http") ? "Ledly" : core.DocumentTitle;
            Navigate();
        }
        catch (Exception e)
        {
            Log.Write($"webview: {e.Message}");
            _initialized = false;
            MessageBox.Show(this, L.Tr("No se puede mostrar la ventana de Ledly ({0}). Se abrirá en el navegador.", e.Message), "Ledly",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _platform.OpenUrl(LocalUrl());
            Hide();
        }
    }

    static string LocalUrl()
    {
        try { return APIServer.Shared.LocalUrl; }
        catch { return $"http://127.0.0.1:{Settings.Shared.ApiPort}/"; }
    }

    void Navigate()
    {
        if (_web.CoreWebView2 is null) return;
        try
        {
            _web.CoreWebView2.Navigate(LocalUrl());
        }
        catch (Exception e)
        {
            Log.Write($"webview: navigate: {e.Message}");
            _retry.Start();
        }
    }

    static bool IsLocal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return true;
        if (u.Scheme is "about" or "data" or "blob") return true;
        if (u.Scheme is not ("http" or "https")) return false;
        return u.IsLoopback && Uri.TryCreate(LocalUrl(), UriKind.Absolute, out var local) && u.Port == local.Port;
    }

    static bool IsCurrentOrigin(Uri? source) =>
        source is not null && Uri.TryCreate(LocalUrl(), UriKind.Absolute, out var local) && source.IsLoopback && source.Port == local.Port;

    void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto")
            _platform.OpenUrl(uri);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveBounds();
        if (e.CloseReason == CloseReason.UserClosing && !_quitting)
        {
            // Close = hide: Ledly stays in the notification area. Suspend the page meanwhile.
            e.Cancel = true;
            Hide();
            _web.Visible = false;
            _ = SuspendAsync();
            return;
        }
        base.OnFormClosing(e);
    }

    async Task SuspendAsync()
    {
        try
        {
            if (_web.CoreWebView2 is { } core) await core.TrySuspendAsync();
        }
        catch
        {
            // Not suspendable right now (e.g. still loading): it just keeps running.
        }
    }

    /// <summary>Closes for real (app quitting).</summary>
    public void CloseForQuit()
    {
        _quitting = true;
        _retry.Stop();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _retry.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }

    // Size and position --------------------------------------------------------------------------

    sealed record SavedBounds(int X, int Y, int Width, int Height, bool Maximized);

    void LoadBounds()
    {
        try
        {
            if (File.Exists(_boundsFile) && JsonSerializer.Deserialize<SavedBounds>(File.ReadAllText(_boundsFile)) is { } b)
            {
                var rect = new Rectangle(b.X, b.Y, Math.Max(b.Width, MinimumSize.Width), Math.Max(b.Height, MinimumSize.Height));
                // Only if a good part of it is still on some screen.
                if (Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, rect) is { Width: > 200, Height: > 100 }))
                {
                    Bounds = rect;
                    if (b.Maximized) WindowState = FormWindowState.Maximized;
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"window: {e.Message}");
        }
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    void SaveBounds()
    {
        try
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var saved = new SavedBounds(r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized);
            File.WriteAllText(_boundsFile, JsonSerializer.Serialize(saved));
        }
        catch (Exception e)
        {
            Log.Write($"window: {e.Message}");
        }
    }
}
