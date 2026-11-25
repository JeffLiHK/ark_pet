using System;
using System.IO;
using Microsoft.Win32;

namespace ArtPet.Windows.Desktop;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ArtPet";

    internal static bool CanRegister => Path.GetFileName(Environment.ProcessPath) is { } name &&
        (name.Equals(ReleaseUpdater.ExecutableName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ArtPet.Windows.exe", StringComparison.OrdinalIgnoreCase));

    internal static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        if (!CanRegister)
        {
            throw new InvalidOperationException();
        }

        using RegistryKey startup = Registry.CurrentUser.CreateSubKey(RunKey);
        startup.SetValue(ValueName, $"\"{Environment.ProcessPath}\"", RegistryValueKind.String);
    }
}
