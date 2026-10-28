using System.Text.Json;

namespace Xrmpe.SelfUpdate;

public sealed class Installer : IDisposable
{
    private readonly string _root, _backup;
    private readonly List<UndoEntry> _undo = [];
    private FileStream? _lock;
    public Installer(string root)
    {
        _root = Path.GetFullPath(root); _backup = Path.Combine(_root, ".xrmpe-launcher-rollback");
        ReleasePolicy.EnsureWritable(_root);
        ReleasePolicy.NoLinks(_backup);
        var lockPath = Path.Combine(_root, ".xrmpe-launcher.lock"); ReleasePolicy.NoLinks(lockPath);
        _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private string Target(string name) => name == ReleasePolicy.ManifestName ? Path.Combine(_root, name) : ReleasePolicy.SafePath(_root, name);
    public void Recover()
    {
        var journal = Path.Combine(_backup, "transaction.json");
        ReleasePolicy.NoLinks(journal);
        if (!File.Exists(journal)) return;
        var saved = JsonSerializer.Deserialize(File.ReadAllText(journal), UpdateJson.Default.ListUndoEntry) ?? throw new InvalidDataException("Повреждён журнал отката.");
        foreach (var entry in saved)
        {
            if (entry.Path != ReleasePolicy.ManifestName) ReleasePolicy.ValidateName(entry.Path);
            _undo.Add(entry);
        }
        Rollback();
    }
    public async Task Apply(string stage, LauncherRelease release, Action<int>? afterReplace = null)
    {
        await ReleasePolicy.Verify(stage, release);
        Recover();
        if (Directory.Exists(_backup)) throw new IOException("Осталась папка предыдущего отката. Сохраните её и устраните ошибку перед повтором.");
        Directory.CreateDirectory(Path.Combine(_backup, "files"));
        try
        {
            int count = 0;
            foreach (var name in release.Files.Select(f => f.Path).Append(ReleasePolicy.ManifestName))
            {
                var target = Target(name); ReleasePolicy.NoLinks(target);
                var original = File.Exists(target);
                var backup = Path.Combine(_backup, "files", name); ReleasePolicy.NoLinks(backup);
                if (original)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, false);
                }
                _undo.Add(new UndoEntry(name, original));
                // Intent is durable before replacing a file; recovery is safe after process termination.
                var journal = Path.Combine(_backup, "transaction.json");
                var pendingJournal = journal + ".new";
                File.WriteAllText(pendingJournal, JsonSerializer.Serialize(_undo, UpdateJson.Default.ListUndoEntry));
                using (var flush = new FileStream(pendingJournal, FileMode.Open, FileAccess.Write, FileShare.None)) flush.Flush(true);
                File.Move(pendingJournal, journal, true);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".xrmpe-new"; ReleasePolicy.NoLinks(temporary);
                File.Copy(name == ReleasePolicy.ManifestName ? Path.Combine(stage, name) : ReleasePolicy.SafePath(stage, name), temporary, true);
                File.Move(temporary, target, true);
                afterReplace?.Invoke(++count);
            }
            await ReleasePolicy.Verify(_root, release);
        }
        catch { Rollback(); throw; }
    }
    public void Rollback()
    {
        // Preserve the journal and backup if even one restoration fails.
        foreach (var entry in _undo.AsEnumerable().Reverse())
        {
            var target = Target(entry.Path); ReleasePolicy.NoLinks(target);
            if (entry.Existed)
            {
                var source = Path.Combine(_backup, "files", entry.Path); ReleasePolicy.NoLinks(source);
                var temporary = target + ".xrmpe-restore"; ReleasePolicy.NoLinks(temporary);
                File.Copy(source, temporary, true); File.Move(temporary, target, true);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        Commit();
    }
    public void Commit()
    {
        if (Directory.Exists(_backup))
        {
            ReleasePolicy.NoLinks(_backup);
            var committed = _backup + ".committed-" + Guid.NewGuid().ToString("N");
            Directory.Move(_backup, committed);
            _undo.Clear();
            // Cleanup failure must not undo an already verified and healthy installation.
            try { Directory.Delete(committed, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        else _undo.Clear();
    }
    public void Dispose() { _lock?.Dispose(); _lock = null; }
}
