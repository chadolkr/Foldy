using System.Windows.Input;

namespace Foldy.App.Services;

/// <summary>
/// 빠른 검색 단축키가 다른 프로그램(또는 입력기)과 겹칠 때의 대화. 조용히 넘어가지 않고 알린 뒤,
/// 비어 있는 키 후보를 보여 주거나 직접 눌러서 정하게 한다. "Foldy 안에서만 쓰기"도 고를 수 있다.
/// </summary>
internal static class HotkeyFlows
{
    /// <summary>고른 결과. <see cref="Gesture"/>가 null이면 바꾸지 않음(그만두기), <see cref="Global"/>이 false면 Foldy 안에서만.</summary>
    public readonly record struct Choice(string? Gesture, bool Global);

    public static string Reason(string gesture) =>
        GlobalHotkey.ConflictsWithInputMethod(gesture)
            ? "중국어·일본어 입력기가 입력 전환 키로 쓰고 있어요"
            : "다른 프로그램이 이미 쓰고 있어요";

    public static async Task<Choice> ChooseAsync(GuideWindow w, IntPtr hwnd, string current)
    {
        w.Say($"빠른 검색은 어느 프로그램을 쓰다가도 {current}를 누르면 열려요. 그런데 이 키는 {Reason(current)}.");
        w.Say("겹치면 둘 중 하나가 안 되니까, 다른 키를 골라 주세요. 나중에 설정 → 단축키에서 언제든 바꿀 수 있어요.");
        while (true)
        {
            var free = GlobalHotkey.Candidates.Where(c => c != current && GlobalHotkey.IsFree(hwnd, c)).Take(3).ToList();
            var choices = free.Select(c => (c, c)).ToList();
            choices.Add(("press", "직접 눌러서 정하기"));
            choices.Add(("local", "Foldy 안에서만 쓰기"));
            var c = await w.AskAsync(null, [.. choices]);
            if (c == GuideWindow.ClosedChoice)
            {
                return new Choice(null, true);
            }

            if (c == "local")
            {
                w.Say($"알겠어요. Foldy 창에서만 {current}로 열게요.");
                return new Choice(current, false);
            }

            if (c != "press")
            {
                w.Say($"좋아요! 이제 어디서든 {c}를 누르면 빠른 검색이 열려요.");
                return new Choice(c, true);
            }

            var pressed = await CaptureAsync(w);
            if (pressed is null)
            {
                return new Choice(null, true);
            }

            if (GlobalHotkey.IsFree(hwnd, pressed))
            {
                w.Say($"좋아요! 이제 어디서든 {pressed}를 누르면 빠른 검색이 열려요.");
                return new Choice(pressed, true);
            }

            w.Say($"{pressed}도 {Reason(pressed)}. 다른 키를 골라 볼까요?");
        }
    }

    /// <summary>Ctrl·Alt·Shift·Win 중 하나 이상과 함께 누른 키를 받는다. 창을 닫으면 null.</summary>
    private static async Task<string?> CaptureAsync(GuideWindow w)
    {
        var hint = w.Say("원하는 키를 함께 눌러 보세요 (예: Ctrl + Alt + K). Ctrl·Alt·Shift·Win 중 하나는 꼭 함께 눌러 주세요.");
        var tcs = new TaskCompletionSource<string?>();
        void OnKey(object s, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Escape)
            {
                return;
            }

            var mods = Keyboard.Modifiers;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            {
                mods |= ModifierKeys.Windows;
            }

            e.Handled = true;
            if (mods == ModifierKeys.None)
            {
                hint.Text = "Ctrl·Alt·Shift·Win 중 하나와 함께 눌러 주세요. (키 하나만으로는 다른 프로그램에서 글을 쓸 때 방해돼요)";
                return;
            }

            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
            tcs.TrySetResult(string.Join("+", parts));
        }

        void OnClosed(object? s, EventArgs e) => tcs.TrySetResult(null);
        w.PreviewKeyDown += OnKey;
        w.Closed += OnClosed;
        w.Busy();
        try
        {
            return await tcs.Task;
        }
        finally
        {
            w.PreviewKeyDown -= OnKey;
            w.Closed -= OnClosed;
        }
    }
}
