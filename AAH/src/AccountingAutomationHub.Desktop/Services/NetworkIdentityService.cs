using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class NetworkIdentityService
{
    private const int PasswordIterations = 350_000;
    private readonly SqliteGovernanceRepository _governance;
    private readonly ServerSecurityRepository _serverSecurity;
    private readonly NetworkSecretProtector _protector;

    public NetworkIdentityService(ApplicationPreferenceService settings, SqliteGovernanceRepository governance, ServerSecurityRepository serverSecurity)
    {
        _governance = governance;
        _serverSecurity = serverSecurity;
        _protector = new NetworkSecretProtector(settings);
    }

    public MfaSetup CreateMfaSetup(string userName)
    {
        var normalized = NormalizeUser(userName);
        EnsureApplicationUser(normalized);
        var secret = Base32Encode(RandomNumberGenerator.GetBytes(20));
        var issuer = Uri.EscapeDataString("Accounting Automation Hub");
        var label = Uri.EscapeDataString($"Accounting Automation Hub:{normalized}");
        var uri = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
        return new MfaSetup(secret, uri);
    }

    public void ConfigurePassword(string userName, string password)
    {
        var normalized = NormalizeUser(userName);
        EnsureApplicationUser(normalized);
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        _serverSecurity.SaveCredential(new NetworkCredentialRecord
        {
            UserName = normalized,
            PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash),
            PasswordIterations = PasswordIterations,
            TotpSecretCipher = string.Empty,
            MfaEnabled = true, // Main-server challenge OTP is mandatory even though no authenticator secret is stored.
            FailedAttempts = 0,
            UpdatedUtc = DateTime.UtcNow
        });
        _serverSecurity.DeleteSessionsForUser(normalized);
        _serverSecurity.LogEvent("NetworkCredentialConfigured", normalized, string.Empty, "Network user ID/password configured by the Main Server; Registration OTP is required only for initial device verification, after 30 days of Windows inactivity, or after an Administrator trust reset.", true);
    }

    public void ConfigureCredentials(string userName, string password, string base32Secret, string verificationCode)
    {
        var normalized = NormalizeUser(userName);
        EnsureApplicationUser(normalized);
        ValidatePassword(password);
        if (!VerifyTotp(base32Secret, verificationCode, DateTime.UtcNow))
            throw new InvalidOperationException("The MFA code does not match the new authenticator secret. Check the device clock and try again.");

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        _serverSecurity.SaveCredential(new NetworkCredentialRecord
        {
            UserName = normalized,
            PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash),
            PasswordIterations = PasswordIterations,
            TotpSecretCipher = _protector.Protect(base32Secret.Trim().ToUpperInvariant()),
            MfaEnabled = true,
            FailedAttempts = 0,
            UpdatedUtc = DateTime.UtcNow
        });
        _serverSecurity.DeleteSessionsForUser(normalized);
        _serverSecurity.LogEvent("NetworkCredentialConfigured", normalized, string.Empty, "Password + TOTP MFA configured/reset by the main server; existing network sessions revoked.", true);
    }

    public bool HasMfaConfigured(string userName)
    {
        var credential = _serverSecurity.GetCredential(NormalizeUser(userName));
        return credential is { MfaEnabled: true } && credential.PasswordHash.Length > 0;
    }

    public AuthenticationResult AuthenticatePassword(string userName, string password)
    {
        var normalized = NormalizeUser(userName);
        var applicationUser = _governance.GetUsers().FirstOrDefault(x => x.WindowsUserName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (applicationUser is null || !applicationUser.IsActive)
            return AuthenticationResult.Fail("Invalid credentials.");

        var credential = _serverSecurity.GetCredential(normalized);
        if (credential is null || credential.PasswordHash.Length == 0)
            return AuthenticationResult.Fail("Network login is not configured for this user. Ask an Administrator to create/reset the LAN password.");
        if (credential.LockoutUntilUtc is { } lockout && lockout > DateTime.UtcNow)
            return AuthenticationResult.Fail($"Account temporarily locked until {lockout.ToLocalTime():g}.");

        bool passwordOk;
        try
        {
            var salt = Convert.FromBase64String(credential.PasswordSalt);
            var expected = Convert.FromBase64String(credential.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, Math.Max(credential.PasswordIterations, 100_000), HashAlgorithmName.SHA256, expected.Length);
            passwordOk = CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { passwordOk = false; }

        if (!passwordOk)
        {
            var failures = credential.FailedAttempts + 1;
            DateTime? until = failures >= 5 ? DateTime.UtcNow.AddMinutes(15) : null;
            _serverSecurity.RecordAuthenticationFailure(normalized, failures, until);
            _serverSecurity.LogEvent("NetworkLogin", normalized, string.Empty, failures >= 5 ? "Password failed; temporary lockout applied." : "Password failed.", false);
            return AuthenticationResult.Fail(failures >= 5 ? "Too many failed attempts. Account is temporarily locked." : "Invalid credentials.");
        }

        _serverSecurity.ClearAuthenticationFailures(normalized);
        return AuthenticationResult.Ok(applicationUser);
    }

    public AuthenticationResult Authenticate(string userName, string password, string totpCode, long clientId = 0, string financialYear = "")
    {
        var normalized = NormalizeUser(userName);
        var applicationUser = _governance.GetUsers().FirstOrDefault(x => x.WindowsUserName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (applicationUser is null || !applicationUser.IsActive)
        {
            _serverSecurity.LogEvent("NetworkLogin", normalized, string.Empty, "Application user not found or disabled.", false);
            return AuthenticationResult.Fail("Invalid credentials.");
        }

        var credential = _serverSecurity.GetCredential(normalized);
        if (credential is null || !credential.MfaEnabled || credential.PasswordHash.Length == 0 || credential.TotpSecretCipher.Length == 0)
        {
            _serverSecurity.LogEvent("NetworkLogin", normalized, string.Empty, "Mandatory MFA is not configured.", false);
            return AuthenticationResult.Fail("MFA is not configured for this user. Ask an Administrator to configure network login.");
        }

        if (credential.LockoutUntilUtc is { } lockout && lockout > DateTime.UtcNow)
            return AuthenticationResult.Fail($"Account temporarily locked until {lockout.ToLocalTime():g}.");

        bool passwordOk;
        try
        {
            var salt = Convert.FromBase64String(credential.PasswordSalt);
            var expected = Convert.FromBase64String(credential.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, Math.Max(credential.PasswordIterations, 100_000), HashAlgorithmName.SHA256, expected.Length);
            passwordOk = CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { passwordOk = false; }

        var totpOk = false;
        if (passwordOk)
        {
            try { totpOk = VerifyTotp(_protector.Unprotect(credential.TotpSecretCipher), totpCode, DateTime.UtcNow); }
            catch { totpOk = false; }
        }

        if (!passwordOk || !totpOk)
        {
            var failures = credential.FailedAttempts + 1;
            DateTime? until = failures >= 5 ? DateTime.UtcNow.AddMinutes(15) : null;
            _serverSecurity.RecordAuthenticationFailure(normalized, failures, until);
            _serverSecurity.LogEvent("NetworkLogin", normalized, string.Empty, failures >= 5 ? "Authentication failed; temporary lockout applied." : "Authentication failed.", false);
            return AuthenticationResult.Fail(failures >= 5 ? "Too many failed attempts. Account is temporarily locked." : "Invalid credentials or MFA code.");
        }

        if (clientId > 0 && !_governance.HasClientAccess(normalized, clientId, financialYear))
        {
            _serverSecurity.LogEvent("NetworkLogin", normalized, string.Empty, $"Client/FY access denied: {clientId}/{financialYear}.", false);
            return AuthenticationResult.Fail("This user is not allowed to access the requested Client/FY.");
        }

        _serverSecurity.ClearAuthenticationFailures(normalized);
        _serverSecurity.LogEvent("NetworkLogin", normalized, string.Empty, "Password + TOTP authentication successful.", true);
        return AuthenticationResult.Ok(applicationUser);
    }

    public bool VerifyTotpForSetup(string base32Secret, string verificationCode) => VerifyTotp(base32Secret, verificationCode, DateTime.UtcNow);

    public static bool VerifyTotp(string base32Secret, string code, DateTime utcNow)
    {
        var normalizedCode = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalizedCode.Length != 6) return false;
        byte[] key;
        try { key = Base32Decode(base32Secret); } catch { return false; }
        var unix = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var counter = unix / 30;
        for (var offset = -1; offset <= 1; offset++)
        {
            var expected = ComputeTotp(key, counter + offset);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(normalizedCode))) return true;
        }
        return false;
    }

    private static string ComputeTotp(byte[] key, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16) | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void EnsureApplicationUser(string userName)
    {
        if (!_governance.GetUsers().Any(x => x.WindowsUserName.Equals(userName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Create this user in Finance Center → Users & Security before configuring network MFA.");
    }

    private static string NormalizeUser(string userName)
    {
        var value = (userName ?? string.Empty).Trim();
        if (value.Length == 0) throw new InvalidOperationException("User name is required.");
        if (value.Length > 80) throw new InvalidOperationException("User name is too long.");
        return value;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new InvalidOperationException("Network passwords must contain at least 12 characters.");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new InvalidOperationException("Network passwords must contain at least one letter and one number.");
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        var text = new string((value ?? string.Empty).ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '=').ToArray());
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var c in text)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) throw new FormatException("Invalid Base32 character.");
            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }

    public sealed record MfaSetup(string Secret, string OtpAuthUri);
    public sealed record AuthenticationResult(bool Success, string Error, ApplicationUserRecord? User)
    {
        public static AuthenticationResult Ok(ApplicationUserRecord user) => new(true, string.Empty, user);
        public static AuthenticationResult Fail(string error) => new(false, error, null);
    }
}
