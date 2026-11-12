using System;
using System.Threading.Tasks;

namespace PalladiumWallet.App.Services;

/// <summary>
/// Seam for platform-specific services (Android/desktop).
/// The Android head sets the delegates in OnCreate; desktop leaves them null.
/// </summary>
public static class PlatformServices
{
    /// <summary>
    /// Opens the native QR scanner and returns the raw code text,
    /// or null if the user cancels or the scanner is unavailable.
    /// </summary>
    public static Func<Task<string?>>? ScanQrAsync { get; set; }

    /// <summary>
    /// Whether the operating system is in dark mode right now, for a head whose OS setting Avalonia does not follow
    /// while the app runs (Android: the activity handles the UI-mode change itself, so it is not recreated and the
    /// framework is not told). Null where Avalonia follows the system on its own (desktop). With the "System" theme
    /// the view model asks this on start and again when the head reports a change.
    /// </summary>
    public static Func<bool>? IsSystemDark { get; set; }
}
