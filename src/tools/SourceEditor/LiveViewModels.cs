using PKHeX.Core;

namespace PKHeX.Presentation.ViewModels;

public partial class TrainerEditorViewModel
{
    private Dictionary<string,object?>? liveFields;
    private bool[] liveBadges = [];

    private Dictionary<string,object?> CaptureLiveFields() => GetType().GetProperties()
        .Where(property => property.CanRead && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0
            && (property.PropertyType.IsPrimitive || property.PropertyType.IsEnum || property.PropertyType == typeof(string)
                || property.PropertyType == typeof(decimal) || property.PropertyType == typeof(DateTime)))
        .ToDictionary(property => property.Name,property => property.GetValue(this));

    private void RememberLiveDraft()
    {
        if (_sav is not SAV3E) return;
        liveFields = CaptureLiveFields();
        liveBadges = Badges.Select(badge => badge.IsObtained).ToArray();
    }

    public bool HasLiveDraft => _sav is SAV3E && liveFields is not null
        && (CaptureLiveFields().Any(item => !Equals(liveFields.GetValueOrDefault(item.Key),item.Value))
            || !Badges.Select(badge => badge.IsObtained).SequenceEqual(liveBadges));

    public object CaptureLiveDraft() => new { Fields = CaptureLiveFields(), Badges = Badges.Select(badge => badge.IsObtained).ToArray() };
}

public partial class PokemonEditorViewModel
{
    private byte[]? liveOriginal;
    public void RememberLiveDraft() => liveOriginal = _sav is SAV3E ? PreparePKM().Data.ToArray() : null;
    public bool HasLiveDraft
    {
        get
        {
            if (liveOriginal is null) return false;
            try { return !liveOriginal.AsSpan().SequenceEqual(PreparePKM().Data); }
            catch (ArgumentException) { return true; }
        }
    }
}

public partial class EventFlagsEditorViewModel
{
    public bool HasLiveDraft => _sav is SAV3E && _flagArray is not null
        && Flags.Any(flag => _flagArray.GetEventFlag(flag.Index) != flag.IsSet);

    public void RefreshLiveFlags()
    {
        if (_sav is not SAV3E || _flagArray is null) return;
        for (int index = 0; index < Flags.Count && index < FlagCount; index++)
        {
            bool current = _flagArray.GetEventFlag(index);
            if (Flags[index].IsSet != current) Flags[index].IsSet = current;
        }
        if (SelectedFlagIndex >= 0 && SelectedFlagIndex < Flags.Count)
            SelectedFlagValue = Flags[SelectedFlagIndex].IsSet;
    }

    public int[] CaptureLiveDraft() => _flagArray is null ? []
        : Flags.Where(flag => _flagArray.GetEventFlag(flag.Index) != flag.IsSet)
            .Select(flag => flag.IsSet ? flag.Index + 1 : -(flag.Index + 1)).ToArray();
}

public partial class MainWindowViewModel
{
    public bool HasLiveDraft => TrainerEditor?.HasLiveDraft == true || CurrentPokemonEditor?.HasLiveDraft == true
        || InventoryEditor?.Pouches.Any(pouch => pouch.HasChanges) == true
        || (EventFlagsEditor as EventFlagsEditorViewModel)?.HasLiveDraft == true;

    public object CaptureLiveDraft() => new
    {
        Trainer = TrainerEditor?.CaptureLiveDraft(),
        Pokemon = CurrentPokemonEditor is { } pokemon ? new { pokemon.Nickname, Data = pokemon.TargetPKM.Data.ToArray() } : null,
        Pouches = InventoryEditor?.Pouches.Select(pouch => pouch.Items.Select(item => item.CreateRecord()).ToArray()).ToArray(),
        Events = (EventFlagsEditor as EventFlagsEditorViewModel)?.CaptureLiveDraft()
    };

    // SAV3 caches its active bank in a readonly field. Recreate the model when
    // the game switches banks, so later serialization writes the latest bank.
    // The host defers refresh while an input or auxiliary editor is active.
    public void RefreshLiveImage(byte[] image, bool edited)
    {
        if (CurrentSave is not SAV3E save) return;
        var target = CurrentPokemonEditor?.TargetPKM;
        int pouch = InventoryEditor?.SelectedPouch is { } selected ? InventoryEditor.Pouches.IndexOf(selected) : 0;
        int? currentBox = BoxViewer?.CurrentBox;
        int selectedBoxSlot = BoxViewer?.SelectedIndex ?? 0;
        int selectedFlag = (EventFlagsEditor as EventFlagsEditorViewModel)?.SelectedFlagIndex ?? 0;
        string flagSearch = (EventFlagsEditor as EventFlagsEditorViewModel)?.SearchText ?? string.Empty;
        var updated = new SAV3E(image);
        int imageCurrentBox = updated.CurrentBox;
        bool bankSwitched = LiveBank(save.Data) != LiveBank(image);
        if (bankSwitched)
        {
            int workspace = SelectedWorkspaceIndex;
            var activeWorkspace = ActiveWorkspace;
            updated.State.Edited = edited;
            _saveFileService.OpenLoadedSave(updated,_saveFileService.CurrentPath);
            save = updated;
            SelectedWorkspaceIndex = workspace;
            ActiveWorkspace = activeWorkspace;
            if (EventFlagsEditor is EventFlagsEditorViewModel eventEditor)
            {
                eventEditor.SearchText = flagSearch;
                if (selectedFlag >= 0 && selectedFlag < eventEditor.FlagCount)
                    eventEditor.SelectedFlagIndex = selectedFlag;
            }
        }
        else
        {
            save.CopyChangesFrom(updated);
            TrainerEditor?.Dispose();
            TrainerEditor = new TrainerEditorViewModel(save,_dialogService,_imageCodec);
            InventoryEditor = new InventoryEditorViewModel(save,_spriteRenderer,IsHaXMode);
            (EventFlagsEditor as EventFlagsEditorViewModel)?.RefreshLiveFlags();
        }
        if (InventoryEditor is { } inventory && pouch >= 0 && pouch < inventory.Pouches.Count)
            inventory.SelectedPouch = inventory.Pouches[pouch];
        if (BoxViewer is { } boxViewer)
        {
            if (currentBox is int box && box >= 0 && box < boxViewer.BoxCount)
                ((IBoxNavigator)boxViewer).NavigateTo(box,selectedBoxSlot);
            else
                boxViewer.RefreshCurrentBox();
        }
        save.CurrentBox = imageCurrentBox;
        save.State.Edited = edited;
        OnPropertyChanged(nameof(CurrentTrainerName));
        PartyViewer?.RefreshParty();
        BatchEditor?.RefreshExternalState();
        if (target is null || target.Species == 0) return;
        var allPokemon = Enumerable.Range(0,save.PartyCount).Select(save.GetPartySlotAtIndex)
            .Concat(Enumerable.Range(0,save.BoxCount * save.BoxSlotCount).Select(index => save.GetBoxSlotAtIndex(index / save.BoxSlotCount,index % save.BoxSlotCount)));
        var matches = allPokemon.Where(pokemon => pokemon.Species != 0 && pokemon.PID == target.PID && pokemon.ID32 == target.ID32).Take(2).ToArray();
        if (matches.Length == 1) CurrentPokemonEditor?.LoadPKM(matches[0]);
        else if (bankSwitched) CurrentPokemonEditor?.LoadPKM(target);
    }

    private static int LiveBank(ReadOnlySpan<byte> image)
    {
        bool first = SAV3.IsAllMainSectorsPresent(image,0,out int firstFooter);
        bool second = SAV3.IsAllMainSectorsPresent(image,1,out int secondFooter);
        if (!first) return second ? 1 : 0;
        return second ? SAV3BlockDetection.CompareFooters(image,firstFooter,secondFooter) : 0;
    }
}
