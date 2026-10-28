using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xrmpe.SelfUpdate;

public sealed record ReleaseArchive(string Name, long Size, string Sha256);
public sealed record GitHubManifest(int Schema, string Repository, string Tag, LauncherRelease Release, ReleaseArchive Archive, string Notes);
public sealed record GitHubOffer(GitHubManifest Manifest, byte[] ManifestBytes, byte[] Signature);
public sealed record GitHubChannelPreferences(string Repository, int Source);

public static class ReleaseTrust
{
    public const string ManifestAsset = "launcher-github.json";
    public const string SignatureAsset = "launcher-github.sig";
    public const string ArchiveAsset = "xrMPE-Launcher-win-x64.zip";
    public static RSA PublicKey()
    {
        using var stream = typeof(ReleaseTrust).Assembly.GetManifestResourceStream("Xrmpe.SelfUpdate.ReleasePublicKey.pem")!;
        using var reader = new StreamReader(stream);
        var rsa = RSA.Create(); rsa.ImportFromPem(reader.ReadToEnd()); return rsa;
    }
    public static string Repository(string text)
    {
        text = text.Trim().TrimEnd('/');
        if (text.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) text = text[19..];
        if (!Regex.IsMatch(text, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,38}/[a-zA-Z0-9_.-]{1,100}\z") || text.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || text.Contains(".."))
            throw new ArgumentException("Укажите публичный репозиторий: владелец/репозиторий или https://github.com/владелец/репозиторий.");
        return text.ToLowerInvariant();
    }
    public static bool Configured(string text) => !string.IsNullOrWhiteSpace(text) && !text.Trim().Equals("OWNER/REPOSITORY", StringComparison.OrdinalIgnoreCase);
    public static GitHubManifest Verify(byte[] bytes, byte[] signature, RSA? verificationKey = null)
    {
        if (bytes.Length is < 2 or > 2 * 1024 * 1024 || signature.Length is < 256 or > 1024) throw new InvalidDataException("Неверный размер подписанного манифеста.");
        using var embedded = verificationKey is null ? PublicKey() : null;
        if (!(verificationKey ?? embedded!).VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Подпись обновления недействительна. Установка запрещена.");
        var manifest = JsonSerializer.Deserialize(bytes, UpdateJson.Default.GitHubManifest) ?? throw new InvalidDataException("Пустой подписанный манифест.");
        if (manifest.Schema != 1 || manifest.Release is null || manifest.Archive is null || manifest.Notes is null || manifest.Notes.Length > 16000 || manifest.Repository is null)
            throw new InvalidDataException("Некорректный подписанный манифест.");
        ReleasePolicy.Validate(manifest.Release);
        if (Repository(manifest.Repository) != manifest.Repository || manifest.Tag != "v" + manifest.Release.Version ||
            manifest.Archive.Name != ArchiveAsset || manifest.Archive.Size is < 1 or > 600L * 1024 * 1024 ||
            manifest.Archive.Sha256 is null || manifest.Archive.Sha256.Length != 64 || !manifest.Archive.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Неверные репозиторий, тег или архив обновления.");
        return manifest;
    }
    public static async Task VerifyStage(string stage, CancellationToken ct = default)
    {
        var json = Path.Combine(stage, ManifestAsset); var sig = Path.Combine(stage, SignatureAsset);
        ReleasePolicy.NoLinks(json); ReleasePolicy.NoLinks(sig);
        if (new FileInfo(json).Length > 2 * 1024 * 1024 || new FileInfo(sig).Length > 1024) throw new InvalidDataException("Слишком большой манифест/подпись.");
        var manifest = Verify(await File.ReadAllBytesAsync(json, ct), await File.ReadAllBytesAsync(sig, ct));
        var release = ReleasePolicy.Read(stage);
        if (!JsonSerializer.SerializeToUtf8Bytes(release, UpdateJson.Default.LauncherRelease).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(manifest.Release, UpdateJson.Default.LauncherRelease)))
            throw new InvalidDataException("Манифест файлов отличается от подписанного.");
        await ReleasePolicy.Verify(stage, manifest.Release, ct);
    }
}
