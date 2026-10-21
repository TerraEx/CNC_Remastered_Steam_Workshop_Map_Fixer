using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace CncRemasterWorkshopFixer;

public partial class AboutWindow : Window
{
    public const string CopyrightNotice = "Copyright © 2026 Terra_Ex. All rights reserved.";
    public const string ReleasesButtonText = "GitHub Releases";
    public const string ReleasesUrl =
        "https://github.com/TerraEx/CNC_Remastered_Steam_Workshop_Map_Fixer/releases";

    public static string DisplayVersion =>
        typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("Application version metadata is missing.");

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {DisplayVersion}";
    }

    private void GitHubReleases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            AppMessageDialog.Show(this, "Open GitHub Releases",
                $"Windows could not open the GitHub Releases page in your default browser.\n\n{exception.Message}",
                AppMessageSeverity.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
