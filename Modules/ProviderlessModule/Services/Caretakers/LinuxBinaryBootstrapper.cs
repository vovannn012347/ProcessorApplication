using System.Diagnostics;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Caretakers;

public class LinuxProcessCaretaker : IProcessCaretaker
{
    public void EnforceParentalControl(Process process)
    {
        // On Linux, we rely on the Process Tree Kill during disposal
        // This is the most reliable cross-platform 'automatic' method
        AppDomain.CurrentDomain.ProcessExit += (s, e) => {
            if (!process.HasExited) process.Kill(true);
        };
    }
}