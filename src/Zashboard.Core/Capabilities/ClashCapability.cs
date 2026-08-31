namespace Zashboard.Core.Capabilities;

public enum ClashCapability
{
    ConfigurationPatch,
    ConfigurationReload,
    ConfigurationUpdate,
    GeoDataUpdate,
    CoreUpgrade,
    CoreRestart,
    DashboardUpgrade,
    SettingsStorage,
    IndependentLatencyHistory,
    ProviderProxyHealthCheck,
    ProviderProxyUpdate,
    RuleDisableByIndex,
    RuleDisableByIdentifier,
    SmartWeights,
    SmartWeightReset,
    SmartConnectionBlock,
    RuntimeStatistics,
    TraceLogLevel,
    SilentLogLevel,
}

public enum CapabilitySupport
{
    Unknown,
    Supported,
    Unsupported,
}

public enum CapabilityEvidenceKind
{
    None,
    SuccessfulCall,
    BadRequest,
    EndpointNotFound,
    MethodNotAllowed,
    CoreHint,
    ResponseData,
    UserOverride,
}
