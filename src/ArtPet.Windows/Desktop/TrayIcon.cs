using System;
using System.Drawing;
using System.Windows.Forms;

namespace ArtPet.Windows.Desktop;

internal sealed class TrayIcon : IDisposable
{
    private readonly Icon _image;
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _icon;

    public TrayIcon(PetWindow pet, Action exit, Action? openWardrobe)
    {
        var iconUri = new Uri("/ArtPet.Windows;component/Media/Sprout.ico", UriKind.Relative);
        using var imageStream = System.Windows.Application.GetResourceStream(iconUri)!.Stream;
        _image = new Icon(imageStream, SystemInformation.SmallIconSize);

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

        _menu.Items.Add(visibilityItem);
        if (openWardrobe is not null)
        {
            var wardrobeItem = new ToolStripMenuItem("衣櫃…");
            wardrobeItem.Click += (_, _) => openWardrobe();
            _menu.Items.Add(wardrobeItem);
        }

        _menu.Items.AddRange([resetItem, topmostItem, new ToolStripSeparator(), exitItem]);
        _menu.Opening += (_, _) =>
        {
            visibilityItem.Text = pet.IsVisible ? "隱藏小人" : "顯示小人";
            topmostItem.Checked = pet.Topmost;
        };

        _icon = new NotifyIcon { Text = "Art Pet", Icon = _image, ContextMenuStrip = _menu, Visible = true };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                pet.ShowPet();
            }
        };
    }

    public void Dispose()
    {
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }
}
