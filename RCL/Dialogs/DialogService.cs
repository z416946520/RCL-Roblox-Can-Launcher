using System.Windows;
using Microsoft.Win32;

namespace RCL.Dialogs;

public static class DialogService
{
    public static void Info(string message, string title = "提示") =>
        Show(title, message, "确定", null);

    public static void Error(string message, string title = "出错了") =>
        Show(title, message, "确定", null);

    public static bool Confirm(string message, string title = "确认", string okText = "确定", string cancelText = "取消") =>
        Show(title, message, okText, cancelText);

    public static string? Prompt(string title, string label, string initial = "", string? hint = null)
    {
        var dialog = new InputWindow(title, label, initial, hint)
        {
            Owner = OwnerWindow
        };
        return dialog.ShowDialog() == true ? dialog.First : null;
    }

    public static (string First, string Second)? PromptTwo(string title, string label, string initial,
        string? hint, string label2, string? initial2 = null, string? hint2 = null)
    {
        var dialog = new InputWindow(title, label, initial, hint, label2, initial2, hint2)
        {
            Owner = OwnerWindow
        };
        return dialog.ShowDialog() == true ? (dialog.First, dialog.Second) : null;
    }

    public static string? PickFile(string filter, string? title = null)
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title ?? "选择文件" };
        return dialog.ShowDialog(OwnerWindow) == true ? dialog.FileName : null;
    }

    public static string? PickFolder(string? title = null)
    {
        var dialog = new OpenFolderDialog { Title = title ?? "选择文件夹" };
        return dialog.ShowDialog(OwnerWindow) == true ? dialog.FolderName : null;
    }

    public static string? PickSaveFile(string filter, string fileName, string? title = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = fileName,
            Title = title ?? "保存文件"
        };
        return dialog.ShowDialog(OwnerWindow) == true ? dialog.FileName : null;
    }

    private static bool Show(string title, string message, string okText, string? cancelText)
    {
        var window = new MsgBoxWindow(title, message, okText, cancelText)
        {
            Owner = OwnerWindow
        };
        window.ShowDialog();
        return window.Result;
    }

    private static Window? OwnerWindow
    {
        get
        {
            var window = Application.Current?.MainWindow;
            return window is { IsVisible: true } ? window : null;
        }
    }
}
