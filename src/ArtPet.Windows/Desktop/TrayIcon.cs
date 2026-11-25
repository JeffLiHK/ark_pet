using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Desktop;

internal sealed record TrayPet(string Name, PetWindow Window, Action? OpenWardrobe, Action? Remove);

internal sealed class TrayIcon : IDisposable
{
    private readonly Icon _fallbackImage;
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _icon;
    private Icon? _characterImage;

    public TrayIcon(PetWindow pet, Action exit, Action openSettings, Action? openWardrobe,
        Action addPet, Func<IEnumerable<TrayPet>> getPets, BitmapSource? appIcon)
    {
        if (appIcon is null)
        {
            var iconUri = new Uri("/ArtPet.Windows;component/Media/ArtPet.ico", UriKind.Relative);
            using var imageStream = System.Windows.Application.GetResourceStream(iconUri)!.Stream;
            _fallbackImage = new Icon(imageStream, SystemInformation.SmallIconSize);
        }
        else
        {
            _fallbackImage = CharacterIcon.ToTrayIcon(appIcon);
        }

        var visibilityItem = new ToolStripMenuItem();
        visibilityItem.Click += (_, _) =>
        {
            if (pet.IsVisible)
            {
                pet.Hide();
            }
            else
            {
                pet.ShowPet();
            }
        };

        var resetItem = new ToolStripMenuItem("移回主螢幕右下角");
        resetItem.Click += (_, _) => pet.ShowPet(resetPosition: true);

        var topmostItem = new ToolStripMenuItem("置頂");
        topmostItem.Click += (_, _) => pet.Topmost = !pet.Topmost;

        var exitItem = new ToolStripMenuItem("結束程式");
        exitItem.Click += (_, _) => exit();
        var addPetItem = new ToolStripMenuItem("新增一隻小人", null, (_, _) => addPet());
        var petsItem = new ToolStripMenuItem("管理小人");

        _menu.Items.Add(visibilityItem);
        if (openWardrobe is not null)
        {
            var wardrobeItem = new ToolStripMenuItem("衣櫃…");
            wardrobeItem.Click += (_, _) => openWardrobe();
            _menu.Items.Add(wardrobeItem);
        }

        var settingsItem = new ToolStripMenuItem("設定…");
        settingsItem.Click += (_, _) => openSettings();

        _menu.Items.AddRange([settingsItem, resetItem, topmostItem, new ToolStripSeparator(),
            addPetItem, petsItem, new ToolStripSeparator(), exitItem]);
        _menu.Opening += (_, _) =>
        {
            visibilityItem.Text = pet.IsVisible ? "隱藏小人" : "顯示小人";
            visibilityItem.Enabled = pet.HasOutfit;
            resetItem.Enabled = pet.HasOutfit;
            addPetItem.Enabled = pet.HasOutfit;
            topmostItem.Checked = pet.Topmost;
            RefreshPetMenu(petsItem, getPets());
        };

        _icon = new NotifyIcon { Text = "Art Pet", ContextMenuStrip = _menu };
        SetCharacterIcon(pet.Icon as BitmapSource);
        _icon.Visible = true;
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                pet.ShowPet();
            }
        };
    }

    private static void RefreshPetMenu(ToolStripMenuItem menu, IEnumerable<TrayPet> pets)
    {
        while (menu.DropDownItems.Count > 0)
        {
            ToolStripItem item = menu.DropDownItems[0];
            menu.DropDownItems.RemoveAt(0);
            item.Dispose();
        }

        foreach (TrayPet pet in pets)
        {
            var entry = new ToolStripMenuItem(pet.Name);
            entry.DropDownItems.Add(pet.Window.IsVisible ? "隱藏" : "顯示", null, (_, _) =>
            {
                if (pet.Window.IsVisible)
                {
                    pet.Window.Hide();
                }
                else
                {
                    pet.Window.ShowPet();
                }
            });
            if (pet.OpenWardrobe is { } open)
            {
                entry.DropDownItems.Add("衣櫃…", null, (_, _) => open());
            }

            entry.DropDownItems.Add("轉身", null, (_, _) => pet.Window.TurnAround());
            entry.DropDownItems.Add("移回起始位置", null, (_, _) => pet.Window.ShowPet(resetPosition: true));
            if (pet.Remove is { } remove)
            {
                entry.DropDownItems.Add("移除", null, (_, _) => remove());
            }

            menu.DropDownItems.Add(entry);
        }
    }

    internal void SetCharacterIcon(BitmapSource? artwork)
    {
        Icon? replacement = artwork is null ? null : CharacterIcon.ToTrayIcon(artwork);
        _icon.Icon = replacement ?? _fallbackImage;
        _characterImage?.Dispose();
        _characterImage = replacement;
    }

    internal void ShowSettingsSaveError() =>
        _icon.ShowBalloonTip(3500, "Art Pet", "目前無法保存設定。重開後會恢復上次保存的設定。", ToolTipIcon.Warning);

    internal void ShowSetupStatus(string message) =>
        _icon.ShowBalloonTip(5000, "Art Pet", message, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Dispose();
        _menu.Dispose();
        _characterImage?.Dispose();
        _fallbackImage.Dispose();
    }
}
