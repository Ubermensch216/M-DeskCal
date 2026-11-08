using System.Windows;
using System.Windows.Controls;
using DeskCal.Core;
using DeskCal.Data;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace DeskCal.App;

/// <summary>Password prompts for the content lock: unlock, set / change, remove.</summary>
internal static class LockUi
{
    /// <summary>Ask until the right password is typed or the user cancels. A few wrong tries in a row slow the next attempts down.</summary>
    public static bool Unlock(ContentLockStore store)
    {
        int failures = 0;
        var values = Ask("콘텐츠 잠금 해제", "비밀번호를 입력하세요.", ["비밀번호"], v =>
        {
            if (store.Verify(v[0])) return null;
            failures++;
            if (failures >= 3) Thread.Sleep(Math.Min(4000, 1000 * (failures - 2)));   // slows down guessing
            return "비밀번호가 맞지 않습니다.";
        });
        return values is not null;
    }

    /// <summary>Set the first password, or change it (after proving the old one). Returns true when a new password was stored.</summary>
    public static bool SetPassword(ContentLockStore store)
    {
        bool change = store.HasPassword;
        string[] labels = change ? ["현재 비밀번호", "새 비밀번호", "새 비밀번호 확인"] : ["새 비밀번호", "새 비밀번호 확인"];
        var values = Ask(change ? "비밀번호 변경" : "비밀번호 설정",
            $"비밀번호는 {ContentLock.MinLength}자 이상. 잊으면 복구할 수 없습니다. 이 잠금은 화면에 보이는 내용을 가리는 것이며, 데이터 파일을 암호화하지는 않습니다.",
            labels, v =>
            {
                int o = change ? 1 : 0;
                if (change && !store.Verify(v[0])) return "현재 비밀번호가 맞지 않습니다.";
                if (ContentLock.Validate(v[o]) is { } problem) return problem;
                return v[o] == v[o + 1] ? null : "새 비밀번호 확인이 일치하지 않습니다.";
            });
        if (values is null) return false;
        store.SetPassword(values[change ? 1 : 0]);
        return true;
    }

    /// <summary>Remove the password after proving it. Returns true when it was removed.</summary>
    public static bool RemovePassword(ContentLockStore store)
    {
        var values = Ask("비밀번호 해제", "비밀번호를 없애면 누구나 잠금을 풀 수 있습니다. 현재 비밀번호를 입력하세요.", ["현재 비밀번호"],
            v => store.Verify(v[0]) ? null : "비밀번호가 맞지 않습니다.");
        if (values is null) return false;
        store.ClearPassword();
        return true;
    }

    /// <summary>A small modal dialog with one password box per label. <paramref name="check"/> returns an error text to keep the dialog open, or null to accept.</summary>
    private static string[]? Ask(string title, string message, string[] labels, Func<string[], string?> check)
    {
        string[]? result = null;
        var win = Dlg.Create(title, 420, 300);
        win.SizeToContent = SizeToContent.Height; win.ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var boxes = new List<PasswordBox>();
        foreach (var label in labels)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2) });
            var box = new PasswordBox { Padding = new Thickness(5, 4, 5, 4), MaxLength = ContentLock.MaxLength };
            boxes.Add(box); panel.Children.Add(box);
        }
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(error);

        var ok = Dlg.Btn("확인", (_, _) =>
        {
            var values = boxes.Select(b => b.Password).ToArray();
            if (check(values) is { } problem)
            {
                error.Text = problem;
                boxes[^1].Clear(); if (boxes.Count == 1) boxes[0].Focus();
                return;
            }
            result = values;
            win.DialogResult = true;
        }, isDefault: true);
        var cancel = new Button { Content = "취소", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.Loaded += (_, _) => boxes[0].Focus();
        win.ShowDialog();
        return result;
    }
}
