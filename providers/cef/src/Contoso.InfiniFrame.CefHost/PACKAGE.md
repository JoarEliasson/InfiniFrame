# Ceffortless

Ceffortless is a .NET 10 host provider for InfiniFrame. The managed adapter launches a native companion from a separately installed Chromium Embedded Framework (CEF) runtime.

Install the matching framework packages and prerelease provider from the same feed. Register the provider on the InfiniFrame application builder before calling `Build()`.

```csharp
using Contoso.InfiniFrame.CefHost;

builder.UseHostProvider(new CefHostProvider(new CefHostOptions {
    PayloadDirectory = installedRuntimeDirectory
}));
```

The directory must contain the companion, CEF, resources and the runtime manifest for your platform. This release uses host protocol 3 and CEF API 15600. See the [source build guide](https://github.com/JoarEliasson/InfiniFrame/blob/integration/cef-host/providers/cef/BUILD.md) and the [sample host](https://github.com/JoarEliasson/InfiniFrame/tree/integration/cef-host/providers/cef/samples/LocalControlsHost).
