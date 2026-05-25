using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Pim.Infrastructure.Data;

public sealed class PimDbContextModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is PimDbContext)
        {
            return (context.GetType(), designTime, PimDbContext.ModuleAssemblySignature);
        }

        return (context.GetType(), designTime);
    }
}
