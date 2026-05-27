namespace RadiologyPlus.Core.Identity;

public enum Role
{
    /// <summary>Local-only super-user. Sees everything plus the scripting engine.</summary>
    NRS = 1,

    /// <summary>Federated. Sees everything except scripting engine. Cannot change NRS password.</summary>
    Admin = 2,

    /// <summary>Federated. Tech Validation only.</summary>
    Tech = 3,

    /// <summary>Federated. Rad Validation only.</summary>
    Radiologist = 4,
}

public static class RoleExtensions
{
    public static bool CanAccessScripting(this Role role) => role == Role.NRS;

    public static bool CanAccessAdmin(this Role role) => role is Role.NRS or Role.Admin;

    public static bool CanAccessTechValidation(this Role role) =>
        role is Role.NRS or Role.Admin or Role.Tech;

    public static bool CanAccessBilling(this Role role) =>
        role is Role.NRS or Role.Admin;

    public static bool CanAccessRadValidation(this Role role) =>
        role is Role.NRS or Role.Admin or Role.Radiologist;
}
