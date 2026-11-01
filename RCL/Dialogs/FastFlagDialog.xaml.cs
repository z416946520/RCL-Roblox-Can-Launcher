using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RCL.Core;

namespace RCL.Dialogs;

public partial class FastFlagDialog : Window
{
    public List<(string Name, string Value)> Entries { get; } = new();

    public FastFlagDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelSingle is null)
            return;
        bool single = TabSingle.IsChecked == true;
        PanelSingle.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        PanelJson.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = "";
        if (single)
            NameBox.Focus();
        else
            JsonBox.Focus();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) { if (ErrorText is not null) ErrorText.Text = ""; }

    private void ValueBox_TextChanged(object sender, TextChangedEventArgs e) { if (ErrorText is not null) ErrorText.Text = ""; }

    private void JsonBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (JsonHint is not null)
            JsonHint.Visibility = string.IsNullOrEmpty(JsonBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (ErrorText is not null)
            ErrorText.Text = "";
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 FastFlags JSON",
            Filter = "JSON 文件|*.json|所有文件|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            JsonBox.Text = File.ReadAllText(dialog.FileName);
            TabJson.IsChecked = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "读取文件失败：" + ex.Message;
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                ErrorText.Text = "剪贴板里没有文本。";
                return;
            }
            JsonBox.Text = Clipboard.GetText();
            TabJson.IsChecked = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "读取剪贴板失败：" + ex.Message;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Entries.Clear();
        ErrorText.Text = "";

        if (TabSingle.IsChecked == true)
        {
            var name = NameBox.Text.Trim();
            if (name.Length == 0)
            {
                ErrorText.Text = "请填写 Flag 名称。";
                NameBox.Focus();
                return;
            }
            var error = FastFlags.ValidateValue(ValueBox.Text);
            if (error is not null)
            {
                ErrorText.Text = "Flag 值不合法：" + error;
                ValueBox.Focus();
                return;
            }
            Entries.Add((name, ValueBox.Text.Trim()));
            DialogResult = true;
            return;
        }

        var json = JsonBox.Text.Trim();
        if (json.Length == 0)
        {
            ErrorText.Text = "请粘贴 JSON 对象。";
            JsonBox.Focus();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                ErrorText.Text = "JSON 根节点必须是对象（{ ... }）。";
                return;
            }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                    continue;
                Entries.Add((property.Name.Trim(), property.Value.GetRawText()));
            }
            if (Entries.Count == 0)
            {
                ErrorText.Text = "JSON 对象里没有可导入的 Flag。";
                return;
            }
            DialogResult = true;
        }
        catch (JsonException ex)
        {
            ErrorText.Text = "JSON 格式错误：" + ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }
}
