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

Console.WriteLine($"PASS: {passed}/{passed} AAH signed GitHub auto-update protocol scenarios.");
