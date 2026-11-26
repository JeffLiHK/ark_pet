using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArtPet.Windows.Media;
using Microsoft.Win32;
using static ArtPet.Windows.App;

namespace ArtPet.Windows.Desktop;

internal partial class MainInterfaceWindow
{
    private const int PhotoWidth = 960;
    private const int PhotoHeight = 540;
    private static readonly Rect PhotoBounds = new(0, 0, PhotoWidth, PhotoHeight);
    private readonly ObservableCollection<PhotoPet> _photoPets = [];
    private bool _exportingPhoto;

    private void RefreshPhotoPage()
    {
        var pets = _app.Pets.Where(pet => pet.Session.Outfit is not null).ToArray();
        foreach (PhotoPet removed in _photoPets.Where(item => !pets.Contains(item.Pet)).ToArray())
        {
            removed.PropertyChanged -= PhotoPetChanged;
            _photoPets.Remove(removed);
        }
        for (int index = 0; index < pets.Length; index++)
        {
            PetEntry pet = pets[index];
            if (index >= _photoPets.Count || _photoPets[index].Pet != pet)
            {
                var item = new PhotoPet(pet, index);
                item.PropertyChanged += PhotoPetChanged;
                _photoPets.Insert(index, item);
            }
            _photoPets[index].RefreshOutfit();
        }
        RefreshPhotoPreview();
    }

    private void PhotoPetChanged(object? sender, PropertyChangedEventArgs e)
    {
        PhotoStatus.Text = string.Empty;
        RefreshPhotoPreview();
    }

    private void RefreshPhotoPreview()
    {
        PhotoPreview.Source = new DrawingImage(DrawPhoto());
        ExportPhoto.IsEnabled = !_exportingPhoto && _photoPets.Any(pet => pet.Included && pet.SelectedPose is not null);
        if (_photoPets.Count == 0)
        {
            PhotoStatus.Text = "選擇已建置的造型後，即可安排合照。";
        }
        else if (!ExportPhoto.IsEnabled && !_exportingPhoto)
        {
            PhotoStatus.Text = "請至少勾選一隻小人。";
        }
    }

    private DrawingGroup DrawPhoto()
    {
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(PhotoBounds) };
        using DrawingContext context = drawing.Open();
        context.DrawRectangle(Brushes.Transparent, null, PhotoBounds);
        foreach (PhotoPet pet in _photoPets.Where(pet => pet.Included && pet.SelectedPose is not null))
        {
            BitmapSource frame = pet.Frame!;
            LoadedOutfit outfit = pet.Pet.Session.Outfit!;
            double size = outfit.RestingBounds is { IsEmpty: false, Height: > 0 } bounds
                ? Math.Min(300, 140 / bounds.Height)
                : 215;
            double x = pet.X * PhotoWidth / 100;
            double y = pet.Y * PhotoHeight / 100;
            context.PushTransform(new ScaleTransform(pet.Flipped ? -1 : 1, 1, x, y));
            context.DrawImage(frame, new Rect(x - outfit.Anchor.X * size, y - outfit.Anchor.Y * size, size, size));
            context.Pop();
        }
        return drawing;
    }

    private BitmapSource CreatePhotoBitmap()
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawDrawing(DrawPhoto());
        }
        var bitmap = new RenderTargetBitmap(PhotoWidth, PhotoHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private async void ExportPhotoClick(object sender, RoutedEventArgs e)
    {
        if (_exportingPhoto || !ExportPhoto.IsEnabled)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "儲存桌面合照",
            Filter = "PNG 圖片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"ArkPet-{DateTime.Today:yyyy-MM-dd}.png"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _exportingPhoto = true;
        ExportPhoto.IsEnabled = false;
        PhotoStatus.Text = "正在儲存合照…";
        try
        {
            BitmapSource bitmap = CreatePhotoBitmap();
            await Task.Run(() => WritePhoto(dialog.FileName, bitmap));
            PhotoStatus.Text = $"已儲存 {Path.GetFileName(dialog.FileName)}。";
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error) ||
            error is System.Runtime.InteropServices.COMException)
        {
            PhotoStatus.Text = "無法儲存合照。請確認儲存位置可寫入後重試。";
        }
        finally
        {
            _exportingPhoto = false;
            RefreshPhotoPreview();
        }
    }

    private static void WritePhoto(string path, BitmapSource bitmap)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class PhotoPet(PetEntry pet, int index) : INotifyPropertyChanged
    {
        private LoadedOutfit? _outfit;
        private bool _included = index < 2;
        private bool _flipped = index % 2 == 1;
        private double _x = index % 2 == 0 ? 35 : 65;
        private double _y = 67;
        private Pose? _selectedPose;

        internal PetEntry Pet { get; } = pet;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Title => $"小人 {Pet.Slot + 1} · {Pet.Session.Pack?.DisplayName} · {_outfit?.Definition.DisplayName}";
        public Pose[] Poses { get; private set; } = [];
        public bool Included { get => _included; set => Set(ref _included, value); }
        public bool Flipped { get => _flipped; set => Set(ref _flipped, value); }
        public double X { get => _x; set => Set(ref _x, value); }
        public double Y { get => _y; set => Set(ref _y, value); }
        internal BitmapSource? Frame { get; private set; }
        public Pose? SelectedPose
        {
            get => _selectedPose;
            set
            {
                if (_selectedPose == value)
                {
                    return;
                }
                // Materialize one pose frame without the decoder owned by the loading thread.
                Frame = value is { } pose ? CopyFrame(pose.Clip.Frames[0]) : null;
                Set(ref _selectedPose, value);
            }
        }

        private static BitmapSource CopyFrame(BitmapSource source)
        {
            int stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
            byte[] pixels = new byte[stride * source.PixelHeight];
            source.CopyPixels(pixels, stride, 0);
            BitmapSource copy = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
                source.Format, source.Palette, pixels, stride);
            copy.Freeze();
            return copy;
        }

        internal void RefreshOutfit()
        {
            if (_outfit == Pet.Session.Outfit)
            {
                return;
            }
            string? previousPose = SelectedPose?.Name;
            _outfit = Pet.Session.Outfit;
            Poses = GetPoses(_outfit);
            Changed(nameof(Title));
            Changed(nameof(Poses));
            SelectedPose = Poses.FirstOrDefault(pose => pose.Name == previousPose) ?? Poses.FirstOrDefault();
        }

        private void Set<T>(ref T current, T value, [CallerMemberName] string? property = null)
        {
            if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(current, value))
            {
                current = value;
                Changed(property);
            }
        }

        private void Changed(string? property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
