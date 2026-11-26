namespace LocalControlsUi.Services;

public sealed record DocumentView(DocumentSession Session, ControlSnapshot Snapshot, ControlDiagnostics Diagnostics);
