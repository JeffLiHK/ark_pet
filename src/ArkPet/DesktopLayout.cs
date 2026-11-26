using System;
using ArtPet.Windows.Desktop;

namespace ArtPet.Windows;

internal sealed record OutfitKey(string CharacterId, string OutfitId);
internal sealed record DesktopPosition(string Display, double X, double Y);
internal sealed record SavedPet(int Slot, OutfitKey Outfit, DesktopPosition? Position,
    bool Topmost, bool Visible, ActivityArea Activity, bool AllowPetInteraction = false, bool Flipped = false);
internal sealed record DesktopConfiguration(string Name, SavedPet[] Pets)
{
    internal const int MaxNameLength = 40;
}
internal sealed record DailyOutfit(DateOnly Date, OutfitKey Outfit);
