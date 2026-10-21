using System.Windows;

namespace CncRemasterWorkshopFixer;

public partial class ModifiedLocalConfirmationWindow : Window
{
    public ModifiedLocalConfirmationWindow(IEnumerable<string> titles)
    {
        InitializeComponent();
        DataContext = titles.ToList();
    }

    private void Repair_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
