using System.Windows.Markup;
using Foldy.Core.Localization;

namespace Foldy.UI.Localization;

/// <summary>
/// XAML에서 다국어 글: <c>Text="{loc:T side.find}"</c>. 언어는 Foldy를 켤 때 정해진다(바꾸면 다시 켤 때 적용).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
