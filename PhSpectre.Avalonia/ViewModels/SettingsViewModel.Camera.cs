using System;
using System.Collections.Generic;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Avalonia.Models;
using PhSpectre.Avalonia.Services;
using PhSpectre.Models;

namespace PhSpectre.Avalonia.ViewModels;

// The Camera mode (recipe picker + slots + write) and its switch, wired into the top bar and Content → Recipe.
public partial class SettingsViewModel
{
    private const int MaxSlotNameLength = 25;
    private RecipeLibraryViewModel? _library;
    private CameraDialogViewModel? _camera;
    private DispatcherTimer? _statusTimer;

    public RecipeLibraryViewModel Library => _library ??= new RecipeLibraryViewModel(this);
    public CameraDialogViewModel Camera => _camera ??= new CameraDialogViewModel(this, SetRecipeStatus);

    // Persisted: the feature stays off until the user turns it on (CAMERA.md: Experimental).
    [ObservableProperty] private bool _cameraExperimental;
    // How many restore points to keep: 5, 20 or 0 for all.
    [ObservableProperty] private int _restoreKeepLast = 20;

    [ObservableProperty] private string _recipeStatusText = "";

    public bool CameraSupported => CameraService.IsSupported;
    public bool ShowCameraMode => CameraSupported && CameraExperimental;
    public bool HasRecipeStatus => RecipeStatusText.Length > 0;

    /// <summary>The shared photo state, supplied by the shell (desktop: Files/SelectedFile, mobile: RecentPhotos).</summary>
    public Func<IReadOnlyList<FileEntry>>? PhotosProvider { get; set; }
    public Func<FileEntry?>? CurrentPhotoProvider { get; set; }
    public Action<FileEntry>? SelectPhotoAction { get; set; }
    public System.Windows.Input.ICommand? OpenPhotosCommand { get; set; }

    public int RestoreKeepIndex
    {
        get => RestoreKeepLast switch { 5 => 0, 20 => 1, _ => 2 };
        set => RestoreKeepLast = value switch { 0 => 5, 1 => 20, _ => 0 };
    }

    partial void OnCameraExperimentalChanged(bool value)
    {
        if (CameraSupported) Camera.SetEnabled(value);
        OnPropertyChanged(nameof(ShowCameraMode));
        if (!value) IsCameraMode = false;
        else if (Camera.SetupOpen)
        {
            Camera.CloseSetup();
            IsCameraMode = true;
        }
    }

    partial void OnRestoreKeepLastChanged(int value) => OnPropertyChanged(nameof(RestoreKeepIndex));

    partial void OnRecipeStatusTextChanged(string value) => OnPropertyChanged(nameof(HasRecipeStatus));

    /// <summary>A recipe name that fits a camera slot (25 characters), taken from the title when the recipe has none.</summary>
    public string SlotNameFor(FilmRecipe recipe)
    {
        var name = string.IsNullOrWhiteSpace(recipe.RecipeName) ? RecipeTitleText : recipe.RecipeName!;
        return name.Length > MaxSlotNameLength ? name[..MaxSlotNameLength] : name;
    }

    public void SetRecipeStatus(string text)
    {
        RecipeStatusText = text;
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            RecipeStatusText = "";
        };
        _statusTimer.Start();
    }


    /// <summary>Content → Recipe: keeps the photo's recipe in Saved (it shows up in the Camera mode).</summary>
    [RelayCommand]
    private void SaveRecipeToLibrary()
    {
        if (DetectedRecipe is not { } recipe) return;
        var result = RecipeLibraryService.User.Add(recipe, null, PhSpectre.Recipes.RecipeOrigin.FromImage);
        SetRecipeStatus(result.Outcome == PhSpectre.Recipes.AddOutcome.Added
            ? $"Saved “{result.Entry.Name}” to Saved recipes"
            : $"Already saved as “{result.Entry.Name}”");
        _library?.Refresh();
    }
    /// <summary>"Send to camera" in Content → Recipe, and the top-bar indicator: the Camera mode, or its setup dialog while off.</summary>
    [RelayCommand]
    private void OpenCamera()
    {
        if (!CameraSupported) return;
        if (!CameraExperimental) { Camera.OpenSetup(); return; }
        Library.UsePhoto();
        IsCameraMode = true;
    }
}
