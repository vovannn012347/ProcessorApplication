using System.ComponentModel.DataAnnotations;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Configuration.Registry;

public class GithubSettings : IRegistrySettings
{
    public string ProviderKey => "github";

    public string ProviderName => "GithubSettings_key";
    public string SettingsViewPath => "~/Views/Providerless/Settings/_Settings_Reg_github.cshtml";


    [Required]
    public string GitHubUrl { get; set; }

    /// <summary>
    /// Fine-grained PAT with write access to the DATA repository.
    /// </summary>
    [Required]
    [DataType(DataType.Password)]
    public string GitHubToken { get; set; }

    /// <summary>
    /// The repository storing JSON files (e.g., 'user/medical-portal-data').
    /// </summary>
    [Required]
    public string RepositoryName { get; set; }

    /// <summary>
    /// The URL of your hosted static resolver site (e.g., 'https://user.github.io/resolver/').
    /// </summary>
    [Required]
    public string ResolverSiteUrl { get; set; }

    public string Branch { get; set; }

    public string RegistryPath { get; set; }

    /// <summary>
    /// How often (in hours) to force a registry update even if the URL is the same.
    /// </summary>
    public double ForceUpdateIntervalHours { get; set; } = 24;
}
