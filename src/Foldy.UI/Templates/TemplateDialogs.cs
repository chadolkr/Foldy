using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Foldy.UI.Theming;

namespace Foldy.UI.Templates;

/// <summary>
/// 폴더 템플릿·위저드·정리·설명 대화상자 진입점 (호스트용).
/// Foldy.UI는 Foldy.Presentation을 참조하지 않으므로 ViewModel은 object로 받고 바인딩으로만 연결한다.
/// 각 ViewModel의 <c>DialogResult</c>(bool?) 속성이 값을 가지면 창이 닫힌다.
/// </summary>
public static class TemplateDialogs
{
    /// <summary>"템플릿으로 만들기" (TemplateDialogViewModel). 만들었으면 true.</summary>
    public static bool? ShowTemplatePicker(Window? owner, object viewModel) => Show(new TemplatePickerWindow(), owner, viewModel);

    /// <summary>폴더 위저드 (FolderWizardViewModel).</summary>
    public static bool? ShowFolderWizard(Window? owner, object viewModel) => Show(new FolderWizardWindow(), owner, viewModel);

    /// <summary>폴더 정리 (OrganizeViewModel). 정리를 실행했으면 true.</summary>
    public static bool? ShowOrganize(Window? owner, object viewModel) => Show(new OrganizeWindow(), owner, viewModel);

    /// <summary>폴더 설명 편집 (DescriptionEditorViewModel, 먼저 Load() 호출).</summary>
    public static bool? ShowDescriptionEditor(Window? owner, object viewModel) => Show(new DescriptionEditorWindow(), owner, viewModel);

    private static bool? Show(Window window, Window? owner, object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        window.DataContext = viewModel;
        if (owner is not null)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        ThemeManager.Attach(window);
        return window.ShowDialog();
    }
}

/// <summary>ViewModel의 DialogResult 바인딩 → 창 닫기 (MVVM 표준 패턴).</summary>
public static class DialogCloser
{
    public static readonly DependencyProperty ResultProperty = DependencyProperty.RegisterAttached(
        "Result", typeof(bool?), typeof(DialogCloser), new PropertyMetadata(null, OnResultChanged));

    public static bool? GetResult(DependencyObject d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return (bool?)d.GetValue(ResultProperty);
    }

    public static void SetResult(DependencyObject d, bool? value)
    {
        ArgumentNullException.ThrowIfNull(d);
        d.SetValue(ResultProperty, value);
    }

    private static void OnResultChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window w || e.NewValue is not bool result)
        {
            return;
        }

        // ShowDialog로 열린 창만 DialogResult를 받을 수 있다
        try
        {
            w.DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            w.Close();
        }
    }
}

/// <summary>열거형 값 이름 == ConverterParameter → Visible.</summary>
public sealed class EnumVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → Visibility (Invert=true면 반대). null·빈 문자열은 false로 본다.</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool x => x,
            string s => s.Length > 0,
            int i => i > 0,
            null => false,
            _ => true,
        };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
