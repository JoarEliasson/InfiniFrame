using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

namespace LocalControlsHost;

public sealed class HostErrorBoundaryLogger(ILogger<HostErrorBoundaryLogger> logger) : IErrorBoundaryLogger {
    public ValueTask LogErrorAsync(Exception exception) {
        logger.LogError(exception, "LocalControlsUi.App failed. Check component services, bindings and local JavaScript assets.");
        return ValueTask.CompletedTask;
    }
}
