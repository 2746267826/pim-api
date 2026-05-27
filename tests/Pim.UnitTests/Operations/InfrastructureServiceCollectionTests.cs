using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pim.Infrastructure.Extensions;
using Xunit;

namespace Pim.UnitTests.Operations;

public class InfrastructureServiceCollectionTests
{
    [Fact]
    public void AddPimInfrastructure_ConfiguresDurableDataProtectionKeyRing()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), $"pim-data-protection-{Guid.NewGuid()}");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=pim;Username=pim;Password=pim",
                    ["Minio:Endpoint"] = "localhost:9000",
                    ["Minio:AccessKey"] = "minioadmin",
                    ["Minio:SecretKey"] = "minioadmin",
                    ["Kopia:RepositoryPath"] = "./data/kopia-repo",
                    ["Kopia:Password"] = "kopia_password",
                    ["Tika:BaseUrl"] = "http://localhost:9998",
                    ["DataProtection:KeysPath"] = keysPath
                })
                .Build();
            var services = new ServiceCollection();

            services.AddPimInfrastructure(configuration);

            Assert.True(Directory.Exists(keysPath));

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
            Assert.Equal("FileSystemXmlRepository", options.XmlRepository?.GetType().Name);

            var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
            Assert.False(string.IsNullOrWhiteSpace(protector.Protect("probe")));
            Assert.NotEmpty(Directory.GetFiles(keysPath, "*.xml"));
        }
        finally
        {
            if (Directory.Exists(keysPath))
                Directory.Delete(keysPath, recursive: true);
        }
    }
}
