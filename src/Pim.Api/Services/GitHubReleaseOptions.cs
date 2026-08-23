namespace Pim.Api.Services;

public class GitHubReleaseOptions
{
    public string Repo { get; set; } = "2746267826/pim-platform";
    public string? Token { get; set; }
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(6);
}
