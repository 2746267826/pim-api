using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Modules;
using Pim.Infrastructure.Data;
using Pim.Module.QuickNotes.Services;

namespace Pim.Module.QuickNotes;

public class QuickNotesModule : IModule
{
    public string Name => "quick-notes";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        services.AddScoped<IQuickNoteObjectStorage, MinioQuickNoteObjectStorage>();
        services.AddScoped<QuickNoteAttachmentService>();
        services.AddScoped<QuickNoteService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        await Task.CompletedTask;
    }
}
