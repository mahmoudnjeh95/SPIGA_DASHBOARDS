namespace Spiga.Core.Security;

// Persistence contracts, implemented by Spiga.Data.

public interface IRoleStore
{
    // With permissions, assignable roles and holder counts.
    Task<IReadOnlyList<Role>> ListAsync();
    Task<Role?> GetAsync(int id);
    Task<Role?> GetByCodeAsync(string code);
    Task<int> InsertAsync(Role role);
    Task UpdateAsync(Role role);
    Task DeleteAsync(int id);
}

public interface IUserStore
{
    Task<int> CountAsync();

    // With their role assignments.
    Task<IReadOnlyList<AppUser>> ListAsync();
    Task<AppUser?> GetAsync(int id);
    Task<AppUser?> GetByLoginAsync(string login);
    Task<int> InsertAsync(AppUser user);

    // The account row only (sign-in counters, password, name, state).
    Task UpdateAsync(AppUser user);

    Task SetAssignmentsAsync(int userId, IReadOnlyList<RoleAssignment> assignments);
}

public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key) where T : class;
    Task SetAsync<T>(string key, T value, string updatedBy) where T : class;
}

public static class SettingKeys
{
    public const string SecurityPolicy = "security.policy";
    public const string OvertimeRule = "pointage.heures-sup";
}
