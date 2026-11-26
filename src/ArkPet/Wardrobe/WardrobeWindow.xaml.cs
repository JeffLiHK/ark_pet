using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ArtPet.Windows.Desktop;
using ArtPet.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace ArtPet.Windows.Wardrobe;

internal partial class WardrobeWindow : Window
{
    private readonly PetSession? _session;
    private readonly Func<bool> _hardwareAccelerationEnabled;
    private readonly IReadOnlyList<CharacterPack> _installedPacks;
    private readonly ObservableCollection<CharacterChoice> _characters = [];
    private readonly CancellationTokenSource _loading = new();
    private OutfitChoice[] _outfits = [];
    private Button? _lastCharacterButton;
    private OutfitChoice? _modelSelection;
    private bool _managingModel;
    private bool _charactersLoaded;

    internal WardrobeWindow(PetSession? session, IReadOnlyList<CharacterPack> installedPacks,
        Func<bool> hardwareAccelerationEnabled)
    {
        _session = session;
        _hardwareAccelerationEnabled = hardwareAccelerationEnabled;
        _installedPacks = installedPacks;
        InitializeComponent();
        ProfessionFilter.ItemsSource = WardrobeProfessions.Options;
        ProfessionFilter.SelectedIndex = 0;
        Characters.ItemsSource = _characters;
        CollectionViewSource.GetDefaultView(_characters).Filter = item =>
            item is CharacterChoice character &&
            (ProfessionFilter.SelectedValue is not string { Length: > 0 } profession || character.Profession == profession) &&
            character.DisplayName.Contains(CharacterSearch.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        if (_session is not null)
        {
            _session.Changed += RefreshChoices;
        }
        else
        {
            Title = "ArkPet 模型影格";
        }
        Loaded += LoadCharacters;
    }

    internal void ShowNear(PetWindow pet)
    {
        IntPtr petHandle = new WindowInteropHelper(pet).EnsureHandle();
        Screen screen = Screen.FromHandle(petHandle);
        var work = NativeMethods.GetWorkingArea(screen.Bounds.Location) ?? screen.WorkingArea;
        double dpiScale = NativeMethods.GetDpiForWindow(petHandle) / 96d;
        MaxWidth = Math.Max(MinWidth, work.Width / dpiScale);
        MaxHeight = Math.Max(MinHeight, work.Height / dpiScale);

        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        uint initialDpi = NativeMethods.GetDpiForWindow(handle);
        PlaceNear(pet, petHandle, handle, work);
        if (NativeMethods.GetDpiForWindow(handle) != initialDpi)
        {
            // A move to the pet's monitor can resize this window before it becomes visible.
            PlaceNear(pet, petHandle, handle, work);
        }

        Show();
    }

    private static void PlaceNear(PetWindow pet, IntPtr petHandle, IntPtr handle, System.Drawing.Rectangle work)
    {
        if (NativeMethods.GetWindowRect(handle, out var bounds) &&
            NativeMethods.GetWindowRect(petHandle, out var petBounds))
        {
            int gap = (int)Math.Round(12 * NativeMethods.GetDpiForWindow(petHandle) / 96d);
            int x = work.Left + (work.Width - bounds.Width) / 2;
            int y = work.Top + (work.Height - bounds.Height) / 2;
            if (pet.IsVisible)
            {
                x = petBounds.Left - gap - bounds.Width;
                if (x < work.Left)
                {
                    x = petBounds.Right + gap;
                }

                y = petBounds.Bottom - bounds.Height;
            }

            NativeMethods.MoveWindow(handle,
                Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
                Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _loading.Cancel();
        _loading.Dispose();
        if (_session is not null)
        {
            _session.Changed -= RefreshChoices;
        }
        base.OnClosed(e);
    }

    private async void LoadCharacters(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadCharacters;
        CancellationToken token = _loading.Token;
        CharacterPack? activePack = _session?.Pack;
        if (activePack is not null && _session?.Outfit is not null)
        {
            AddCharacter(new CharacterChoice(activePack));
        }

        try
        {
            if (_characters.FirstOrDefault() is { } activeCharacter)
            {
                BitmapSource? portrait = await Task.Run(() => LoadCharacterPortrait(activeCharacter.Pack, token), token);
                token.ThrowIfCancellationRequested();
                activeCharacter.SetImages(portrait);
            }

            foreach (var group in _installedPacks.Where(pack => _session is not null || pack.Models is not null)
                         .GroupBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(group.Key, activePack?.Id))
                {
                    continue;
                }

                CharacterChoice? character = await Task.Run(() => FindCharacter(group, token), token);
                token.ThrowIfCancellationRequested();
                if (character is not null)
                {
                    AddCharacter(character);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        _charactersLoaded = true;
        UpdateCharacterStatus();
    }

    private void FilterCharacters(object sender, RoutedEventArgs e)
    {
        if (Characters?.ItemsSource is { } items)
        {
            CollectionViewSource.GetDefaultView(items).Refresh();
            UpdateCharacterStatus();
            CardsScroll.ScrollToTop();
        }
    }

    private void UpdateCharacterStatus()
    {
        CharacterListStatus.Text = !_charactersLoaded ? "讀取角色素材…" :
            _characters.Count == 0 ? "找不到可載入的角色素材" : "沒有符合篩選條件的角色";
        CharacterListStatus.Visibility = Characters.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static CharacterChoice? FindCharacter(IEnumerable<CharacterPack> candidates, CancellationToken token)
    {
        foreach (CharacterPack pack in candidates)
        {
            if (pack.Models is not null || pack.FindLoadableOutfit(token) is not null)
            {
                var character = new CharacterChoice(pack);
                character.SetImages(LoadCharacterPortrait(pack, token));
                return character;
            }
        }

        return null;
    }

    private void AddCharacter(CharacterChoice character)
    {
        character.Refresh(_session);
        _characters.Add(character);
        UpdateCharacterStatus();
    }

    private async void ShowOutfits(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CharacterChoice character } button)
        {
            return;
        }

        _lastCharacterButton = button;
        bool loadImages = character.Outfits is null;
        _outfits = character.Outfits ??= character.Pack.Outfits.Select(outfit => new OutfitChoice(character.Pack, outfit)).ToArray();
        Outfits.ItemsSource = _outfits;
        RefreshChoices(this, EventArgs.Empty);
        PageHeading.Text = character.DisplayName + "的造型";
        CharacterPage.Visibility = Visibility.Collapsed;
        Outfits.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        if (_session is null)
        {
            ModelActions.Visibility = Visibility.Visible;
            RefreshModelActions();
        }
        CardsScroll.ScrollToTop();
        BackButton.Focus();

        if (!loadImages)
        {
            return;
        }

        CancellationToken token = _loading.Token;
        try
        {
            await LoadOutfitImagesAsync(character.Outfits, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private static async Task LoadOutfitImagesAsync(IEnumerable<OutfitChoice> choices, CancellationToken token)
    {
        foreach (OutfitChoice choice in choices)
        {
            var (preview, portrait) = await Task.Run(
                () => (LoadChibi(choice.Pack, choice.Outfit), choice.Pack.LoadPortrait(choice.Outfit)), token);
            token.ThrowIfCancellationRequested();
            choice.SetImages(portrait, preview);
        }
    }

    private void ShowCharacters(object sender, RoutedEventArgs e)
    {
        PageHeading.Text = "選擇角色";
        BackButton.Visibility = Visibility.Collapsed;
        Outfits.Visibility = Visibility.Collapsed;
        CharacterPage.Visibility = Visibility.Visible;
        Outfits.ItemsSource = null;
        _outfits = [];
        _modelSelection = null;
        ModelActions.Visibility = Visibility.Collapsed;
        _lastCharacterButton?.Focus();
        _lastCharacterButton?.BringIntoView();
    }

    private static BitmapSource? LoadChibi(CharacterPack pack, OutfitDefinition outfit) =>
        WardrobePreview.CropToVisiblePixels(pack.LoadPreview(outfit));

    private static BitmapSource? LoadCharacterPortrait(CharacterPack pack, CancellationToken token)
    {
        foreach (OutfitDefinition outfit in pack.StartupOutfits)
        {
            token.ThrowIfCancellationRequested();
            if (pack.LoadPortrait(outfit) is { } portrait)
            {
                return WardrobePreview.CropToVisiblePixels(portrait);
            }
        }

        return null;
    }

    private void RefreshChoices(object? sender, EventArgs e)
    {
        foreach (CharacterChoice character in _characters)
        {
            character.Refresh(_session);
        }

        foreach (OutfitChoice choice in _outfits)
        {
            choice.Refresh(_session, _modelSelection == choice);
        }
    }

    private async void ApplyOutfit(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OutfitChoice choice })
        {
            if (_session is null)
            {
                _modelSelection = choice;
                RefreshChoices(this, EventArgs.Empty);
                RefreshModelActions();
                return;
            }
            CancellationToken token = _loading.Token;
            await _session.SelectAsync(choice.Pack, choice.Outfit, _hardwareAccelerationEnabled());
            try
            {
                BitmapSource? image = await Task.Run(() => LoadChibi(choice.Pack, choice.Outfit), token);
                if (!token.IsCancellationRequested)
                {
                    choice.SetImages(choice.Portrait, image);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }
    }

    private async void BuildModel(object sender, RoutedEventArgs e) => await ManageModelAsync(remove: false);

    private async void DeleteModel(object sender, RoutedEventArgs e) => await ManageModelAsync(remove: true);

    private async Task ManageModelAsync(bool remove)
    {
        if (_managingModel || _modelSelection is not { Pack.Models: { } library, Outfit: var outfit })
        {
            return;
        }

        CancellationToken token = _loading.Token;
        _managingModel = true;
        ModelStatus.Text = remove ? "正在刪除影格…" : "正在建置動畫影格…";
        RefreshModelActions();
        string? errorMessage = null;
        try
        {
            bool hardwareAcceleration = _hardwareAccelerationEnabled();
            await Task.Run(() => library.ManageOutfitAsync(outfit, remove, hardwareAcceleration, token), token);
            await LoadOutfitImagesAsync(_outfits.Where(choice => library.ShareModel(outfit, choice.Outfit)), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error))
        {
            errorMessage = error is InvalidDataException ? error.Message :
                "無法變更模型影格，請檢查模型素材及資料夾寫入權限。";
        }
        finally
        {
            _managingModel = false;
        }

        if (!token.IsCancellationRequested)
        {
            RefreshChoices(this, EventArgs.Empty);
            RefreshModelActions();
            if (errorMessage is not null)
            {
                ModelStatus.Text = errorMessage;
            }
        }
    }

    private void RefreshModelActions()
    {
        OutfitChoice? selected = _modelSelection;
        bool built = selected?.Pack.IsOutfitReady(selected.Outfit) == true;
        BackButton.IsEnabled = !_managingModel;
        Outfits.IsEnabled = !_managingModel;
        BuildModelButton.IsEnabled = !_managingModel && selected is not null && !built;
        DeleteModelButton.IsEnabled = !_managingModel && built;
        if (_managingModel)
        {
            return;
        }

        if (selected is not { Pack.Models: { } library })
        {
            ModelStatus.Text = "選擇要管理的造型。";
            return;
        }

        string names = string.Join("／", _outfits.Where(choice => library.ShareModel(selected.Outfit, choice.Outfit))
            .Select(choice => choice.DisplayName));
        ModelStatus.Text = $"{names} · {(built ? "影格已建置" : "尚未建置")}";
    }

    private void CloseOnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private abstract class CardChoice(CharacterPack pack, string displayName) : INotifyPropertyChanged
    {
        internal CharacterPack Pack { get; } = pack;
        public string DisplayName { get; } = displayName;
        public BitmapSource? Preview { get; private set; }
        public BitmapSource? Portrait { get; private set; }
        public bool IsCurrent { get; protected set; }
        public bool HasError { get; protected set; }
        public string Status { get; protected set; } = string.Empty;
        public string PreviewStatus => Preview is not null ? string.Empty : ImagesLoaded ? "暫無小人預覽" : "讀取中…";
        public string PortraitStatus => Portrait is not null ? string.Empty : ImagesLoaded ? "暫無立繪" : "讀取中…";
        public string AccessibleName => string.IsNullOrEmpty(Status) ? DisplayName : $"{DisplayName}，{Status}";
        private bool ImagesLoaded { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void SetImages(BitmapSource? portrait, BitmapSource? preview = null)
        {
            Portrait = portrait;
            Preview = preview;
            ImagesLoaded = true;
            NotifyChanged();
        }

        protected void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private sealed class CharacterChoice(CharacterPack pack)
        : CardChoice(pack, pack.DisplayName)
    {
        internal string? Profession { get; } = WardrobeProfessions.Find(pack.Id);
        internal OutfitChoice[]? Outfits { get; set; }

        internal void Refresh(PetSession? session)
        {
            IsCurrent = session?.Pack == Pack;
            Status = IsCurrent ? "使用中" : string.Empty;
            NotifyChanged();
        }
    }

    private sealed class OutfitChoice(CharacterPack pack, OutfitDefinition outfit) : CardChoice(pack, outfit.DisplayName)
    {
        internal OutfitDefinition Outfit { get; } = outfit;
        internal void Refresh(PetSession? session, bool selected)
        {
            bool ready = Pack.IsOutfitReady(Outfit);
            if (session is null)
            {
                IsCurrent = selected;
                Status = ready ? "影格已建置" : "尚未建置";
                NotifyChanged();
                return;
            }
            IsCurrent = session.Pack == Pack && session.Outfit?.Definition == Outfit;
            HasError = session.HasLoadError(Pack, Outfit);
            Status = session.PendingSelection == (Pack, Outfit) ? ready ? "套用中…" : "正在建置模型…" :
                HasError ? "素材無法讀取" : IsCurrent ? "使用中" : ready ? string.Empty : "尚未建置";
            NotifyChanged();
        }
    }
}
