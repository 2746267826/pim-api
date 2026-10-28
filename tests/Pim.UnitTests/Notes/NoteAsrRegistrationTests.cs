using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Notes;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteAsrRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledOrUnconfiguredHangfireDoesNotBreakValidatedApiHost(bool explicitlyDisabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "pim-note-asr-registration", Guid.NewGuid().ToString("N"));
        try
        {
            // Development enables container validation; Test environment intentionally does not.
            await using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("note-asr-registration"))
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development").UseSetting("DataProtection:KeysPath", Path.Combine(root, "keys"));
                    builder.UseSetting("DisableHangfire", explicitlyDisabled ? "true" : "false");
                    if (!explicitlyDisabled) builder.UseSetting("ConnectionStrings:DefaultConnection", "");
                });
            using var client = factory.CreateClient();
            var version = await client.GetAsync("/api/version"); version.EnsureSuccessStatusCode();
            using var scope = factory.Services.CreateScope();
            Assert.Null(scope.ServiceProvider.GetService<NoteAsrDispatchJob>());
            Assert.NotNull(scope.ServiceProvider.GetService<NoteAsrJob>());
            Assert.NotNull(scope.ServiceProvider.GetService<NoteAsrService>());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
