using Avalonia.Platform.Storage;

namespace ECAD.Desktop;

public static class WorkspaceFolders
{
    private static readonly Lazy<string> Root = new(ResolveRoot);
    public static string RootDirectory => Root.Value;
    public static string ProjectsDirectory => Ensure(Path.Combine(RootDirectory, "Projects"));
    public static string LibrariesDirectory => Ensure(Path.Combine(RootDirectory, "Libraries"));

    public static Task<IStorageFolder?> Projects(IStorageProvider provider) =>
        provider.TryGetFolderFromPathAsync(ProjectsDirectory);
    public static Task<IStorageFolder?> Libraries(IStorageProvider provider) =>
        provider.TryGetFolderFromPathAsync(LibrariesDirectory);

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ECAD_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Ensure(Path.GetFullPath(configured));

        var application = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (application.Name.Equals("App", StringComparison.OrdinalIgnoreCase) && application.Parent is { } package)
            return Ensure(package.FullName);

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Ensure(Path.Combine(string.IsNullOrWhiteSpace(documents) ? AppContext.BaseDirectory : documents, "ECAD"));
    }

    private static string Ensure(string path) { Directory.CreateDirectory(path); return path; }
}
