using System.ComponentModel.DataAnnotations;
using System.Diagnostics;

using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;

namespace ProviderlessModule.Infrastructure.Interfaces;

public interface IProcessCaretaker
{
    /// <summary>
    /// Instructs the OS to automatically terminate the process 
    /// if the parent (this app) unexpectedly exits.
    /// </summary>
    void EnforceParentalControl(Process process);
}
