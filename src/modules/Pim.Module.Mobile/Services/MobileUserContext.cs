using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;

namespace Pim.Module.Mobile.Services;

internal static class MobileUserContext
{
    public static Guid RequireUserId(ICurrentUserService currentUser)
        => currentUser.UserId ?? throw new DomainException(6200, "Mobile endpoints require an authenticated user.");
}
