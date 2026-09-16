using System.Globalization;
using System.Resources;

namespace ECAD.Desktop;

internal static class WorkspaceText
{
    // Ukrainian is the product's default. Additional cultures can use resx satellites.
    private static readonly ResourceManager Resources = new("ECAD.Desktop.Workspace.WorkspaceStrings", typeof(WorkspaceText).Assembly);
    public static string Get(string key, string fallback) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? fallback;
}
