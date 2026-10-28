using Avalonia.Controls;
using Avalonia.Interactivity;
using Xrmpe.Launcher.Content;
namespace Xrmpe.Launcher;
public partial class MainWindow {
    private sealed record DifficultyChoice(string Code,string Title) {public override string ToString()=>Title;}
    private string[] ReadRotation()=> (DefenceRotation.Text??"").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
    private void ApplyScenarioControls(ServerProfile p) {
        var difficulties=new[]{new DifficultyChoice("none","По умолчанию"),new("gd_novice","Новичок"),new("gd_stalker","Сталкер"),new("gd_veteran","Ветеран"),new("gd_master","Мастер")};
        CampaignDifficulty.ItemsSource=difficulties; CampaignDifficulty.SelectedItem=difficulties.First(x=>x.Code==p.Difficulty); ScenarioMinPlayers.Value=p.MinPlayers;
        DefenceRotation.Text=string.Join("\n",p.Rotation??new[]{p.Map});
        SetCampaignSaves(p.CoopSave,p.OfficialSave);
    }
    private void SetCampaignSaves(string name,bool official) {
        var rows=_contentRoot is null?new[]{new CampaignSave("",false)}:AddonScenarios.Saves(_contentRoot,_activeAddonName);
        var selected=rows.FirstOrDefault(x=>x.Name==name && x.Official==official)??new CampaignSave(name,official);
        if(!rows.Contains(selected))rows=rows.Append(selected).ToArray();
        CampaignSaveChoice.ItemsSource=rows;CampaignSaveChoice.SelectedItem=selected;
    }
    private void RefreshCampaignSaves(object? sender,RoutedEventArgs e) {
        if(_running || _serverRunning)return;
        var old=CampaignSaveChoice.SelectedItem as CampaignSave??new("",false);_applyingContent=true;
        try{SetCampaignSaves(old.Name,old.Official);}finally{_applyingContent=false;}UpdateContentAvailability();
    }
    private void ScenarioSelectionChanged(object? sender,SelectionChangedEventArgs e){if(!_applyingContent && _profileStore is not null)UpdateContentAvailability();}
    private void ScenarioTextChanged(object? sender,TextChangedEventArgs e){if(!_applyingContent && _profileStore is not null)UpdateContentAvailability();}
    private void ScenarioNumberChanged(object? sender,NumericUpDownValueChangedEventArgs e){if(!_applyingContent && _profileStore is not null)UpdateContentAvailability();}
}
