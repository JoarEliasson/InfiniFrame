namespace LocalControlsUi.Services;

/// <summary>Demonstration credentials only. No authentication or device access is performed.</summary>
public sealed class FakeLoginService
{
    public async Task<bool> AcceptAsync(string userName, string password, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        return userName == "operator" && password == "demo";
    }
}
