using System.Windows;
using System.Windows.Input;

namespace RCL.Dialogs;

public partial class MsgBoxWindow : Window
{
    public bool Result { get; private set; }

    public MsgBoxWindow(string title, string message, string okText, string? cancelText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        if (cancelText is null)
            CancelButton.Visibility = Visibility.Collapsed;
        else
            CancelButton.Content = cancelText;
        Loaded += (_, _) => OkButton.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
}
