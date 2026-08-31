using Zashboard.Core.Clash;

namespace Zashboard.App.Services;

public sealed partial class AppSessionCoordinator
{
    internal void SetRuleCatalogForTest(RuleCatalog catalog) => RuleCatalog = catalog;

    internal void AppendLogsForTest(
        IReadOnlyList<ClashLogMessage> messages,
        DateTimeOffset receivedAt,
        long droppedBeforeDisplay = 0) =>
        AppendLogs(messages, receivedAt, droppedBeforeDisplay);
}
