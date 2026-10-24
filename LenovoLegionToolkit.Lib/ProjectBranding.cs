namespace LenovoLegionToolkit.Lib;

/// <summary>Project-specific identity and integrations, separate from the hardware layer.</summary>
public static class ProjectBranding
{
    public const string ProductName = "Project Legion";
    public const string AppDataFolderName = "ProjectLegion";

    public const string UpdateGitHubOwner = "ojeshfrrr";
    public const string UpdateGitHubRepository = "personalized-legion-kit";
    public const string UpdateTrustedSignerThumbprint = "";

    public static bool HasUpdateFeed =>
        !string.IsNullOrWhiteSpace(UpdateGitHubOwner) &&
        !string.IsNullOrWhiteSpace(UpdateGitHubRepository) &&
        !string.IsNullOrWhiteSpace(UpdateTrustedSignerThumbprint);
    public static string ReleasesUrl => $"https://github.com/{UpdateGitHubOwner}/{UpdateGitHubRepository}/releases";
}
