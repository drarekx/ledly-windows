using Ledly.Windows.App;
using Ledly.Windows.Interop;
using Ledly.Windows.Platform;

namespace Ledly.Windows;

static class Program
{
    const string MutexName = @"Local\Ledly.SingleInstance";
    const string ShowEventName = @"Local\Ledly.ShowWindow";

    [STAThread]
    static int Main(string[] args)
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ledly");
        Directory.CreateDirectory(data);
        Log.FilePath = Path.Combine(data, "ledly.log");
        var platform = new WindowsPlatform(data);

        // Command-line tools (--flash, --read-flash): print to the console that started us.
        if (args.Any(a => a is "--flash" or "--read-flash"))
        {
            AttachConsoleOutput();
            Settings.Load(data);
            Flasher.BackupFolder = Path.Combine(data, "Backups");
            if (Cli.TryRun(args, platform) is { } exitCode) return exitCode;
        }

        // Only one copy at a time: the serial port and the API port can only be opened once.
        // A second launch asks the running one to show its window and quits.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (Exception e)
            {
                Log.Write($"second launch: {e.Message}");
            }
            return 0;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        Log.Write($"Ledly {Updater.CurrentVersion} starting ({Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        // The output loop runs at 50 Hz on Sleep-based timing: needs 1 ms timer resolution.
        Native.timeBeginPeriod(1);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");

            Settings.Load(data);
            Flasher.BackupFolder = Path.Combine(data, "Backups");
            var engine = LEDEngine.Start(platform);
            Start("api", () => APIServer.Start(platform));
            Start("updater", () => Updater.Start(platform));

            // First launch: show the window. Started by Windows at login (--background): stay in the tray.
            var marker = Path.Combine(data, ".onboarded");
            var firstRun = !File.Exists(marker);
            if (firstRun) File.WriteAllText(marker, "");
            var showWindow = firstRun || !args.Contains("--background");

            // WinForms' synchronization context must exist before the tray app grabs it.
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using var app = new TrayApp(platform, showWindow);

            // A second launch sets the event: show the window.
            var waiter = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => platform.ShowWindow(), null, Timeout.Infinite, executeOnlyOnce: false);
            try
            {
                Application.Run(app);
            }
            finally
            {
                waiter.Unregister(null);
                engine.Shutdown();
                Log.Write("Ledly quit");
            }
            return 0;
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    static void Start(string what, Action start)
    {
        try
        {
            start();
        }
        catch (Exception e)
        {
            Log.Write($"{what}: {e.Message}");
        }
    }

    /// <summary>A WinExe has no console: write to the one we were started from, if any.</summary>
    static void AttachConsoleOutput()
    {
        if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return;
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            Console.WriteLine();
        }
        catch
        {
            // No usable console handles.
        }
    }
}
