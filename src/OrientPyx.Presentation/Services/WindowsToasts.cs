using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Windows toast notifications («сповіщення Windows») for an unpackaged app, without the Windows SDK projection (which
/// would need a <c>net10.0-windows…</c> target and ~25 MB more in the package): a few WinRT interfaces declared as
/// classic COM interop (<c>Windows.UI.Notifications</c>, <c>Windows.Data.Xml.Dom</c>) over <c>combase.dll</c>.
/// <para>
/// No Start-menu shortcut is needed: the app id is registered under
/// <c>HKCU\Software\Classes\AppUserModelId\OrientPyx</c> (display name + icon), as the Windows Community Toolkit and
/// Avalonia.Labs.Notifications do. The toast is always silent (<c>&lt;audio silent="true"/&gt;</c>) — the finish alerts
/// play their own sounds. A click while the app runs is reported through the toast's <c>Activated</c> event. Windows
/// shows ordinary toasts one at a time (the rest queue), so a toast can replace its predecessor by tag or be «important».
/// </para>
/// Windows 10 1809+ only (<see cref="IsSupported"/>); every call is best-effort and reports failure by exception for the
/// caller to log. Call <see cref="Show"/> on the UI (STA) thread; the callbacks arrive on a worker thread.
/// </summary>
public static class WindowsToasts
{
    /// <summary>The id the toasts are shown under — one entry «OrientPyx» in Windows' notification settings.</summary>
    public const string AppUserModelId = "OrientPyx";

    private const string RegistryKey = @"Software\Classes\AppUserModelId\" + AppUserModelId;

    // The group of every OrientPyx toast; with a tag it makes a later toast replace an earlier one.
    private const string Group = "finish";

    // Toasts still on screen keep their in-process object (and so their events) alive until they are clicked,
    // dismissed or fail; a cap keeps a long session from accumulating them.
    private const int MaxLive = 20;
    private static readonly ConcurrentDictionary<int, object> Live = new();
    private static int _nextId;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>
    /// <c>scenario="urgent"</c> («важливе сповіщення») is accepted from Windows 10 2004 (build 19041); older builds get
    /// an ordinary toast.
    /// </summary>
    public static bool SupportsImportant => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>
    /// Registers (or refreshes) the app id: the name Windows shows on the toast and in its settings, and the icon.
    /// Cheap and idempotent — call it once per launch before the first toast.
    /// </summary>
    public static void Register(string displayName, string? iconPath)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
        key.SetValue("DisplayName", displayName);
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            key.SetValue("IconUri", iconPath);
            key.SetValue("IconBackgroundColor", "00000000");
        }
        else
        {
            key.DeleteValue("IconUri", throwOnMissingValue: false);
        }
    }

    /// <summary>Removes the app id from the registry (the uninstall hook). Never throws.</summary>
    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // Leaving a stale key behind is harmless.
        }
    }

    /// <summary>
    /// The toast's XML: a title line, a message line, no sound of its own; <paramref name="important"/> adds
    /// <c>scenario="urgent"</c>.
    /// </summary>
    public static string BuildXml(string title, string message, bool important = false)
        => "<toast launch=\"orientpyx\"" + (important ? " scenario=\"urgent\"" : "") + ">"
           + "<visual><binding template=\"ToastGeneric\">"
           + $"<text>{SecurityElement.Escape(title)}</text>"
           + $"<text>{SecurityElement.Escape(message)}</text>"
           + "</binding></visual>"
           + "<audio silent=\"true\"/>"
           + "</toast>";

    /// <summary>
    /// Shows a toast. <paramref name="onActivated"/> runs (on a worker thread) when the judge clicks it while the app is
    /// running. A <paramref name="replaceTag"/> makes it replace the earlier toast with the same tag — on screen, waiting
    /// in Windows' queue (ordinary toasts are shown one at a time) or in the notification centre. An
    /// <paramref name="important"/> toast is <c>scenario="urgent"</c> where <see cref="SupportsImportant"/>. Throws when
    /// Windows refuses (notifications switched off for the app are NOT an error — Windows then just drops the toast).
    /// </summary>
    public static void Show(string title, string message, Action? onActivated, string? replaceTag = null, bool important = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        var xml = CreateXmlDocument(BuildXml(title, message, important && SupportsImportant));
        var factory = (IToastNotificationFactory)ActivationFactory("Windows.UI.Notifications.ToastNotification",
            typeof(IToastNotificationFactory).GUID);
        var toast = factory.CreateToastNotification(xml);
        if (replaceTag is not null)
            SetTag(toast, replaceTag);

        var id = Interlocked.Increment(ref _nextId);
        var events = new ToastEvents(() =>
        {
            Live.TryRemove(id, out _);
            return true;
        }, onActivated);
        Subscribe(toast, events);
        Live[id] = toast;
        foreach (var stale in Live.Keys.Where(k => k <= id - MaxLive))
            Live.TryRemove(stale, out _);

        var statics = (IToastNotificationManagerStatics)ActivationFactory("Windows.UI.Notifications.ToastNotificationManager",
            typeof(IToastNotificationManagerStatics).GUID);
        using var appId = new HString(AppUserModelId);
        var notifier = statics.CreateToastNotifierWithId(appId.Handle);
        notifier.Show(toast);
    }

    // Both events hand the toast a COM delegate each (a CCW over one managed object); the toast holds them. (A toast
    // that fails to show raises neither — the cap on Live lets it go.)
    [SupportedOSPlatform("windows")]
    private static void Subscribe(IToastNotification toast, ToastEvents events)
    {
        var activated = Marshal.GetComInterfaceForObject<ToastEvents, IActivatedHandler>(events);
        var dismissed = Marshal.GetComInterfaceForObject<ToastEvents, IDismissedHandler>(events);
        try
        {
            toast.AddActivated(activated);
            toast.AddDismissed(dismissed);
        }
        finally
        {
            Marshal.Release(activated);
            Marshal.Release(dismissed);
        }
    }

    // Tag + group (IToastNotification2): the key Windows replaces an earlier toast by.
    [SupportedOSPlatform("windows")]
    private static void SetTag(IToastNotification toast, string tag)
    {
        var tagged = (IToastNotification2)toast;
        using var tagText = new HString(tag);
        using var groupText = new HString(Group);
        tagged.PutTag(tagText.Handle);
        tagged.PutGroup(groupText.Handle);
    }

    [SupportedOSPlatform("windows")]
    private static IXmlDocument CreateXmlDocument(string xml)
    {
        using var className = new HString("Windows.Data.Xml.Dom.XmlDocument");
        Check(NativeMethods.RoActivateInstance(className.Handle, out var instance));
        try
        {
            var document = Marshal.GetObjectForIUnknown(instance);
            using var text = new HString(xml);
            ((IXmlDocumentIO)document).LoadXml(text.Handle);
            return (IXmlDocument)document;
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    [SupportedOSPlatform("windows")]
    private static object ActivationFactory(string className, Guid iid)
    {
        using var name = new HString(className);
        Check(NativeMethods.RoGetActivationFactory(name.Handle, ref iid, out var factory));
        try
        {
            return Marshal.GetObjectForIUnknown(factory);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    private readonly struct HString : IDisposable
    {
        public HString(string value)
            => Check(NativeMethods.WindowsCreateString(value, value.Length, out Handle));

        public readonly IntPtr Handle;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                NativeMethods.WindowsDeleteString(Handle);
        }
    }

    // One object behind both event delegates: a click runs the callback; either event ends the toast's life.
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ToastEvents(Func<bool> forget, Action? onActivated) : IActivatedHandler, IDismissedHandler
    {
        int IActivatedHandler.Invoke(IntPtr sender, IntPtr args)
        {
            forget();
            try
            {
                onActivated?.Invoke();
            }
            catch
            {
                // A callback must never throw back into Windows.
            }

            return 0;
        }

        int IDismissedHandler.Invoke(IntPtr sender, IntPtr args)
        {
            forget();
            return 0;
        }
    }

    private static class NativeMethods
    {
        [DllImport("combase.dll", PreserveSig = true)]
        public static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        public static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        public static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = true)]
        public static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);
    }

    // --- WinRT interfaces as classic COM interop. Each starts with the three IInspectable slots (never called), then
    // the interface's own methods in vtable order. Only the members used here are typed; the rest are placeholders.

    [ComImport]
    [Guid("50AC103F-D235-4598-BBEF-98FE4D1A3AD4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationManagerStatics
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        IToastNotifier CreateToastNotifier();
        IToastNotifier CreateToastNotifierWithId(IntPtr applicationId);
    }

    [ComImport]
    [Guid("75927B93-03F3-41EC-91D3-6E5BAC1B38E7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotifier
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void Show(IToastNotification notification);
    }

    [ComImport]
    [Guid("04124B20-82C6-4229-B109-FD9ED4662B53")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationFactory
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        IToastNotification CreateToastNotification(IXmlDocument content);
    }

    [ComImport]
    [Guid("997E2675-059E-4E60-8B06-1760917C8B80")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotification
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void GetContent();
        void PutExpirationTime();
        void GetExpirationTime();
        long AddDismissed(IntPtr handler);
        void RemoveDismissed(long token);
        long AddActivated(IntPtr handler);
        void RemoveActivated(long token);
        long AddFailed(IntPtr handler);
        void RemoveFailed(long token);
    }

    [ComImport]
    [Guid("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotification2
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void PutTag(IntPtr value);
        void GetTag();
        void PutGroup(IntPtr value);
    }

    [ComImport]
    [Guid("6CD0E74E-EE65-4489-9EBF-CA43E87BA637")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocumentIO
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void LoadXml(IntPtr xml);
    }

    // Only passed along (to CreateToastNotification) — its methods are never called.
    [ComImport]
    [Guid("F7F3A506-1E87-42D6-BCFB-B8C809FA5494")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocument
    {
    }

    // WinRT delegates are plain IUnknown interfaces with one Invoke(sender, args). The IIDs are those of the
    // parameterised TypedEventHandler<ToastNotification, …> instances (SHA-1 of the WinRT type signature). Public, so
    // COM can see them (a managed object is handed to Windows through them); not meant for other code.

    /// <summary>COM delegate type of <c>ToastNotification.Activated</c> (interop only).</summary>
    [ComVisible(true)]
    [Guid("AB54DE2D-97D9-5528-B6AD-105AFE156530")] // TypedEventHandler<ToastNotification, object>
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivatedHandler
    {
        [PreserveSig]
        int Invoke(IntPtr sender, IntPtr args);
    }

    /// <summary>COM delegate type of <c>ToastNotification.Dismissed</c> (interop only).</summary>
    [ComVisible(true)]
    [Guid("61C2402F-0ED0-5A18-AB69-59F4AA99A368")] // TypedEventHandler<ToastNotification, ToastDismissedEventArgs>
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDismissedHandler
    {
        [PreserveSig]
        int Invoke(IntPtr sender, IntPtr args);
    }
}
