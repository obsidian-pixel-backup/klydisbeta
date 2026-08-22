namespace Klydis.Core.Web.Models;

/// <summary>
/// Structured recovery actions recommended to the agent when a web operation encounters a failure.
/// </summary>
public enum WebRecoveryAction
{
    Retry,
    RetryWithBackoff,
    UseBrowser,
    UseHttp,
    UseDifferentProvider,
    UseDifferentUrl,
    NarrowQuery,
    Authenticate,
    Stop
}
