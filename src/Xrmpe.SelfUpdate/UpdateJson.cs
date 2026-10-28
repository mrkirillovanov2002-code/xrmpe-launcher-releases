using System.Text.Json.Serialization;

namespace Xrmpe.SelfUpdate;

public sealed record UndoEntry(string Path, bool Existed);
public sealed record InstallResult(bool Success, string Target, string Message);

[JsonSerializable(typeof(LauncherRelease))]
[JsonSerializable(typeof(InstallJob))]
[JsonSerializable(typeof(List<UndoEntry>))]
[JsonSerializable(typeof(InstallResult))]
[JsonSerializable(typeof(GitHubManifest))]
[JsonSerializable(typeof(GitHubChannelPreferences))]
public partial class UpdateJson : JsonSerializerContext { }
