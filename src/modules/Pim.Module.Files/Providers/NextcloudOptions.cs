namespace Pim.Module.Files.Providers;

public sealed class NextcloudOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
