using Avalonia.Interactivity;
using Xrmpe.Launcher.Content;

namespace Xrmpe.Launcher;

public partial class MainWindow
{
    private ModWorkspaceStore _modWorkspaces = null!;
    private string? _modGame, _modAddon;
    private ModWorkspace _visibleModWorkspace = ModWorkspace.Empty;
    private void LoadModWorkspace()
    {
        HomeAddonName.Text = SelectedAddon is { } addon ? $"{addon.Name} · {addon.Version}" : "Аддон не выбран или отсутствует";
        if (_contentRoot is null || (_modGame == _contentRoot && _modAddon == _activeAddonName)) return;
        SaveModNotesIfChanged();
        _modGame = _contentRoot; _modAddon = _activeAddonName;
        _visibleModWorkspace = _modWorkspaces.Load(_modGame, _modAddon);
        ModNotes.Text = _visibleModWorkspace.Notes;
        ModAddonName.Text = SelectedAddon?.Name ?? _activeAddonName;
        ModWorkspaceStatus.Text = _modWorkspaces.Warning ?? "Профиль принадлежит только этому аддону и этой установке игры. Модификации не применяются.";
    }
    private void SaveModNotesIfChanged()
    {
        if (_modGame is null || _modAddon is null || ModNotes.Text == _visibleModWorkspace.Notes) return;
        var next = _visibleModWorkspace with { Notes = ModNotes.Text ?? "" };
        _modWorkspaces.Save(_modGame, _modAddon, next); _visibleModWorkspace = next;
    }
    private void SaveModWorkspace(object? sender, RoutedEventArgs e)
    {
        try { SaveModNotesIfChanged(); ModWorkspaceStatus.Text = "Заметки профиля сохранены. Файлы игры и модификаций не изменялись."; }
        catch (Exception ex) { ModWorkspaceStatus.Text = "Не удалось сохранить профиль: " + ex.Message; }
    }
}
