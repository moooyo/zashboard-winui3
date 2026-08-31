using Zashboard.App.ViewModels;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class SettingsFormPolicyTests
{
    [TestMethod]
    public void ListenerDraftRejectsSameSessionRefreshWhileDirty()
    {
        ListenerSettingsDraftState state = new();

        Assert.IsTrue(state.ShouldSynchronize(7));
        state.MarkEdited();

        Assert.IsFalse(state.ShouldSynchronize(7));
        Assert.IsTrue(state.IsDirty);
    }

    [TestMethod]
    public void ListenerDraftAcceptsNewSessionAndClearsDirtyState()
    {
        ListenerSettingsDraftState state = new();
        _ = state.ShouldSynchronize(7);
        state.MarkEdited();

        Assert.IsTrue(state.ShouldSynchronize(8));
        Assert.IsFalse(state.IsDirty);
    }

    [TestMethod]
    public void ListenerDraftCanSynchronizeAfterSuccessfulSave()
    {
        ListenerSettingsDraftState state = new();
        _ = state.ShouldSynchronize(7);
        state.MarkEdited();

        state.MarkSaved();

        Assert.IsTrue(state.ShouldSynchronize(7));
        Assert.IsFalse(state.IsDirty);
    }

    [TestMethod]
    public void ListenerDraftRestoresValuesForTheSameSession()
    {
        ListenerSettingsDraftState state = new();
        ListenerSettingsDraft authoritative = new(7890, 7891, 7892, 7893, 7894, false);
        ListenerSettingsDraft edited = new(8080, 1080, double.NaN, 0, 8888, true);

        Assert.AreEqual(authoritative, state.Resolve(7, authoritative));
        state.MarkEdited(7, edited);

        Assert.AreEqual(edited, state.Resolve(7, authoritative));
        Assert.IsTrue(state.IsDirty);
    }

    [TestMethod]
    public void ListenerDraftUsesAuthoritativeValuesForANewSession()
    {
        ListenerSettingsDraftState state = new();
        ListenerSettingsDraft original = new(7890, 7891, 7892, 7893, 7894, false);
        ListenerSettingsDraft edited = new(8080, 1080, 0, 0, 8888, true);
        ListenerSettingsDraft next = new(9090, 1090, 0, 0, 9999, false);
        _ = state.Resolve(7, original);
        state.MarkEdited(7, edited);

        Assert.AreEqual(next, state.Resolve(8, next));
        Assert.IsFalse(state.IsDirty);
    }

    [TestMethod]
    public void ListenerPortValidationAcceptsOnlyWholeNumbersInRange()
    {
        Assert.IsTrue(SettingsFormPolicy.IsValidListenerPort(0));
        Assert.IsTrue(SettingsFormPolicy.IsValidListenerPort(65_535));
        Assert.IsFalse(SettingsFormPolicy.IsValidListenerPort(double.NaN));
        Assert.IsFalse(SettingsFormPolicy.IsValidListenerPort(-1));
        Assert.IsFalse(SettingsFormPolicy.IsValidListenerPort(65_536));
        Assert.IsFalse(SettingsFormPolicy.IsValidListenerPort(80.5));
    }

    [TestMethod]
    public void DelayTestUrlValidationRequiresSafeAbsoluteHttpUrl()
    {
        Assert.IsTrue(SettingsFormPolicy.IsValidDelayTestUrl("https://example.com/check"));
        Assert.IsTrue(SettingsFormPolicy.IsValidDelayTestUrl(" http://127.0.0.1:8080/ping "));
        Assert.IsFalse(SettingsFormPolicy.IsValidDelayTestUrl(string.Empty));
        Assert.IsFalse(SettingsFormPolicy.IsValidDelayTestUrl("example.com/check"));
        Assert.IsFalse(SettingsFormPolicy.IsValidDelayTestUrl("ftp://example.com/check"));
        Assert.IsFalse(SettingsFormPolicy.IsValidDelayTestUrl("https://user@example.com/check"));
    }

    [TestMethod]
    public void ListenerStatusExplainsEveryDisabledState()
    {
        StringAssert.Contains(
            SettingsFormPolicy.DescribeListenerSettings(false, false, false),
            "online controller");
        StringAssert.Contains(
            SettingsFormPolicy.DescribeListenerSettings(true, false, false),
            "configuration is loaded");
        StringAssert.Contains(
            SettingsFormPolicy.DescribeListenerSettings(true, true, false),
            "does not support");
        StringAssert.Contains(
            SettingsFormPolicy.DescribeListenerSettings(true, true, true),
            "ready to edit");
    }
}
