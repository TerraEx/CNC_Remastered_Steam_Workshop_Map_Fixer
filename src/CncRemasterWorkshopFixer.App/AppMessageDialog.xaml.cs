using System.Windows;

namespace CncRemasterWorkshopFixer;

public enum AppMessageSeverity
{
    Information,
    Warning,
    Error
}

public partial class AppMessageDialog : Window
{
    private AppMessageDialog(string title, string message, AppMessageSeverity severity)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        SeverityIndicator.Text = severity switch
        {
            AppMessageSeverity.Warning => "!",
            AppMessageSeverity.Error => "×",
            _ => "i"
        };
        Loaded += (_, _) => OkButton.Focus();
    }

    public static void Show(Window? owner, string title, string message,
        AppMessageSeverity severity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);

        var dialog = new AppMessageDialog(title, message, severity)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        dialog.ShowDialog();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
