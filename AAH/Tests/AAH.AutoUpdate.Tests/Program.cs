using AccountingAutomationHub.Desktop.Services;
using System.Text.Json;

static void Require(bool ok, string message)
{
    if (!ok) throw new Exception("AUTOUPDATE SECURITY FAILED: " + message);
}
int passed = 0;
void Pass(string description)
{
    passed++;
    Console.WriteLine("PASS: " + description);
}
var service = typeof(GitHubAutoUpdateService);
Require(service.GetMethod("Start", System.Reflection.BindingFlags.Static |
    System.Reflection.BindingFlags.NonPublic) is not null, "background updater absent");
Require(GitHubAutoUpdateService.ReleaseApi.StartsWith(
    "https://api.github.com/repos/surajTech-22/AccountingAutomationHub-Windows/releases/latest",
    StringComparison.Ordinal), "release feed not pinned");
Pass("pinned main AAH GitHub release feed");

// Display time uses the PC's timezone but never loses the actual UTC instant.
// In India, 06:19:33Z must display as 11:49:33+05:30; CI may use UTC.
var utcReleaseInstant = new DateTimeOffset(2026, 10, 31, 6, 19, 33, TimeSpan.Zero);
var localLogText = GitHubAutoUpdateService.FormatLocalLogTimestamp(utcReleaseInstant);
Require(DateTimeOffset.TryParse(localLogText,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsedLocalLog) &&
        parsedLocalLog.ToUniversalTime() == utcReleaseInstant &&
        parsedLocalLog.Offset == TimeZoneInfo.Local.GetUtcOffset(utcReleaseInstant.UtcDateTime),
    "Desktop auto-update log timestamp is not local offset-aware or changed the release instant.");
Pass("desktop updater uses offset-aware local timestamps without shifting UTC instants");

// Catch-up is event replay, never an untrusted alternate update feed.
// Assert that a dead SSE connection cannot block forever, and that replay
// never accepts arbitrary relay payloads as update authority.
Require(GitHubAutoUpdateService.ReleaseSignalReplayUrl.StartsWith(
    GitHubAutoUpdateService.ReleaseSignalUrl + "?since=latest",
    StringComparison.Ordinal), "missed release-event replay not configured");
Require(GitHubAutoUpdateService.ReleaseStreamIdleTimeout > TimeSpan.FromMinutes(1) &&
        GitHubAutoUpdateService.ReleaseStreamIdleTimeout <= TimeSpan.FromMinutes(5),
    "stale SSE stream must be reconnectable without hourly polling");
Require(GitHubAutoUpdateService.TryGetReleaseSignalVersion(
        "data: {\"event\":\"message\",\"message\":\"v4.0.0.516\"}", out var accepted) &&
        accepted == "v4.0.0.516",
    "valid cached stable release event was discarded");
Require(GitHubAutoUpdateService.TryGetReleaseSignalVersion(
        "data:{\"event\":\"message\",\"message\":\"v4.0.0.517\"}", out accepted) &&
        accepted == "v4.0.0.517",
    "unspaced valid SSE data prefix was discarded");
Require(!GitHubAutoUpdateService.TryGetReleaseSignalVersion(
        "data: {\"event\":\"keepalive\",\"message\":\"v4.0.0.999\"}", out _),
    "keepalive accepted as update event");
Require(!GitHubAutoUpdateService.TryGetReleaseSignalVersion(
        "data: {\"event\":\"message\",\"message\":\"v4.0.0.517-rc1\"}", out _),
    "prerelease accepted from relay");
Require(!GitHubAutoUpdateService.TryGetReleaseSignalVersion(
        "data: {\"event\":\"message\",\"message\":\"https://untrusted.invalid/update\"}", out _),
    "arbitrary relay URL accepted");
Require(!GitHubAutoUpdateService.TryGetReleaseSignalVersion("data: invalid-json", out _),
    "broken event JSON accepted");
Pass("missed SSE release replay, idle watchdog and strict untrusted-event filtering");

var baseVersion = new Version(4,0,0,502);
Require(GitHubAutoUpdateService.IsNewer("v4.0.0.503", baseVersion), "stable revision not accepted");
Require(!GitHubAutoUpdateService.IsNewer("v4.0.0.502", baseVersion), "same release accepted");
Require(!GitHubAutoUpdateService.IsNewer("v4.0.0.501", baseVersion), "downgrade accepted");
Require(!GitHubAutoUpdateService.IsNewer("v4.0.0.503-rc1", baseVersion), "prerelease accepted");
Require(!GitHubAutoUpdateService.IsNewer("v4.0.0.503+evil", baseVersion), "build notation accepted");
Require(!GitHubAutoUpdateService.IsNewer("../updater", baseVersion), "invalid tag accepted");
Pass("version upgrades only, never downgrade/prerelease");

var hash = new string('a',64);
Require(GitHubAutoUpdateService.IsValidSha256(hash), "valid checksum rejected");
Require(!GitHubAutoUpdateService.IsValidSha256(hash[..63]), "truncated checksum accepted");
Require(!GitHubAutoUpdateService.IsValidSha256(hash + "f"), "overlong checksum accepted");
Require(GitHubAutoUpdateService.ParseChecksum(hash + "  AAH_Update.exe") == hash,
    "sha256sum formatting failed");
Require(GitHubAutoUpdateService.ParseChecksum(hash + " *AAH_Update.exe") == hash,
    "binary checksum formatting failed");
Require(GitHubAutoUpdateService.ParseChecksum(hash + "  wrong.exe") is null,
    "unrelated asset digest accepted");
Require(GitHubAutoUpdateService.ParseChecksum(hash + "\n" + hash) is null,
    "multiple checksum entries accepted");
Pass("strict updater-only SHA256 digest");

var tag = "v4.0.0.503";
var root = $"https://github.com/surajTech-22/AccountingAutomationHub-Windows/releases/download/{tag}/";
Require(GitHubAutoUpdateService.IsTrustedAssetUrl(root + "AAH_Update.exe", tag, "AAH_Update.exe"), 
    "approved download URL rejected");
Require(!GitHubAutoUpdateService.IsTrustedAssetUrl("http://github.com/surajTech-22/AccountingAutomationHub-Windows/releases/download/" + tag + "/AAH_Update.exe", tag,"AAH_Update.exe"),"HTTP downgrade");
Require(!GitHubAutoUpdateService.IsTrustedAssetUrl("https://evil.example/" + tag + "/AAH_Update.exe", tag,"AAH_Update.exe"),"untrusted host");
Require(!GitHubAutoUpdateService.IsTrustedAssetUrl("https://github.com/another/repo/releases/download/" + tag + "/AAH_Update.exe",tag,"AAH_Update.exe"),"wrong repository");
Require(!GitHubAutoUpdateService.IsTrustedAssetUrl(root + "AAH_Update.exe?name=wrong",tag,"AAH_Update.exe"),"query spoof");
Pass("pinned HTTPS release assets");

string Protocol(string protocol, string release, string sha, bool allowed) =>
    JsonSerializer.Serialize(new {
        protocol,
        release,
        repository = "surajTech-22/AccountingAutomationHub-Windows",
        verification = "github-release-sha256-v1",
        asset = "AAH_Update.exe",
        sha256 = sha,
        automatic_install_allowed = allowed
    });
Require(GitHubAutoUpdateService.IsProtocolAccepted(
    Protocol(GitHubAutoUpdateService.Protocol, tag, hash, true),tag,hash),
    "valid protocol rejected");
Require(!GitHubAutoUpdateService.IsProtocolAccepted(
    Protocol("UNKNOWN", tag, hash, true),tag,hash),"unknown protocol accepted");
Require(!GitHubAutoUpdateService.IsProtocolAccepted(
    Protocol(GitHubAutoUpdateService.Protocol,"v99.99.99",hash,true),tag,hash),"wrong version accepted");
Require(!GitHubAutoUpdateService.IsProtocolAccepted(
    Protocol(GitHubAutoUpdateService.Protocol,tag,new string('b',64),true),tag,hash),"changed hash accepted");
Require(!GitHubAutoUpdateService.IsProtocolAccepted(
    Protocol(GitHubAutoUpdateService.Protocol,tag,hash,false),tag,hash),"rollout disabled accepted");
Require(!GitHubAutoUpdateService.IsProtocolAccepted("invalid-json",tag,hash),"malformed protocol accepted");
Require(!GitHubAutoUpdateService.PublisherIdentityCompatible(null, "AAA"), "unsigned installed app accepted signed unknown updater");
Require(!GitHubAutoUpdateService.PublisherIdentityCompatible("AAA", null), "signed installed app accepted unsigned updater");
Require(!GitHubAutoUpdateService.PublisherIdentityCompatible("AAA", "BBB"), "different signing certificate accepted");
Require(GitHubAutoUpdateService.PublisherIdentityCompatible("AAA", "aaa"), "matching publisher rejected");
Require(GitHubAutoUpdateService.PublisherIdentityCompatible(null, null), "DeskSpace-style unsigned checksum-only channel not available");
Pass("signed publisher consistency and two-unsigned GitHub SHA256 compatibility");

Pass("release protocol opt-in, identity, and hash matching");

GitHubAutoUpdateService.Start(isLanClient:true);
Pass("LAN clients excluded from the GitHub update path");

if (OperatingSystem.IsWindows())
{
    var unsignedFile = Path.Combine(Path.GetTempPath(), "AAH-unsigned-updater-" +
        Guid.NewGuid().ToString("N") + ".exe");
    try
    {
        File.WriteAllBytes(unsignedFile, new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x01 });
        var identity = await GitHubAutoUpdateService.GetVerifiedPublisherAsync(unsignedFile);
        Require(identity is null, "unsigned executable accepted as an updater");
    }
    finally { try { File.Delete(unsignedFile); } catch { } }
}
Pass("unsigned executable never gains trusted release authority");

// Simulate two running AAH processes with the same release notification.
// Only one may write the staging cache. Each verified updater executable gets
// its own immutable path so a running host can never lock a new download.
var stageRoot = Path.Combine(Path.GetTempPath(),
    "AAH-GitHub-Stage-Test-" + Guid.NewGuid().ToString("N"));
try
{
    using (var lock1 = GitHubAutoUpdateService.TryAcquireStagingLock(stageRoot))
    {
        Require(lock1 is not null, "first update staging lock was refused");
        using var lock2 = GitHubAutoUpdateService.TryAcquireStagingLock(stageRoot);
        Require(lock2 is null, "second process could overwrite a concurrently staged updater");
    }
    using (var lock3 = GitHubAutoUpdateService.TryAcquireStagingLock(stageRoot))
        Require(lock3 is not null, "staging lock was not released after host stopped");
    var firstAttempt = GitHubAutoUpdateService.GetUpdaterAttemptDirectory(stageRoot, "v4.0.0.535");
    var secondAttempt = GitHubAutoUpdateService.GetUpdaterAttemptDirectory(stageRoot, "v4.0.0.535");
    Require(firstAttempt != secondAttempt &&
        Path.GetDirectoryName(firstAttempt) == GitHubAutoUpdateService.GetReleaseCacheDirectory(stageRoot, "v4.0.0.535") &&
        Path.GetDirectoryName(secondAttempt) == Path.GetDirectoryName(firstAttempt),
        "repeat updater attempts reused an executable path and could hit a Windows file lock");
}
finally { try { Directory.Delete(stageRoot, recursive: true); } catch { } }
Pass("cross-process staging lock and immutable per-attempt executable paths");

// Cache cleanup must remove past updater copies, not workspace/data folders.
var cache = Path.Combine(Path.GetTempPath(), "AAH-GitHub-Cache-Test-" + Guid.NewGuid().ToString("N"));
try
{
    var past = GitHubAutoUpdateService.GetReleaseCacheDirectory(cache, "v4.0.0.503");
    var latest = GitHubAutoUpdateService.GetReleaseCacheDirectory(cache, "v4.0.0.504");
    var legacy = Path.Combine(cache, "0123456789abcdef0123456789abcdef");
    var privateData = Path.Combine(cache, "company-ledger-data");
    foreach (var dir in new[] { past, latest, legacy, privateData })
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "fixture");
    }
    var removed = GitHubAutoUpdateService.CleanupStaleUpdaterFolders(cache, "v4.0.0.504");
    Require(removed == 2 && !Directory.Exists(past) && !Directory.Exists(legacy) &&
            Directory.Exists(latest) && Directory.Exists(privateData),
        "GitHub updater cache did not safely replace older updater folders.");
}
finally { try { Directory.Delete(cache, recursive: true); } catch { } }
Pass("remove previous updater caches but leave unrelated accounting directories");

Require(GitHubAutoUpdateService.IsForeignAahRegistration(
        "Accounting Automation Hub", "{LEGACY-AAH}_is1"),
    "Separate legacy AAH registration not detected.");
Require(!GitHubAutoUpdateService.IsForeignAahRegistration(
        "Accounting Automation Hub", "{D8E4758C-09F1-45BF-831A-59C22B79DC04}_is1"),
    "Current GitHub setup was mistaken for a duplicate legacy application.");
Require(!GitHubAutoUpdateService.IsForeignAahRegistration(
        "An Unrelated Application", "{SOME-OTHER-ID}"),
    "Unrelated applications were falsely identified as AAH.");
Pass("block update when Windows registers legacy and new AAH copies separately");

Console.WriteLine($"PASS: {passed}/{passed} AAH GitHub auto-update protocol scenarios.");
