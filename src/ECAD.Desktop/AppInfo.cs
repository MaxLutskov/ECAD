using System.Reflection;

namespace ECAD.Desktop;

public static class AppInfo
{
    public static string BuildVersion { get; } = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static string Version { get; } = BuildVersion.Split('+')[0];
}
