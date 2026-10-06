using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ImageViewer;

/// <summary>
/// Registers the app with Windows as an image viewer for the current user only (HKEY_CURRENT_USER, no admin rights).
/// Windows 10 doesn't let apps make themselves the default; registering makes it appear in Settings → Default apps
/// and in "Open with", where the user picks it.
/// </summary>
internal static class DefaultAppRegistration
{
    private const string ProgId = "ImageViewer.Image";
    private const string AppName = "Image Viewer";
    private const string CapabilitiesKey = @"Software\ImageViewer\Capabilities";
    private static string ExeName => Path.GetFileName(Environment.ProcessPath!);

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
            return key?.GetValue(AppName) != null;
        }
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath!;
        var open = $"\"{exe}\" \"%1\"";
        var root = Registry.CurrentUser;

        using (var progId = root.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "Image");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue("", @"%SystemRoot%\System32\imageres.dll,-72", RegistryValueKind.ExpandString); // standard photo icon
            using (var command = progId.CreateSubKey(@"shell\open\command"))
                command.SetValue("", open);
        }

        using (var app = root.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var command = app.CreateSubKey(@"shell\open\command"))
                command.SetValue("", open);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in FolderNavigator.SupportedExtensions)
                types.SetValue(ext, "");
        }

        using (var capabilities = root.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "Lightweight image viewer with lossless rotation");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var ext in FolderNavigator.SupportedExtensions)
                associations.SetValue(ext, ProgId);
        }

        foreach (var ext in FolderNavigator.SupportedExtensions)
        {
            using var openWith = root.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var registered = root.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppName, CapabilitiesKey);

        NotifyAssociationsChanged();
    }

    /// <summary>Removes everything <see cref="Register"/> added.</summary>
    public static void Unregister()
    {
        var root = Registry.CurrentUser;
        using (var registered = root.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue(AppName, throwOnMissingValue: false);

        foreach (var ext in FolderNavigator.SupportedExtensions)
        {
            using var openWith = root.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        root.DeleteSubKeyTree(@"Software\ImageViewer", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree($@"Software\Classes\Applications\{ExeName}", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);

        NotifyAssociationsChanged();
    }

    public static void OpenDefaultAppsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });

    private static void NotifyAssociationsChanged() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
