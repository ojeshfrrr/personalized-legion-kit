using System;
using System.IO;
using System.Linq;
using LenovoLegionToolkit.Lib;

namespace LenovoLegionToolkit.Lib.Utils;

public static class Folders
{
    private static string? _readOnlyProfile;
    private static bool _isReadOnlySession;
    public static bool IsIsolatedProfile => _readOnlyProfile is not null;
    public static bool IsReadOnlySession => _isReadOnlySession;

    // Command-line only: never import the user's profiles or external startup flags into QA.
    public static void ConfigureReadOnlyProfile(string[] args)
    {
        _readOnlyProfile = null;
        _isReadOnlySession = Array.Exists(args, a => a == "--read-only" || a == "--ui-preview");

        var option = Array.Find(args, a => a.StartsWith("--qa-profile=", StringComparison.Ordinal));
        if (option is null) return;
        if (!Array.Exists(args, a => a == "--read-only"))
            throw new ArgumentException("--qa-profile requires --read-only.");
        var path = option["--qa-profile=".Length..];
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("QA profile must be an absolute path.");
        path = Path.GetFullPath(path);
        var marker = Path.Combine(path, ".project-legion-qa-profile");
        if (Directory.Exists(path) && !File.Exists(marker) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new ArgumentException("QA profile must be empty or previously created for QA.");
        Directory.CreateDirectory(path);
        if (!File.Exists(marker))
            File.WriteAllText(marker, "Isolated read-only QA profile; no upstream settings migration.");
        _readOnlyProfile = path;
    }

    public static void EnsureParentDirectoryExists(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var folderPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }
    }

    public static string Program => AppDomain.CurrentDomain.SetupInformation.ApplicationBase ?? string.Empty;

    public static string AppData
    {
        get
        {
            if (_readOnlyProfile is not null) return _readOnlyProfile;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folderPath = Path.Combine(appData, ProjectBranding.AppDataFolderName);
            if (!IsReadOnlySession)
            {
                Directory.CreateDirectory(folderPath);
                MigrateSettings(appData, folderPath);
            }

            return folderPath;
        }
    }

    public static string Temp
    {
        get
        {
            var appData = Path.GetTempPath();
            var folderPath = Path.Combine(appData, ProjectBranding.AppDataFolderName);
            Directory.CreateDirectory(folderPath);
            return folderPath;
        }
    }

    private static void MigrateSettings(string appData, string destination)
    {
        var marker = Path.Combine(destination, ".upstream-settings-migrated");
        if (File.Exists(marker))
            return;

        var legacyFolder = Path.Combine(appData, "LenovoLegionToolkit");
        try
        {
            if (Directory.Exists(legacyFolder))
            {
                // Copy settings only; never remove or overwrite the upstream application's data.
                foreach (var source in Directory.EnumerateFiles(legacyFolder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Copy(source, Path.Combine(destination, Path.GetFileName(source)), overwrite: false);
                    }
                    catch (IOException) { /* Keep the fork usable if a setting is locked or already exists. */ }
                    catch (UnauthorizedAccessException) { /* Settings migration is best-effort. */ }
                }
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch (IOException) { /* Keep the fork usable if migration metadata cannot be written. */ }
        catch (UnauthorizedAccessException) { /* Settings migration is best-effort. */ }
    }
}
