namespace HobbywingCan.App.Localization;

/// <summary><c>{loc:Tr Key}</c>: a text from the resources that follows the language picker.</summary>
[ContentProperty(nameof(Key))]
[AcceptEmptyServiceProvider]
public sealed class TrExtension : IMarkupExtension<BindingBase>
{
    public string Key { get; set; } = "";

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]", BindingMode.OneWay, source: Localizer.Instance);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
