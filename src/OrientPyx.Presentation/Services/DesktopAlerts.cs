using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// What happened at the finish — it picks the sound (see <see cref="AlertSoundSynth"/> for how each one sounds) and how
/// the Windows notification behaves.
/// </summary>
public enum FinishAlert
{
    /// <summary>A rental chip due back was read out — take it from the runner.</summary>
    CollectChip,

    /// <summary>An unknown chip was read out — its holder has to be picked at the computer.</summary>
    UnknownChip
}

/// <summary>
/// Signals that reach the finish judge when they are not looking at the screen, or when the window is minimized: a
/// sound, the taskbar button flashing (the Dock icon bouncing on macOS) and a Windows notification. Best-effort on every
/// OS — a failure is logged once and never thrown. Which alert fires when is the caller's choice (the judge's settings in
/// <see cref="IUiPreferencesService"/>).
/// </summary>
public interface IDesktopAlerts
{
    /// <summary>False where this OS has no way to play a sound for us.</summary>
    bool CanPlaySound { get; }

    /// <summary>False where this OS has no taskbar flash / Dock bounce for us.</summary>
    bool CanRequestAttention { get; }

    /// <summary>True on Windows 10 1809+, where <see cref="ShowNotification"/> shows a Windows notification.</summary>
    bool CanShowNotifications { get; }

    /// <summary>
    /// Plays an alert. Sounds never overlap: a different sound waits for the one playing; the same sound again while
    /// it still plays (or right after) is dropped, so a burst of reads gives one signal. Safe from any thread.
    /// </summary>
    void Play(FinishAlert sound);

    /// <summary>The «▶» of the settings: plays the sound now, whatever is playing.</summary>
    void Preview(FinishAlert sound);

    /// <summary>
    /// Flashes the taskbar button (bounces the Dock icon) until the window is activated — only while it is not the
    /// active window. Safe from any thread.
    /// </summary>
    void RequestAttention();

    /// <summary>
    /// The example button of «Налаштування сповіщень»: minimizes the window and then flashes its taskbar button (bounces
    /// the Dock icon) — the flash only shows on a window that is not in front, so the example puts it there. Safe from
    /// any thread.
    /// </summary>
    void PreviewAttention();

    /// <summary>
    /// Shows a silent Windows notification (the sound is <see cref="Play"/>'s job) — only while the window is not the
    /// active one, so it never doubles the in-window toast. A click on it brings the window to the front. Windows shows
    /// ordinary notifications one at a time, so they would queue: a <see cref="FinishAlert.CollectChip"/> one replaces the
    /// previous one still up or waiting (the newest is what matters — the runner is at the desk now); an
    /// <see cref="FinishAlert.UnknownChip"/> one is «important» (<c>scenario="urgent"</c>, Windows 10 2004+), shown beside
    /// the queue and, where the judge allows it, through «Не турбувати». Safe from any thread; a no-op where
    /// <see cref="CanShowNotifications"/> is false.
    /// </summary>
    void ShowNotification(FinishAlert kind, string title, string message);

    /// <summary>
    /// The example button of «Налаштування сповіщень»: the same Windows notification, shown even though the window is
    /// active. Safe from any thread; a no-op where <see cref="CanShowNotifications"/> is false.
    /// </summary>
    void PreviewNotification(FinishAlert kind, string title, string message);
}

/// <inheritdoc />
public sealed class DesktopAlerts : IDesktopAlerts
{
    /// <summary>Silences every alert — set by the headless test host, so a test run makes no sound.</summary>
    public static bool Muted { get; set; }

    // A pause between two queued sounds, and how long the same sound stays quiet after it played.
    private const long GapMs = 250;
    private const long SameSoundQuietMs = 1500;

    private readonly IActivityLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<FinishAlert, (string Path, long DurationMs)> _files = [];
    private FinishAlert? _lastSound;
    private long _lastEndMs;
    private string[] _linuxPlayers = ["paplay", "aplay"];
    private bool _soundFailureLogged;
    private bool _attentionFailureLogged;
    private bool _notificationFailureLogged;
    private bool _notificationsRegistered;

    public DesktopAlerts(IActivityLog log) => _log = log;

    public bool CanPlaySound => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public bool CanRequestAttention => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public bool CanShowNotifications => WindowsToasts.IsSupported;

    public void Play(FinishAlert sound) => Schedule(sound, preview: false);

    public void Preview(FinishAlert sound) => Schedule(sound, preview: true);

    private void Schedule(FinishAlert sound, bool preview)
    {
        if (Muted || !CanPlaySound)
            return;

        try
        {
            string path;
            long delayMs;
            lock (_gate)
            {
                var file = PrepareFile(sound);
                path = file.Path;
                var now = Environment.TickCount64;
                if (!preview && _lastSound == sound && now < _lastEndMs + SameSoundQuietMs)
                    return;

                var start = preview ? now : Math.Max(now, _lastEndMs);
                _lastSound = sound;
                _lastEndMs = start + file.DurationMs + GapMs;
                delayMs = start - now;
            }

            if (delayMs <= 0)
                Start(path);
            else
                _ = Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ContinueWith(_ => Start(path), TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            LogSoundFailure(ex);
        }
    }

    // The WAV is written once per build of the sounds into the temp folder (every OS player wants a file) and reused;
    // two app instances writing it at once both end up with the same complete file.
    private (string Path, long DurationMs) PrepareFile(FinishAlert sound)
    {
        if (_files.TryGetValue(sound, out var known))
            return known;

        var wav = AlertSoundSynth.Wav(sound);
        var folder = Path.Combine(Path.GetTempPath(), "OrientPyx", "sounds");
        var path = Path.Combine(folder, $"{sound}-v{AlertSoundSynth.Version}.wav");
        if (!File.Exists(path) || new FileInfo(path).Length != wav.Length)
        {
            Directory.CreateDirectory(folder);
            var temp = Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temp, wav);
                File.Move(temp, path, overwrite: true);
            }
            catch when (File.Exists(path) && new FileInfo(path).Length == wav.Length)
            {
                // Another instance wrote (and may be playing) the same file — use it.
            }
            finally
            {
                File.Delete(temp);
            }
        }

        var entry = (path, (long)AlertSoundSynth.Duration(wav).TotalMilliseconds);
        _files[sound] = entry;
        return entry;
    }

    private void Start(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!NativeMethods.PlaySound(path, IntPtr.Zero, NativeMethods.SndAsync | NativeMethods.SndNoDefault | NativeMethods.SndFilename))
                    throw new InvalidOperationException("PlaySound failed for " + path);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Launch("afplay", path);
                return;
            }

            StartOnLinux(path);
        }
        catch (Exception ex)
        {
            LogSoundFailure(ex);
        }
    }

    // The first player that starts is remembered; none at all ⇒ the error is logged once and the sound stays silent.
    private void StartOnLinux(string path)
    {
        foreach (var player in _linuxPlayers)
        {
            try
            {
                Launch(player, path);
                _linuxPlayers = [player];
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed — try the next one.
            }
        }

        throw new InvalidOperationException("No sound player found (paplay, aplay).");
    }

    private static void Launch(string player, string path)
    {
        var info = new ProcessStartInfo(player) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(path);
        using var _ = Process.Start(info);
    }

    public void RequestAttention()
    {
        if (Muted || !CanRequestAttention)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var window = MainWindow;
            if (window is not null && !window.IsActive)
                Flash(window);
        });
    }

    public void PreviewAttention()
    {
        if (Muted || !CanRequestAttention)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var window = MainWindow;
            if (window is null)
                return;

            window.WindowState = WindowState.Minimized;
            // A moment for the minimize to land, so the flash starts on a window that is no longer in front.
            DispatcherTimer.RunOnce(() => Flash(window), TimeSpan.FromMilliseconds(400));
        });
    }

    // On the UI thread: the taskbar button flashes until the window comes forward (the Dock icon bounces on macOS).
    private void Flash(Window window)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
                    NativeMethods.FlashUntilForeground(handle.Handle);
            }
            else if (OperatingSystem.IsMacOS())
            {
                NativeMethods.BounceDockIcon();
            }
        }
        catch (Exception ex)
        {
            if (_attentionFailureLogged)
                return;
            _attentionFailureLogged = true;
            _log.Error("DesktopAlerts.RequestAttention", ex);
        }
    }

    public void ShowNotification(FinishAlert kind, string title, string message) => Notify(kind, title, message, preview: false);

    public void PreviewNotification(FinishAlert kind, string title, string message) => Notify(kind, title, message, preview: true);

    private void Notify(FinishAlert kind, string title, string message, bool preview)
    {
        if (Muted || !CanShowNotifications)
            return;

        // On the UI thread: the window's state is read there, and the WinRT calls want its (STA) apartment.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var window = MainWindow;
                if (window is null || (window.IsActive && !preview))
                    return;

                RegisterNotifications();
                var collect = kind == FinishAlert.CollectChip;
                WindowsToasts.Show(title, message, () => Dispatcher.UIThread.Post(BringToFront),
                    replaceTag: collect ? "collect-chip" : null, important: !collect);
            }
            catch (Exception ex)
            {
                if (_notificationFailureLogged)
                    return;
                _notificationFailureLogged = true;
                _log.Error("DesktopAlerts.ShowNotification", ex);
            }
        });
    }

    // Once per launch: the app id under HKCU (name + icon) — the icon is the app's PNG written next to the sounds.
    private void RegisterNotifications()
    {
        if (_notificationsRegistered)
            return;

        string? icon = null;
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "OrientPyx");
            Directory.CreateDirectory(folder);
            icon = Path.Combine(folder, "notification-icon.png");
            using var source = AssetLoader.Open(new Uri("avares://OrientPyx/Assets/orientpyx.png"));
            using var target = File.Create(icon);
            source.CopyTo(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another instance holds the file (it is the same picture) or temp is unwritable — the toast still shows.
        }

        WindowsToasts.Register("OrientPyx", icon);
        _notificationsRegistered = true;
    }

    // The click on a notification: restore the window as it was (maximized stays maximized) and bring it forward. If
    // Windows refuses the foreground, the taskbar button flashes instead.
    private static void BringToFront()
    {
        var window = MainWindow;
        if (window is null)
            return;

        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
            NativeMethods.RestoreAndFocus(handle.Handle);
        window.Activate();
    }

    private static Window? MainWindow
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private void LogSoundFailure(Exception ex)
    {
        lock (_gate)
        {
            if (_soundFailureLogged)
                return;
            _soundFailureLogged = true;
        }

        _log.Error("DesktopAlerts.Play", ex);
    }

    private static class NativeMethods
    {
        public const uint SndAsync = 0x0001;
        public const uint SndNoDefault = 0x0002;
        public const uint SndFilename = 0x00020000;

        private const uint FlashwAll = 0x00000003;
        private const uint FlashwTimerNoFg = 0x0000000C;

        // NSRequestUserAttentionType: «critical» bounces until the app is activated.
        private const nint NsCriticalRequest = 0;
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PlaySound(string sound, IntPtr module, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashWindowInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FlashWindowInfo info);

        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        public static void RestoreAndFocus(IntPtr window)
        {
            if (IsIconic(window))
                ShowWindow(window, SwRestore);
            SetForegroundWindow(window);
        }

        public static void FlashUntilForeground(IntPtr window)
        {
            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                Window = window,
                Flags = FlashwAll | FlashwTimerNoFg,
            };
            FlashWindowEx(ref info);
        }

        [DllImport(ObjC)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern nint SendMessage(IntPtr receiver, IntPtr selector, nint argument);

        // [[NSApplication sharedApplication] requestUserAttention:NSCriticalRequest] — must run on the main thread,
        // which is Avalonia's UI thread on macOS.
        public static void BounceDockIcon()
        {
            var app = SendMessage(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != IntPtr.Zero)
                SendMessage(app, sel_registerName("requestUserAttention:"), NsCriticalRequest);
        }
    }
}
