using System.ComponentModel.DataAnnotations;

namespace Pim.Api.DTOs;

public record UpdateUserRoleRequest(
    [Required][MaxLength(20)] string Role
);

public record UpdateUserStatusRequest(
    [Required] bool IsActive
);
