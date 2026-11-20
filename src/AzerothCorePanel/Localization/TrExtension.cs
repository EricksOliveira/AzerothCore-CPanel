using System.Windows.Data;
using System.Windows.Markup;

namespace AzerothCorePanel.Localization;

/// <summary>
/// <c>{l:Tr Key}</c> in XAML: a one-way binding to the translated text, refreshed when the language changes. It works
/// on any dependency property, including DataGrid column headers, because the binding has an explicit source.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = L.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
