namespace Xrmpe.Launcher.Presentation;

public sealed record CatalogEditPlan(CatalogPreferences Expected, CatalogPreferences Proposed, string Summary)
{
    public static CatalogEditPlan Create(CatalogPreferences current, CatalogPreferences proposed)
    {
        proposed = CatalogPreferencesStore.Validate(proposed);
        string Address(string value) => value.Length == 0 ? "не настроен" : value;
        var changes = new List<string>();
        if (current.Address != proposed.Address) changes.Add($"Основной API: {Address(current.Address)} → {Address(proposed.Address)}");
        if (current.BackupAddress != proposed.BackupAddress) changes.Add($"Резервный API: {Address(current.BackupAddress)} → {Address(proposed.BackupAddress)}");
        if (current.Publish != proposed.Publish) changes.Add($"Публикация dedicated: {(current.Publish ? "включена" : "выключена")} → {(proposed.Publish ? "включена" : "выключена")}");
        return new(current, proposed, changes.Count == 0 ? "Изменений нет." : string.Join("\n", changes) + "\n\nБудут изменены только эти локальные настройки. Игровые/мод-профили, история и избранное сохраняются. Запросы изменения или удаления в API не выполняются.");
    }
}
