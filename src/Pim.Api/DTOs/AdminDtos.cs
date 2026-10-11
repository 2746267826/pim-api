using System.ComponentModel.DataAnnotations;

namespace Pim.Api.DTOs;

public record UpdateUserRoleRequest(
    [property:Required][property:MaxLength(20)] string Role
);

public record UpdateUserStatusRequest(
    [property:Required] bool IsActive
);
