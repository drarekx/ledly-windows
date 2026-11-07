using System.Drawing;
using Ledly.Windows.Platform;
using Microsoft.Win32;

namespace Ledly.Windows.App;

/// <summary>
/// Notification-area icon and menu (the Mac menu bar's: on/off, modes, effects, brightness, open,
/// launch at login, updates, quit), the app window, and the power watcher. Lives on the UI thread.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly WindowsPlatform _platform;
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly PowerWatcher _power;
    readonly SynchronizationContext _ui;
    MainWindow? _window;
    bool? _iconOn;
    bool _toldNoWebView;

    public TrayApp(WindowsPlatform platform, bool showWindow)
    {
        _platform = platform;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        platform.UiContext = _ui;
        platform.ShowWindowHandler = ShowMain;
        platform.QuitHandler = Quit;

        _menu.Opening += (_, _) => BuildMenu();
        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = false };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMain();
        };
        UpdateIcon(force: true);
        _tray.Visible = true;

        Settings.Shared.Changed += _ => _ui.Post(_ => UpdateIcon(), null);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionEnded += OnSessionEnded;
        _power = new PowerWatcher();

        if (showWindow) _ui.Post(_ => ShowMain(), null);
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => _ui.Post(_ => UpdateIcon(force: true), null);
    void OnDisplaySettingsChanged(object? sender, EventArgs e) => _ui.Post(_ => UpdateIcon(force: true), null);
    /// <summary>Log off / shut down: quit cleanly so the strip goes dark and settings are saved.</summary>
    void OnSessionEnded(object? sender, SessionEndedEventArgs e) => _ui.Post(_ => Quit(), null);

    // Icon -------------------------------------------------------------------------------------

    void UpdateIcon(bool force = false)
    {
        var on = Settings.Shared.IsOn;
        if (!force && _iconOn == on) return;
        _iconOn = on;
        var old = _tray.Icon;
        _tray.Icon = TrayIcons.Draw(on, SystemInformation.SmallIconSize.Width);
        _tray.Text = on ? L.Tr("Ledly, tira encendida") : L.Tr("Ledly, tira apagada");
        old?.Dispose();
    }

    // Menu -------------------------------------------------------------------------------------

    void BuildMenu()
    {
        foreach (var item in _menu.Items.OfType<ToolStripItem>().ToList()) item.Dispose();
        _menu.Items.Clear();
        var settings = Settings.Shared;

        Add(_menu.Items, settings.IsOn ? L.Tr("Apagar luces") : L.Tr("Encender luces"), () => settings.IsOn = !settings.IsOn);
        _menu.Items.Add(new ToolStripSeparator());

        foreach (var mode in Enum.GetValues<LightMode>())
        {
            var item = Add(_menu.Items, ModeTitle(mode), () =>
            {
                settings.Mode = mode;
                settings.IsOn = true;
            });
            item.Checked = settings.Mode == mode;
        }

        var effects = new ToolStripMenuItem(L.Tr("Efectos"));
        foreach (var effect in EffectKindInfo.Ordered)
        {
            var item = Add(effects.DropDownItems, EffectTitle(effect), () =>
            {
                settings.Effect = effect;
                settings.Mode = LightMode.effect;
                settings.IsOn = true;
            });
            item.Checked = settings.Mode == LightMode.effect && settings.Effect == effect;
        }
        _menu.Items.Add(effects);

        var brightness = new ToolStripMenuItem(L.Tr("Brillo"));
        foreach (var level in new[] { 10, 25, 50, 75, 100 })
        {
            var item = Add(brightness.DropDownItems, $"{level} %", () => settings.Brightness = level / 100.0);
            item.Checked = (int)Math.Round(settings.Brightness * 100) == level;
        }
        _menu.Items.Add(brightness);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(DeviceLine()) { Enabled = false });
        var open = Add(_menu.Items, L.Tr("Abrir Ledly…"), ShowMain);
        open.Font = new Font(open.Font, FontStyle.Bold);
        if (_platform.SupportsLaunchAtLogin)
        {
            var login = Add(_menu.Items, L.Tr("Abrir al iniciar sesión"), () => _platform.LaunchAtLogin = !_platform.LaunchAtLogin);
            login.Checked = _platform.LaunchAtLogin;
        }

        _menu.Items.Add(new ToolStripSeparator());
        var updating = UpdaterState() != UpdateStateKind.idle;
        var update = Add(_menu.Items, updating ? L.Tr("Actualizando…") : L.Tr("Buscar actualizaciones…"), CheckForUpdates);
        update.Enabled = !updating;
        Add(_menu.Items, L.Tr("Invítame a un café…"), () => _platform.OpenUrl("https://buymeacoffee.com/drarek"));
        _menu.Items.Add(new ToolStripMenuItem($"Ledly {Updater.CurrentVersion}") { Enabled = false });
        Add(_menu.Items, L.Tr("Salir de Ledly"), Quit);
    }

    static ToolStripMenuItem Add(ToolStripItemCollection items, string title, Action action)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception e) { Log.Write($"menu: {e.Message}"); }
        };
        items.Add(item);
        return item;
    }

    static string ModeTitle(LightMode mode) => mode switch
    {
        LightMode.screen => L.Tr("Pantalla"),
        LightMode.effect => L.Tr("Efecto"),
        LightMode.color => L.Tr("Color"),
        _ => mode.ToString(),
    };

    static string EffectTitle(EffectKind effect)
    {
        try { return L.Tr(effect.TitleKey()); }
        catch { return effect.ToString(); }
    }

    static string DeviceLine()
    {
        try
        {
            return LEDEngine.Shared.DeviceStatus.Kind switch
            {
                DeviceStatusKind.connected => L.Tr("Tira conectada · {0} LEDs", Settings.Shared.Layout.Count),
                DeviceStatusKind.connecting => L.Tr("Conectando…"),
                DeviceStatusKind.disconnected => L.Tr("Tira desconectada"),
                _ => L.Tr("Error en la tira"),
            };
        }
        catch
        {
            return L.Tr("Tira desconectada");
        }
    }

    static UpdateStateKind UpdaterState()
    {
        try { return Updater.Shared.State; }
        catch { return UpdateStateKind.idle; }
    }

    static void CheckForUpdates()
    {
        try
        {
            _ = Updater.Shared.CheckAsync(userInitiated: true).ContinueWith(t =>
            {
                if (t.Exception is { } e) Log.Write($"update check: {e.InnerException?.Message}");
            }, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            Log.Write($"update check: {e.Message}");
        }
    }

    // Window -----------------------------------------------------------------------------------

    public void ShowMain()
    {
        if (!MainWindow.RuntimeAvailable())
        {
            // No WebView2 runtime (rare: some Windows 10 / LTSC installs): the browser shows the same UI.
            if (!_toldNoWebView)
            {
                _toldNoWebView = true;
                MessageBox.Show(L.Tr("Falta Microsoft Edge WebView2 Runtime, que Ledly usa para su ventana. La interfaz se abrirá en el navegador; para tenerla en su propia ventana, instala WebView2 Runtime desde la web de Microsoft."),
                    "Ledly", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            string url;
            try { url = APIServer.Shared.LocalUrl; }
            catch { url = $"http://127.0.0.1:{Settings.Shared.ApiPort}/"; }
            _platform.OpenUrl(url);
            return;
        }
        if (_window is null || _window.IsDisposed) _window = new MainWindow(_platform);
        _window.ShowAndActivate();
    }

    void Quit()
    {
        _tray.Visible = false;
        _window?.CloseForQuit();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionEnded -= OnSessionEnded;
            _power.Dispose();
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _window?.Dispose();
        }
        base.Dispose(disposing);
    }
}
