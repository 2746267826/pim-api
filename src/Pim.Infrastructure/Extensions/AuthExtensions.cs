using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pim.Infrastructure.Auth;

namespace Pim.Infrastructure.Extensions;

public static class AuthExtensions
{
    public static IServiceCollection AddPimAuth(
        this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtService>((options, jwtService) =>
            {
                options.TokenValidationParameters =
                    jwtService.GetValidationParameters();
            });

        services.AddAuthorization();

        return services;
    }
}
