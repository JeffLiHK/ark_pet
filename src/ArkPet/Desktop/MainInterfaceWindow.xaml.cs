using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ArtPet.Windows.Media;
using static ArtPet.Windows.App;
using Screen = System.Windows.Forms.Screen;

namespace ArtPet.Windows.Desktop;

internal partial class MainInterfaceWindow : Window
{
    private enum Page { Pets, Voices, Layout, Photo }
    private readonly App _app;
    private readonly PetVoicePlayer _voice;
    private readonly ObservableCollection<PetCard> _cards = [];
    private Page _page;
    private bool _narrow;
    private bool _showDetails;
    private bool _refreshing;
    private bool _closed;
    private LoadedOutfit? _previewOutfit;
    private string? _previewCharacter;
    private long _poseStarted;
    private long _voiceRequest;
    private VoiceChoice[] _voices = [];
    private PetEntry? SelectedPet => (PetList.SelectedItem as PetCard)?.Pet;

    internal MainInterfaceWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _voice = new PetVoicePlayer(subtitle =>
        {
            PreviewSubtitle.Text = subtitle ?? string.Empty;
            LibrarySubtitle.Text = PreviewSubtitle.Text;
        }, app.VoiceRoot);
        _voice.LinePlayed += app.RecordVoice;
        PetList.ItemsSource = _cards;
        PhotoPets.ItemsSource = _photoPets;
        SourceInitialized += (_, _) => FitWorkingArea();
        Activated += (_, _) => RefreshVoiceAssets();
        Closed += (_, _) =>
        {
            _closed = true;
            _voiceRequest++;
            _voice.Stop();
            CompositionTarget.Rendering -= RenderPose;
        };
        StyleSelected(PetsPageButton, true);
        Refresh();
    }

    private void FitWorkingArea()
    {
        var screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var work = screen.WorkingArea;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        NativeMethods.MoveWindow(handle, work.Left, work.Top);
        double dpi = NativeMethods.GetDpiForWindow(handle) / 96d;
        Width = Math.Min(Width, work.Width / dpi);
        Height = Math.Min(Height, work.Height / dpi);
        NativeMethods.MoveWindow(handle, work.Left + Math.Max(0, (work.Width - (int)(Width * dpi)) / 2),
            work.Top + Math.Max(0, (work.Height - (int)(Height * dpi)) / 2));
    }

    internal void Refresh()
    {
        if (_closed || _refreshing)
        {
            return;
        }

        _refreshing = true;
        int previousIndex = Math.Max(0, PetList.SelectedIndex);
        PetCard? previous = PetList.SelectedItem as PetCard;
        PetEntry[] pets = _app.Pets.ToArray();
        foreach (PetCard card in _cards.Where(card => !pets.Contains(card.Pet)).ToArray())
        {
            _cards.Remove(card);
        }
        for (int index = 0; index < pets.Length; index++)
        {
            if (index >= _cards.Count || _cards[index].Pet != pets[index])
            {
                _cards.Insert(index, new PetCard(pets[index]));
            }
            _cards[index].Refresh();
        }

        if (previous is not null && _cards.Contains(previous))
        {
            PetList.SelectedItem = previous;
        }
        else if (_cards.Count > 0)
        {
            PetList.SelectedIndex = Math.Min(previousIndex, _cards.Count - 1);
        }

        bool installed = _app.InstalledPacks.Count > 0;
        PetList.Visibility = ShowWhen(installed);
        EmptyResources.Visibility = ShowWhen(!installed);
        AssetStatusText.Text = _app.AssetStatus ?? string.Empty;
        StyleSelected(Daily, _app.Scenario == DesktopScenario.Daily);
        StyleSelected(FocusMode, _app.Scenario == DesktopScenario.Focus);
        StyleSelected(HideMode, _app.Scenario == DesktopScenario.Hidden);
        _refreshing = false;
        RefreshDetails();
        UpdateLayoutMode();
        if (_page == Page.Layout)
        {
            RefreshLayoutPage();
        }
        if (_page == Page.Photo)
        {
            RefreshPhotoPage();
        }
    }

    internal void SelectPet(PetEntry pet)
    {
        PetList.SelectedItem = _cards.FirstOrDefault(card => card.Pet == pet);
        PetList.ScrollIntoView(PetList.SelectedItem);
    }

    private void RefreshDetails()
    {
        PetEntry? pet = SelectedPet;
        AddButton.IsEnabled = pet?.Window.HasOutfit == true;
        PetHeading.Text = (PetList.SelectedItem as PetCard)?.Name ?? string.Empty;
        var outfit = pet?.Session.Outfit;
        EmptyOutfit.Visibility = ShowWhen(outfit is null);
        OutfitDetails.Visibility = ShowWhen(outfit is not null);
        if (_previewOutfit != outfit)
        {
            _previewOutfit = outfit;
            PosePicker.ItemsSource = GetPoses(outfit);
            PosePicker.SelectedIndex = outfit is null ? -1 : 0;
        }
        if (_previewCharacter != pet?.Session.Pack?.Id)
        {
            _previewCharacter = pet?.Session.Pack?.Id;
            if (_page == Page.Pets)
            {
                RefreshVoiceAssets();
            }
        }
        if (pet is not null)
        {
            PetInteractionEnabled.IsChecked = pet.Window.AllowPetInteraction;
            RefreshActivity(pet.Window);
        }
    }

    private void RefreshActivity(PetWindow window)
    {
        _refreshing = true;
        ActivityArea area = window.Activity;
        MonitorOption[] monitors = Screen.AllScreens.Select((screen, index) =>
            new MonitorOption(screen.DeviceName, screen.Primary ? $"螢幕 {index + 1}（主螢幕）" : $"螢幕 {index + 1}")).ToArray();
        if (area.Display is { } missing && monitors.All(monitor => monitor.Id != missing))
        {
            monitors = [.. monitors, new MonitorOption(missing, $"已中斷的螢幕 ({missing})")];
        }
        if (MonitorPicker.ItemsSource is not MonitorOption[] current || !current.SequenceEqual(monitors))
        {
            MonitorPicker.ItemsSource = monitors;
        }
        ScopePicker.SelectedIndex = (int)area.Scope;
        CornerPicker.SelectedIndex = (int)area.Corner;
        MonitorPicker.SelectedIndex = Array.FindIndex(monitors, monitor => monitor.Id == area.Display);
        MonitorPicker.Visibility = ShowWhen(area.Scope != ActivityScope.CurrentDisplay);
        CornerPicker.Visibility = ShowWhen(area.Scope == ActivityScope.Corner);
        string fallback = window.UsesFallbackActivityDisplay ? "指定螢幕已中斷，目前使用主螢幕。" : string.Empty;
        string outside = !window.IsWithinActivityArea ? "目前在活動範圍外，自動行走已暫停。" : string.Empty;
        RangeStatus.Text = fallback + outside;
        _refreshing = false;
    }

    private void ActivityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || SelectedPet?.Window is not { } window || ScopePicker.SelectedIndex < 0)
        {
            return;
        }
        var scope = (ActivityScope)ScopePicker.SelectedIndex;
        string? monitor = (MonitorPicker.SelectedItem as MonitorOption)?.Id ?? Screen.PrimaryScreen?.DeviceName;
        window.SetActivityArea(new ActivityArea(scope, monitor, (ActivityCorner)Math.Max(0, CornerPicker.SelectedIndex)));
    }

    private void ReturnToActivityArea(object sender, RoutedEventArgs e) => SelectedPet?.Window.ReturnToActivityArea();

    private void ResizeLayout(object sender, SizeChangedEventArgs e) => UpdateLayoutMode();

    private void UpdateLayoutMode()
    {
        if (DetailsScroll is null)
        {
            return;
        }

        _narrow = ActualWidth < 900;
        bool stackedHeader = ActualWidth < 600;
        Grid.SetColumn(ScenarioNavigation, stackedHeader ? 0 : 1);
        Grid.SetRow(ScenarioNavigation, stackedHeader ? 1 : 0);
        Grid.SetColumnSpan(ScenarioNavigation, stackedHeader ? 3 : 1);
        RailColumn.Width = new GridLength(_narrow ? 0 : 156);
        Grid.SetColumn(Rail, 0);
        Grid.SetColumnSpan(Rail, _narrow ? 2 : 1);
        Grid.SetRowSpan(Rail, _narrow ? 1 : 2);
        Rail.BorderThickness = _narrow ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        PageNavigation.Orientation = _narrow ? Orientation.Horizontal : Orientation.Vertical;
        Grid.SetColumn(Pages, _narrow ? 0 : 1);
        Grid.SetColumnSpan(Pages, _narrow ? 2 : 1);
        Grid.SetRow(Pages, _narrow ? 1 : 0);
        Grid.SetRowSpan(Pages, _narrow ? 1 : 2);
        ListColumn.Width = _narrow ? new GridLength(0) : new GridLength(308);
        Grid.SetColumn(PetListPanel, _narrow ? 1 : 0);
        DetailsScroll.Margin = _narrow ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        PetListPanel.Visibility = ShowWhen(!_narrow || !_showDetails || _app.InstalledPacks.Count == 0);
        DetailsScroll.Visibility = ShowWhen(SelectedPet is not null && _app.InstalledPacks.Count > 0 && (!_narrow || _showDetails));
        BackButton.Visibility = ShowWhen(_narrow);
        UpdatePreviewAnimation();
    }

    private static Visibility ShowWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private static void StyleSelected(Button button, bool selected)
    {
        button.Background = Brush(selected ? "#233D39" : "#222B33");
        button.Foreground = Brush(selected ? "#72DDC3" : "#F2F5F7");
        button.BorderBrush = Brush(selected ? "#72DDC3" : "#465963");
        button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "已選取" : string.Empty);
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromInvariantString(color)!;

    private void SwitchScenario(object sender, RoutedEventArgs e) =>
        _app.SetScenario(Enum.Parse<DesktopScenario>((string)((Button)sender).Tag));

    private void SwitchPage(object sender, RoutedEventArgs e)
    {
        _page = Enum.Parse<Page>((string)((Button)sender).Tag);
        PetsPage.Visibility = ShowWhen(_page == Page.Pets);
        VoicesPage.Visibility = ShowWhen(_page == Page.Voices);
        LayoutPage.Visibility = ShowWhen(_page == Page.Layout);
        PhotoPage.Visibility = ShowWhen(_page == Page.Photo);
        StyleSelected(PetsPageButton, _page == Page.Pets);
        StyleSelected(VoicesPageButton, _page == Page.Voices);
        StyleSelected(LayoutPageButton, _page == Page.Layout);
        StyleSelected(PhotoPageButton, _page == Page.Photo);
        UpdatePreviewAnimation();
        RefreshVoiceAssets();
        if (_page == Page.Layout)
        {
            RefreshLayoutPage();
        }
        if (_page == Page.Photo)
        {
            RefreshPhotoPage();
        }
    }

    private void PetInteractionClick(object sender, RoutedEventArgs e) =>
        SelectedPet?.Window.SetPetInteraction(PetInteractionEnabled.IsChecked == true);

    private void RefreshLayoutPage()
    {
        RememberLayout.IsChecked = _app.Settings.RememberDesktopLayout;
        RotationEnabled.IsChecked = _app.Settings.DailyRotationEnabled;
        RotationEnabled.IsEnabled = _app.InstalledPacks.Count > 0 || _app.Settings.DailyRotationEnabled;
        LayoutNotices.ItemsSource = _app.LayoutNotices;
        if (RotationCharacterPicker.ItemsSource is null)
        {
            RotationCharacter[] characters = _app.InstalledPacks.GroupBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase)
                .Select(packs => new RotationCharacter(packs.Key, packs.First().DisplayName,
                    packs.SelectMany(pack => pack.Outfits).DistinctBy(outfit => outfit.Id).ToArray())).ToArray();
            RotationCharacterPicker.ItemsSource = characters;
            RotationCharacterPicker.SelectedIndex = characters.Length == 0 ? -1 : 0;
        }
        RefreshRotationStatus();
        RefreshRotationOutfits();
        RefreshMissingCandidates();
    }

    private void RefreshMissingCandidates()
    {
        RotationCharacter[] characters = (RotationCharacter[]?)RotationCharacterPicker.ItemsSource ?? [];
        MissingRotationOutfits.ItemsSource = _app.Settings.RotationCandidates
            .Where(key => !characters.Any(character =>
                character.Id == key.CharacterId && character.Outfits.Any(outfit => outfit.Id == key.OutfitId)))
            .Select(key => new RotationOption(key, $"已不可用 · {key.CharacterId} / {key.OutfitId}", true)).ToArray();
    }

    private void RefreshRotationStatus()
    {
        int count = _app.Settings.RotationCandidates.Length;
        RotationCount.Text = $"已選擇 {count} 套輪值造型";
        RotationStatusText.Text = _app.InstalledPacks.Count == 0 ? "請先在資源管理安裝角色資源。"
            : _app.Settings.DailyRotationEnabled && count == 0 ? "請選擇至少一套輪值造型。"
            : _app.RotationStatus;
    }

    private void RefreshRotationOutfits()
    {
        RotationOption[] options = RotationCharacterPicker.SelectedItem is RotationCharacter character
            ? character.Outfits.Select(outfit =>
            {
                var key = new OutfitKey(character.Id, outfit.Id);
                return new RotationOption(key, outfit.DisplayName, _app.Settings.RotationCandidates.Contains(key));
            }).ToArray()
            : [];
        if (RotationOutfits.ItemsSource is not RotationOption[] current || !current.SequenceEqual(options))
        {
            RotationOutfits.ItemsSource = options;
        }
    }

    private void RememberLayoutClick(object sender, RoutedEventArgs e) =>
        _app.SetRememberDesktop(RememberLayout.IsChecked == true);

    private void RotationEnabledClick(object sender, RoutedEventArgs e)
    {
        _app.SetRotationEnabled(RotationEnabled.IsChecked == true);
        RefreshRotationStatus();
    }

    private void RotationCharacterChanged(object sender, SelectionChangedEventArgs e) => RefreshRotationOutfits();

    private void RotationCandidateClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: RotationOption option } check)
        {
            _app.SetRotationCandidate(option.Key, check.IsChecked == true);
            RefreshRotationStatus();
        }
    }

    private void RemoveRotationCandidate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RotationOption option })
        {
            _app.SetRotationCandidate(option.Key, included: false);
            RefreshRotationStatus();
            RefreshMissingCandidates();
            RotationCharacterPicker.Focus();
        }
    }

    private sealed record RotationCharacter(string Id, string Name, OutfitDefinition[] Outfits);
    private sealed record RotationOption(OutfitKey Key, string Label, bool Included);

    private void PetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && e.Source == PetList)
        {
            RefreshDetails();
        }
    }

    private void OpenPetDetails(object sender, RoutedEventArgs e)
    {
        if (PetFrom(sender) is { } pet)
        {
            SelectPet(pet);
            _showDetails = true;
            UpdateLayoutMode();
            if (_narrow)
            {
                DetailsScroll.ScrollToTop();
                BackButton.Focus();
                RefreshVoiceAssets();
            }
        }
    }

    private void BackToPets(object sender, RoutedEventArgs e)
    {
        _showDetails = false;
        _voice.Stop();
        UpdateLayoutMode();
        if (PetList.ItemContainerGenerator.ContainerFromItem(PetList.SelectedItem) is ListBoxItem item)
        {
            item.Focus();
        }
    }

    private PetEntry? PetFrom(object sender) => ((FrameworkElement)sender).DataContext is PetCard card
        ? card.Pet
        : SelectedPet;

    private void SettingsClick(object sender, RoutedEventArgs e) => _app.OpenSettings();
    private void ResourcesClick(object sender, RoutedEventArgs e) => _app.OpenResources();
    private void DownloadResources(object sender, RoutedEventArgs e) => _app.OpenResources(download: true);

    private void AddPet(object sender, RoutedEventArgs e)
    {
        if (SelectedPet is { } pet)
        {
            _app.AddPet(pet, select: true);
        }
    }

    private void OpenWardrobe(object sender, RoutedEventArgs e)
    {
        if (PetFrom(sender) is { } pet)
        {
            _app.OpenWardrobe(pet);
        }
    }

    private void ToggleVisibility(object sender, RoutedEventArgs e)
    {
        if (PetFrom(sender)?.Window is { } window)
        {
            window.SetVisibilityPreference(!window.WantsVisible);
        }
    }

    private void OpenMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || PetFrom(sender) is not { } pet)
        {
            return;
        }
        var menu = new ContextMenu { PlacementTarget = button };
        AddMenuItem(menu, "轉身", pet.Window.TurnAround, pet.Window.HasOutfit);
        var pin = AddMenuItem(menu, "置頂", () => pet.Window.Topmost = !pet.Window.Topmost);
        pin.IsCheckable = true;
        pin.IsChecked = pet.Window.Topmost;
        AddMenuItem(menu, "移回初始位置", () => pet.Window.ShowPet(resetPosition: true), pet.Window.HasOutfit);
        if (pet.Slot != 0)
        {
            AddMenuItem(menu, "移除", () => App.RemovePet(pet));
        }
        menu.Closed += (_, _) =>
        {
            if (button.IsVisible)
            {
                button.Focus();
            }
            else if (PetList.ItemContainerGenerator.ContainerFromItem(PetList.SelectedItem) is ListBoxItem item)
            {
                item.Focus();
            }
        };
        menu.IsOpen = true;
        if (menu.Items[0] is MenuItem first)
        {
            first.Focus();
        }
    }

    private static MenuItem AddMenuItem(ContextMenu menu, string label, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    private static Pose[] GetPoses(LoadedOutfit? outfit)
    {
        if (outfit is null)
        {
            return [];
        }
        return new (string Name, FrameClip? Clip)[]
        {
            ("待機", outfit.Idle), ("行走", outfit.Move), ("坐下", outfit.Sit), ("睡覺", outfit.Sleep), ("互動", outfit.Touch)
        }.Where(pose => pose.Clip is not null).Select(pose => new Pose(pose.Name, pose.Clip!)).ToArray();
    }

    private void PoseChanged(object sender, SelectionChangedEventArgs e)
    {
        _poseStarted = Stopwatch.GetTimestamp();
        PreviewLabel.Text = PosePicker.SelectedItem is Pose pose ? $"{pose.Name}預覽" : string.Empty;
        RenderPose(this, EventArgs.Empty);
    }

    private void WindowStateChanged(object? sender, EventArgs e) => UpdatePreviewAnimation();

    private void UpdatePreviewAnimation()
    {
        CompositionTarget.Rendering -= RenderPose;
        if (!_closed && _page == Page.Pets && DetailsScroll.Visibility == Visibility.Visible && WindowState != WindowState.Minimized)
        {
            CompositionTarget.Rendering += RenderPose;
        }
    }

    private void RenderPose(object? sender, EventArgs e)
    {
        if (PosePicker.SelectedItem is Pose { Clip: { } clip })
        {
            double seconds = Stopwatch.GetElapsedTime(_poseStarted).TotalSeconds;
            int outputFps = _app.Settings.AnimationFps;
            double sampled = Math.Floor(seconds * outputFps) / outputFps;
            PreviewImage.Source = clip.Frames[(int)(sampled * clip.Fps % clip.Frames.Length)];
        }
        else
        {
            PreviewImage.Source = null;
        }
    }

    internal void StopVoice() => _voice.Stop();

    internal async void RefreshVoiceAssets()
    {
        if (_closed || _voice is null)
        {
            return;
        }
        long request = ++_voiceRequest;
        if (_page == Page.Voices)
        {
            RefreshVoiceCharacters();
        }
        string? character = _page switch
        {
            Page.Voices => (VoiceCharacterPicker.SelectedItem as VoiceCharacter)?.Id,
            Page.Pets => _previewCharacter,
            _ => null
        };
        string? previous = (PreviewVoicePicker.SelectedItem as VoiceChoice)?.Line.Id;
        _voice.Configure(character, _app.Settings.VoiceLanguage, _app.Settings.SubtitleLanguage);
        _voice.SetVolume(_app.Settings.VoiceVolumePercent);
        _voices = [];
        PreviewVoicePicker.ItemsSource = null;
        PreviewVoiceStatus.Text = character is null ? string.Empty : "正在讀取語音…";
        RefreshVoiceHistory();
        var available = await _voice.GetAvailableAsync();
        if (request != _voiceRequest)
        {
            return;
        }

        _voices = available.OrderBy(line => line.Id, StringComparer.Ordinal)
            .Select(line => new VoiceChoice(line)).ToArray();
        PreviewVoicePicker.ItemsSource = _voices;
        PreviewVoicePicker.SelectedItem = _voices.FirstOrDefault(item => item.Line.Id == previous) ?? _voices.FirstOrDefault();
        PreviewVoiceStatus.Text = _voices.Length == 0 ? "此角色目前未安裝所選語言的配音。" : string.Empty;
        RefreshVoiceHistory();
    }

    private void RefreshVoiceCharacters()
    {
        _refreshing = true;
        string? selected = (VoiceCharacterPicker.SelectedItem as VoiceCharacter)?.Id ?? _previewCharacter;
        VoiceCharacter[] characters = _app.InstalledPacks.Select(pack => new VoiceCharacter(pack.Id, pack.DisplayName))
            .Concat(_app.Settings.VoiceFavorites.Concat(_app.RecentVoices).Select(voice => new VoiceCharacter(voice.CharacterId, voice.CharacterId)))
            .DistinctBy(character => character.Id).ToArray();
        if (VoiceCharacterPicker.ItemsSource is not VoiceCharacter[] current || !current.SequenceEqual(characters))
        {
            VoiceCharacterPicker.ItemsSource = characters;
        }
        VoiceCharacterPicker.SelectedItem = characters.FirstOrDefault(character => character.Id == selected) ?? characters.FirstOrDefault();
        _refreshing = false;
    }

    internal void RefreshVoiceHistory()
    {
        if (_closed)
        {
            return;
        }
        PreferFavorites.IsChecked = _app.Settings.PreferFavoriteVoices;
        UpdateVoiceButtons();
        if (_page != Page.Voices)
        {
            return;
        }
        string? character = (VoiceCharacterPicker.SelectedItem as VoiceCharacter)?.Id;
        VoiceRow[] favorites = VoiceRows(_app.Settings.VoiceFavorites, character);
        VoiceRow[] recent = VoiceRows(_app.RecentVoices, character);
        SetRows(FavoritesList, favorites);
        SetRows(RecentList, recent);
        EmptyFavorites.Visibility = ShowWhen(favorites.Length == 0);
        EmptyRecent.Visibility = ShowWhen(recent.Length == 0);
    }

    private static void SetRows(ItemsControl list, VoiceRow[] rows)
    {
        if (list.ItemsSource is not VoiceRow[] current || !current.SequenceEqual(rows))
        {
            list.ItemsSource = rows;
        }
    }

    private VoiceRow[] VoiceRows(System.Collections.Generic.IEnumerable<VoiceFavorite> voices, string? character) => voices
        .Where(voice => voice.CharacterId == character)
        .Select(voice => new VoiceRow(voice, _voices.FirstOrDefault(item => item.Line.Id == voice.LineId)?.Line, IsFavorite(voice)))
        .ToArray();

    private bool IsFavorite(VoiceFavorite voice) => _app.Settings.VoiceFavorites.Any(item =>
        item.CharacterId == voice.CharacterId && item.LineId == voice.LineId);

    private VoiceFavorite? CurrentPreviewVoice => PreviewVoicePicker.SelectedItem is VoiceChoice selected && _previewCharacter is { } character
        ? new VoiceFavorite(character, selected.Line.Id, selected.Line.Subtitle)
        : null;

    private void VoiceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _voice?.Stop();
        UpdateVoiceButtons();
    }

    private void UpdateVoiceButtons()
    {
        if (PlayPreviewVoice is null)
        {
            return;
        }
        VoiceFavorite? voice = CurrentPreviewVoice;
        FavoritePreviewVoice.IsEnabled = PlayPreviewVoice.IsEnabled = voice is not null;
        FavoritePreviewVoice.Content = voice is not null && IsFavorite(voice) ? "取消收藏" : "收藏";
    }

    private void PlayPreview(object sender, RoutedEventArgs e)
    {
        if (PreviewVoicePicker.SelectedItem is VoiceChoice choice && _previewCharacter is { } character)
        {
            _voice.PlayLine(character, choice.Line);
        }
    }

    private void TogglePreviewFavorite(object sender, RoutedEventArgs e)
    {
        if (CurrentPreviewVoice is { } voice)
        {
            _app.ToggleFavorite(voice);
        }
    }

    private void VoiceCharacterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && _page == Page.Voices)
        {
            RefreshVoiceAssets();
        }
    }

    private void PreferFavoritesClick(object sender, RoutedEventArgs e) =>
        _app.UpdateSettings(_app.Settings with { PreferFavoriteVoices = PreferFavorites.IsChecked == true });

    private void ReplayVoice(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is VoiceRow { Line: { } line } row)
        {
            _voice.PlayLine(row.Voice.CharacterId, line);
        }
    }

    private void ToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is VoiceRow row)
        {
            _app.ToggleFavorite(row.Voice);
        }
    }

    private sealed record Pose(string Name, FrameClip Clip);
    private sealed record MonitorOption(string Id, string Label);
    private sealed record VoiceCharacter(string Id, string Name);
    private sealed record VoiceChoice(PetVoicePlayer.VoiceLine Line)
    {
        public string Label => Line.Subtitle ?? Line.Id;
    }

    private sealed record VoiceRow(VoiceFavorite Voice, PetVoicePlayer.VoiceLine? Line, bool Favorite)
    {
        public string Label => Line?.Subtitle ?? Voice.Text ?? Voice.LineId;
        public bool CanPlay => Line is not null;
        public string Availability => CanPlay ? "播放台詞" : "目前語言沒有這句配音";
        public string FavoriteAction => Favorite ? "取消收藏" : "收藏";
    }

    private sealed class PetCard(PetEntry pet) : INotifyPropertyChanged
    {
        private LoadedOutfit? _outfit;
        private string? _status;
        private bool _wantsVisible;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal PetEntry Pet { get; } = pet;
        public string Name => $"小人 {Pet.Slot + 1} · {Pet.Session.Pack?.DisplayName ?? "尚未選擇角色"}";
        public string OutfitName => Pet.Session.Outfit?.Definition.DisplayName ?? "尚未選取造型";
        public bool HasOutfit => Pet.Window.HasOutfit;
        public string Status => Pet.Window.ScenarioHidden ? "情境已隱藏" : Pet.Window.WantsVisible && HasOutfit ? "顯示中" : "已隱藏";
        public string VisibilityAction => Pet.Window.WantsVisible ? "隱藏" : "顯示";
        public ImageSource? Portrait { get; private set; } = pet.Window.Icon;

        internal void Refresh()
        {
            if (_outfit != Pet.Session.Outfit)
            {
                _outfit = Pet.Session.Outfit;
                LoadPortrait();
                Notify(nameof(Name), nameof(OutfitName), nameof(HasOutfit));
            }
            if (_status != Status || _wantsVisible != Pet.Window.WantsVisible)
            {
                _status = Status;
                _wantsVisible = Pet.Window.WantsVisible;
                Notify(nameof(Status), nameof(VisibilityAction));
            }
        }

        private async void LoadPortrait()
        {
            if (Pet.Session.Pack is not { } pack || _outfit is not { } outfit)
            {
                return;
            }
            ImageSource? portrait = await Task.Run(() => pack.LoadPortrait(outfit.Definition));
            if (_outfit == outfit)
            {
                Portrait = portrait ?? outfit.Preview ?? Pet.Window.Icon;
                Notify(nameof(Portrait));
            }
        }

        private void Notify(params string[] names)
        {
            foreach (string name in names)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
