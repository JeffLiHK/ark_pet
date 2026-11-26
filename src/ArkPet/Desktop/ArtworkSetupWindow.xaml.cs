using System.Windows;

namespace ArtPet.Windows.Desktop;

internal partial class ArtworkSetupWindow : Window
{
    internal ArtworkSetupWindow()
    {
        InitializeComponent();
    }

    internal void ShowModelBuild(string message)
    {
        Heading.Text = "正在建置初始模型";
        ShowProgress(message);
    }

    internal void ShowProgress(string message) => Status.Text = message;

    private void SkipSetup(object sender, RoutedEventArgs e) => Close();
}
