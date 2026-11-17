namespace Spiga.Core.Security;

// Persistence contracts, implemented by Spiga.Data.

public interface IRoleStore
{
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
    Task<IReadOnlyList<AppUser>> ListAsync();
    Task<AppUser?> GetAsync(int id);
    Task<AppUser?> GetByLoginAsync(string login);
    Task<int> InsertAsync(AppUser user);
    Task UpdateAsync(AppUser user);
}

public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key) where T : class;
    Task SetAsync<T>(string key, T value, string updatedBy) where T : class;
}

public static class SettingKeys
{
    public const string SecurityPolicy = "security.policy";
}
