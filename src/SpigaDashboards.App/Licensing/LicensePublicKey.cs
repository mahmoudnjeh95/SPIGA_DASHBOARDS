namespace SpigaDashboards.App.Licensing;

// Public half of SPIGA's licence key pair (ECDSA P-256, SubjectPublicKeyInfo,
// base64): checks the dongles, cannot make them. The private half stays
// offline with whoever prepares the dongles (tools/SpigaLicenseTool keygen).
public static class LicensePublicKey
{
    public const string Value =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEu+l42DE6We6LglwlIVjT6U8s+VdkwBGAVtLRp7buVwXezaTgzvFcOuopLY2Baj8xLPzOJPOb5wEEagppXnqZjA==";
}
