namespace Spiga.Core.Security;

// Site-wide security rules, edited in Utilisateurs et rôles > Sécurité.
public sealed class SecurityPolicy
{
    public int MinPasswordLength { get; set; } = 10;
    public bool RequireLetterAndDigit { get; set; } = true;
    public int MaxFailedAttempts { get; set; } = 5;

    // 0 = the account stays locked until an administrator unlocks it.
    public int LockoutMinutes { get; set; }

    // The screen locks after this much inactivity; 0 = never.
    public int SessionTimeoutMinutes { get; set; } = 15;

    // A waste entry must be validated by someone other than its author.
    public bool RequireSecondValidator { get; set; } = true;

    public string? CheckPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"Le mot de passe doit contenir au moins {MinPasswordLength} caractères.";
        if (RequireLetterAndDigit && (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)))
            return "Le mot de passe doit contenir au moins une lettre et un chiffre.";
        return null;
    }

    public string? CheckSelf()
    {
        if (MinPasswordLength is < 8 or > 64)
            return "La longueur minimale doit être comprise entre 8 et 64.";
        if (MaxFailedAttempts is < 3 or > 20)
            return "Le nombre d'échecs avant blocage doit être compris entre 3 et 20.";
        if (LockoutMinutes is < 0 or > 1440)
            return "La durée de blocage doit être comprise entre 0 et 1440 minutes.";
        if (SessionTimeoutMinutes is < 0 or > 480)
            return "Le verrouillage de session doit être compris entre 0 et 480 minutes.";
        return null;
    }

    public SecurityPolicy Clone() => (SecurityPolicy)MemberwiseClone();

    public string Describe()
        => $"mot de passe ≥ {MinPasswordLength} car.{(RequireLetterAndDigit ? " lettre+chiffre" : "")}, " +
           $"blocage après {MaxFailedAttempts} échecs ({(LockoutMinutes == 0 ? "déblocage par un administrateur" : $"{LockoutMinutes} min")}), " +
           $"verrouillage {(SessionTimeoutMinutes == 0 ? "désactivé" : $"après {SessionTimeoutMinutes} min")}, " +
           $"second validateur {(RequireSecondValidator ? "obligatoire" : "facultatif")}";
}
