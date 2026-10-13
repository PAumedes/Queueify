using Microsoft.UI.Xaml.Markup;
using Queueify.Services;

namespace Queueify;

/// <summary>A translated text in XAML: <c>Text="{l:Tr Key=nav.queue}"</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Key);
}
