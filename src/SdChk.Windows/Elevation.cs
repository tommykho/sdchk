// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security.Principal;

namespace SdChk.Windows;

/// <summary>Raw drive access needs Administrator; let the UI detect that and offer a restart.</summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts this program again with a UAC prompt. Throws <see cref="System.ComponentModel.Win32Exception"/> if declined.</summary>
    public static void RelaunchElevated(string arguments)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown program path."), arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        Process.Start(psi);
    }
}
