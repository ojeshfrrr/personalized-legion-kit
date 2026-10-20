namespace LenovoLegionToolkit.Lib;

/// <summary>Project-specific identity and integrations, separate from the hardware layer.</summary>
public static class ProjectBranding
{
    public const string ProductName = "Project Legion";
    public const string AppDataFolderName = "ProjectLegion";

    // Leave blank until this fork has an owned release repository.
    public const string UpdateGitHubOwner = "";
    public const string UpdateGitHubRepository = "";
    public const string UpdateTrustedSignerThumbprint = "";

    public static bool HasUpdateFeed =>
        !string.IsNullOrWhiteSpace(UpdateGitHubOwner) &&
        !string.IsNullOrWhiteSpace(UpdateGitHubRepository) &&
        !string.IsNullOrWhiteSpace(UpdateTrustedSignerThumbprint);
}
