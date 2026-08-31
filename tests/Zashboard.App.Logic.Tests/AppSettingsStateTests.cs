using Microsoft.VisualStudio.TestTools.UnitTesting;
using Zashboard.App.Services;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class AppSettingsStateTests
{
    [TestMethod]
    public void ApplyThemeRaisesTheThemePropertyName()
    {
        AppSettingsState settings = new(storage: null);
        List<string?> propertyNames = [];
        settings.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        settings.Apply("theme", "dark");

        CollectionAssert.AreEqual(
            new[] { nameof(AppSettingsState.Theme) },
            propertyNames);
    }

    [TestMethod]
    public void ApplyMicaBackdropRaisesTheMicaBackdropPropertyName()
    {
        AppSettingsState settings = new(storage: null);
        List<string?> propertyNames = [];
        settings.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        settings.Apply("micaBackdrop", false);

        CollectionAssert.AreEqual(
            new[] { nameof(AppSettingsState.MicaBackdrop) },
            propertyNames);
    }

    [TestMethod]
    [DataRow("startWithWindows", true, nameof(AppSettingsState.StartWithWindows))]
    [DataRow("closeBehavior", "exit", nameof(AppSettingsState.CloseBehavior))]
    [DataRow("logBufferSize", 10_000, nameof(AppSettingsState.LogBufferSize))]
    [DataRow("delayTestUrl", "https://example.com/generate_204", nameof(AppSettingsState.DelayTestUrl))]
    [DataRow("delayTimeoutMilliseconds", 9_000, nameof(AppSettingsState.DelayTimeoutMilliseconds))]
    [DataRow("delayTimeoutSeconds", 9, nameof(AppSettingsState.DelayTimeoutMilliseconds))]
    public void ApplyOtherSettingsRaisesTheExpectedPropertyName(
        string key,
        object value,
        string expectedPropertyName)
    {
        AppSettingsState settings = new(storage: null);
        List<string?> propertyNames = [];
        settings.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        settings.Apply(key, value);

        CollectionAssert.AreEqual(new[] { expectedPropertyName }, propertyNames);
    }
}
