using System.Security.Principal;

namespace ConnectionManager.Integration.Tests;

/// Skips the test unless it runs from an elevated (admin) prompt. WFP sessions need admin rights.
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "Needs an elevated (admin) prompt";
    }
}
