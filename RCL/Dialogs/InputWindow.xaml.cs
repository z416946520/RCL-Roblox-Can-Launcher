using System.Windows;
using System.Windows.Input;

namespace RCL.Dialogs;

public partial class InputWindow : Window
{
    public string First { get; private set; } = "";
    public string Second { get; private set; } = "";

    public InputWindow(string title, string label, string initial, string? hint,
        string? label2 = null, string? initial2 = null, string? hint2 = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        LabelText.Text = label;
        Input.Text = initial;
        HintText.Text = hint ?? "";
        HintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;

        if (label2 is null)
        {
            SecondPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondLabel.Text = label2;
            Input2.Text = initial2 ?? "";
            SecondHint.Text = hint2 ?? "";
            SecondHint.Visibility = string.IsNullOrWhiteSpace(hint2) ? Visibility.Collapsed : Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
            UpdateOk();
        };
        Input.TextChanged += (_, _) => UpdateOk();
    }

    private void UpdateOk() => OkButton.IsEnabled = !string.IsNullOrWhiteSpace(Input.Text);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        First = Input.Text.Trim();
        Second = Input2.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
}
