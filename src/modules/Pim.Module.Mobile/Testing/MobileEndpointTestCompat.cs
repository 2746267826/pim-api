using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Pim.UnitTests.Mobile;

public sealed class ServiceCollection : Microsoft.Extensions.DependencyInjection.ServiceCollection;

public sealed class ConfigurationBuilder : Microsoft.Extensions.Configuration.ConfigurationBuilder;

public sealed class WebApplication : IEndpointRouteBuilder
{
    private readonly Microsoft.AspNetCore.Builder.WebApplication _inner;

    private WebApplication(Microsoft.AspNetCore.Builder.WebApplication inner)
    {
        _inner = inner;
    }

    public static MobileWebApplicationBuilder CreateBuilder()
        => new(Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder());

    public ICollection<EndpointDataSource> DataSources => ((IEndpointRouteBuilder)_inner).DataSources;

    IServiceProvider IEndpointRouteBuilder.ServiceProvider => ((IEndpointRouteBuilder)_inner).ServiceProvider;

    IApplicationBuilder IEndpointRouteBuilder.CreateApplicationBuilder()
        => ((IEndpointRouteBuilder)_inner).CreateApplicationBuilder();

    public sealed class MobileWebApplicationBuilder
    {
        private readonly WebApplicationBuilder _inner;

        internal MobileWebApplicationBuilder(WebApplicationBuilder inner)
        {
            _inner = inner;
        }

        public IServiceCollection Services => _inner.Services;

        public WebApplication Build() => new(_inner.Build());
    }
}

public static class MobileEndpointTestServiceCollectionExtensions
{
    public static IServiceCollection AddRouting(this IServiceCollection services)
        => RoutingServiceCollectionExtensions.AddRouting(services);

    public static IServiceCollection AddAuthorization(this IServiceCollection services)
        => services;
}
