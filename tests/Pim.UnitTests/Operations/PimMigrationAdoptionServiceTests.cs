using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Operations;

public class PimMigrationAdoptionServiceTests
{
    [Fact]
    public void NeedsBaselineAdoption_ReturnsFalse_WhenNoUsersTableExists()
    {
        Assert.False(PimMigrationAdoptionService.NeedsBaselineAdoption(false, false));
    }

    [Fact]
    public void NeedsBaselineAdoption_ReturnsTrue_WhenUsersTableExistsWithoutHistory()
    {
        Assert.True(PimMigrationAdoptionService.NeedsBaselineAdoption(true, false));
    }

    [Fact]
    public void NeedsBaselineAdoption_ReturnsFalse_WhenHistoryAlreadyExists()
    {
        Assert.False(PimMigrationAdoptionService.NeedsBaselineAdoption(true, true));
    }

    [Fact]
    public void BaselineMigrationId_IsStable()
    {
        Assert.Equal("20260524000000_BaselineExistingSchema", PimMigrationAdoptionService.BaselineMigrationId);
    }
}
