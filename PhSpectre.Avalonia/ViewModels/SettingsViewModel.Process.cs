using PhSpectre.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Models;
using PhSpectre.Rendering;

namespace PhSpectre.Avalonia.ViewModels;

// Edit: one photo, Look, Frame, Export. Border is drawn by ProcessViewModel; Card and Recipe card by the card
// renderers through ExportMode. Recipe card needs a recipe in EXIF and no Look.
public partial class SettingsViewModel
{
    private ProcessViewModel? _process;
    public ProcessViewModel Process
    {
        get
        {
            if (_process != null) return _process;
            _process = new ProcessViewModel(this);
            _process.LookChanged += () => { OnPropertyChanged(nameof(CardLookKey)); EnforceFrameAvailability(); };
            return _process;
        }
    }

    [ObservableProperty] private int _editTab; // 0 Look, 1 Frame, 2 Export
    [ObservableProperty] private FrameType _frameType = FrameType.None;
    [ObservableProperty] private string _frameNotice = "";

    public bool HasFrameNotice => FrameNotice.Length > 0;
    [RelayCommand] private void DismissFrameNotice() => FrameNotice = "";

    private bool _noticeFromLook;
    public bool CanUndoFrameSwitch => HasFrameNotice && _noticeFromLook;

    // Undoes the automatic switch from Recipe card to Card.
    [RelayCommand]
    private void UndoFrameSwitch()
    {
        FrameNotice = "";
        if (_process?.UndoCommand.CanExecute(null) == true) _process.UndoCommand.Execute(null);
        ApplyFrameType(FrameType.RecipeCard);
    }
    partial void OnFrameNoticeChanged(string value) { OnPropertyChanged(nameof(HasFrameNotice)); OnPropertyChanged(nameof(CanUndoFrameSwitch)); }

    // Collage is "tray open" or a collage export mode; Camera is its own screen.
    private bool IsCollageLike => ShowCollageOptions || ExportMode is ExportMode.Collage or ExportMode.CollageInfoOnly;
    public bool IsEditMode => !IsCameraMode && !IsCollageLike;

    private bool CardTypeSelected => FrameType is FrameType.Card or FrameType.RecipeCard;

    // The Edit canvas + inspector; false while the card layout (Frame → Card / Recipe card) is shown.
    public bool IsProcessMode => IsEditMode && !(CardTypeSelected && EditTab != 0);

    // The card and collage workspace.
    public bool ShowCardBody => !IsCameraMode && !IsProcessMode;

    public bool FrameIsBorder => FrameType == FrameType.Border;
    public bool FrameIsFilm => FrameType == FrameType.Film;
    public bool ShowCardHint => !FrameIsBorder && !FrameIsFilm;
    public bool FrameIsCardType => CardTypeSelected;

    // Only the collage uses the old inspector.
    public bool ShowLegacyInspector => ShowCardBody && !IsEditMode;
    public bool ShowLegacyExport => ShowLegacyInspector && ActiveTab == InspectorTab.Export;

    // Wired by the shell to the card renderer.
    public System.Windows.Input.ICommand? CardSaveCommand { get; set; }
    public System.Windows.Input.ICommand? CardSaveAllCommand { get; set; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _cardSaveAllText = "";
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _cardCanSaveAll;

    // The key tells the live preview when to re-render.
    public PhSpectre.ColorGrading.GradeStack? CardLook => FrameType == FrameType.Card && IsEditMode ? _process?.LookStack() : null;
    // Card and Recipe card are built from the adjusted (cropped, exposed) photo.
    public PhSpectre.Rendering.PhotoAdjust? CardAdjust => IsEditMode && FrameType is FrameType.Card or FrameType.RecipeCard ? _process?.AdjustForCard() : null;
    public string CardLookKey => (CardLook == null ? "" : _process!.LookKey) + (CardAdjust is { } a ? "|" + a : "");
    public bool IsRecipeCardType => FrameType == FrameType.RecipeCard;
    public bool EditIsLook => EditTab == 0;
    public bool EditIsFrame => EditTab == 1;
    public bool EditIsExport => EditTab == 2;

    partial void OnEditTabChanged(int value)
    {
        // Leaving Look closes Crop, so Frame and Export show the cropped photo.
        if (value != 0 && _process is { LookSection: not 0 } p) p.LookSection = 0;
        _process?.RaiseEditSection();
        RaiseLayoutChanged();
        if (IsProcessMode) Process.Refresh();
    }
    partial void OnFrameTypeChanged(FrameType value) { OnPropertyChanged(nameof(CardLookKey)); RaiseLayoutChanged(); }

    private void RaiseLayoutChanged()
    {
        OnPropertyChanged(nameof(TopModeIndex)); OnPropertyChanged(nameof(TopModeSelection));
        OnPropertyChanged(nameof(IsEditMode)); OnPropertyChanged(nameof(IsProcessMode));
        OnPropertyChanged(nameof(ShowCardBody)); OnPropertyChanged(nameof(FrameIsBorder));
            OnPropertyChanged(nameof(FrameIsFilm));
            OnPropertyChanged(nameof(ShowCardHint));OnPropertyChanged(nameof(FrameIsCardType));
            OnPropertyChanged(nameof(ShowLegacyInspector)); OnPropertyChanged(nameof(ShowLegacyExport));
        OnPropertyChanged(nameof(IsRecipeCardType)); OnPropertyChanged(nameof(EditIsLook));
        OnPropertyChanged(nameof(EditIsFrame)); OnPropertyChanged(nameof(EditIsExport));
        OnPropertyChanged(nameof(FrameTypeIndex)); OnPropertyChanged(nameof(CardTabIndex));
            OnPropertyChanged(nameof(CardTopIndex)); OnPropertyChanged(nameof(CardShowsOptions));
            OnPropertyChanged(nameof(CardTabsVisible)); OnPropertyChanged(nameof(ShowRecipeCardReasonInFrame));
        RaiseFrameAvailability();
    }

    public bool RecipeCardAvailable => FrameAvailability.Check(FrameType.RecipeCard, HasRecipe, _process?.HasLook == true).Available;
    public string RecipeCardReason
    {
        get
        {
            var r = FrameAvailability.Check(FrameType.RecipeCard, HasRecipe, _process?.HasLook == true);
            return r.Available ? "" : Loc.F("Recipe card is unavailable: {0}", r.Reason);
        }
    }
    public bool ShowRecipeCardReason => !RecipeCardAvailable;

    private void RaiseFrameAvailability()
    {
        OnPropertyChanged(nameof(RecipeCardAvailable));
        OnPropertyChanged(nameof(RecipeCardReason));
        OnPropertyChanged(nameof(ShowRecipeCardReason)); OnPropertyChanged(nameof(ShowRecipeCardReasonInFrame));
    }

    // Recipe card may stop being allowed.
    private void EnforceFrameAvailability()
    {
        RaiseFrameAvailability();
        if (FrameType != FrameType.RecipeCard) return;
        var r = FrameAvailability.Check(FrameType.RecipeCard, HasRecipe, _process?.HasLook == true);
        if (r.Available) return;
        _noticeFromLook = _process?.HasLook == true;
        FrameNotice = _process?.HasLook == true
            ? Loc.T("Recipe card needs the original look, so the frame was switched to Card.")
            : Loc.T("This photo has no film recipe, so the frame was switched to Card.");
        ApplyFrameType(FrameType.Card);
    }

    public void ApplyFrameType(FrameType type)
    {
        var r = FrameAvailability.Check(type, HasRecipe, _process?.HasLook == true);
        if (!r.Available) { RaiseLayoutChanged(); return; }
        if (type != FrameType.RecipeCard && type != FrameType.Card) { /* keep notice until user acts */ }
        else if (FrameNotice.Length > 0 && type == FrameType.RecipeCard) FrameNotice = "";
        Process.SetBorder(type == FrameType.Border);
        Process.SetFilm(type == FrameType.Film);
        FrameType = type;
        if (type == FrameType.Card) ApplyTopMode(0);
        else if (type == FrameType.RecipeCard) ApplyTopMode(1);
        if (type is FrameType.Card or FrameType.RecipeCard) { if (EditTab == 0) EditTab = 1; }
        RaiseLayoutChanged();
        if (IsProcessMode) Process.Refresh();
    }

    // A disabled Recipe card cannot be picked.
    public int FrameTypeIndex
    {
        get => (int)FrameType;
        set
        {
            if (value < 0) { OnPropertyChanged(); return; }
            if (value != (int)FrameType) { FrameNotice = ""; ApplyFrameType((FrameType)value); }
            OnPropertyChanged();
        }
    }

    // Header of the card layout: Look · Frame (Look returns to the Edit canvas).
    // Style / Content pickers are hidden while the card export tab is open.
    public bool CardShowsOptions => !(IsEditMode && ActiveTab == InspectorTab.Export);
    public bool CardTabsVisible => !IsRecipeEditorOpen && CardShowsOptions;
    public bool ShowRecipeCardReasonInFrame => ShowRecipeCardReason && CardShowsOptions;

    public int CardTabIndex
    {
        get => EditTab == 0 ? 0 : 1;
        set { if (value >= 0) EditTab = value == 0 ? 0 : 1; OnPropertyChanged(); }
    }

    public int CardTopIndex
    {
        get => EditTab == 0 ? 0 : ActiveTab == InspectorTab.Export ? 2 : 1;
        set
        {
            if (value < 0) return;
            if (value == 0) EditTab = 0;
            else { EditTab = 1; ActiveTab = value == 2 ? InspectorTab.Export : (ActiveTab == InspectorTab.Export ? InspectorTab.Style : ActiveTab); }
            OnPropertyChanged();
        }
    }

    // Called by the shells whenever the current photo changes.
    public void NotifyPhotoChanged()
    {
        if (IsProcessMode) Process.Refresh();
        EnforceFrameAvailability();
    }
}
