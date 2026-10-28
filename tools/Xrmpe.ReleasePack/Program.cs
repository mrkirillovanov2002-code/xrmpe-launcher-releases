using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xrmpe.SelfUpdate;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Signing uses the publisher's Windows CNG key.");
if (args.Length is < 4 or > 5) throw new ArgumentException("Arguments: publish-folder output-folder owner/repo notes-file [CNG-key-name]");
var publish = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]);
var repository = ReleaseTrust.Repository(args[2]);
var notes = await File.ReadAllTextAsync(args[3]);
if (!ReleaseTrust.Configured(repository) || notes.Length > 16000) throw new ArgumentException("Set a real repository and <=16000 characters of release notes.");
var release = ReleasePolicy.Read(publish);
await ReleasePolicy.Verify(publish, release);
foreach (var requiredNotice in new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "THIRD-PARTY-COMPONENTS.json" })
    if (!release.Files.Any(f => f.Path == requiredNotice)) throw new InvalidDataException("Missing distribution notices: " + requiredNotice);
if (Directory.Exists(output)) throw new IOException("Output already exists; use a new release folder.");
using var key = CngKey.Open(args.Length == 5 ? args[4] : "xrMPE.Launcher.ReleaseSigning");
using var rsa = new RSACng(key);
using var trusted = ReleaseTrust.PublicKey();
if (!rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(trusted.ExportSubjectPublicKeyInfo())) throw new CryptographicException("Signing key differs from embedded public key.");
Directory.CreateDirectory(output);
var archivePath = Path.Combine(output, ReleaseTrust.ArchiveAsset);
using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    foreach (var file in release.Files)
        zip.CreateEntryFromFile(ReleasePolicy.SafePath(publish, file.Path), file.Path, CompressionLevel.Optimal);
var archive = new ReleaseArchive(ReleaseTrust.ArchiveAsset, new FileInfo(archivePath).Length, await ReleasePolicy.Hash(archivePath));
var manifest = new GitHubManifest(1, repository, "v" + release.Version, release, archive, notes);
var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJson.Default.GitHubManifest);
var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
ReleaseTrust.Verify(bytes, signature);
await File.WriteAllBytesAsync(Path.Combine(output, ReleaseTrust.ManifestAsset), bytes);
await File.WriteAllBytesAsync(Path.Combine(output, ReleaseTrust.SignatureAsset), signature);
await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(output)!, "RELEASE-NOTES-RU.md"), notes);
await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(output)!, "SHA256SUMS.txt"), string.Join(Environment.NewLine,
    new[] { ReleaseTrust.ArchiveAsset, ReleaseTrust.ManifestAsset, ReleaseTrust.SignatureAsset }.Select(name =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, name)))) + "  " + name)));
Console.WriteLine($"Prepared signed release {manifest.Tag} for {repository}: {release.Files.Count} allowlisted files, {archive.Size} compressed bytes. Nothing uploaded.");
