using Microsoft.Extensions.Configuration;
using Touchline.Configuration;

namespace Touchline.Tests.Unit.Configuration;

/// <summary>
/// A configuration source standing in for the App Configuration store: it can change and signal a reload, as the provider
/// does after a refresh, and it is marked as the store so the start-up fallback knows to leave it out.
/// </summary>
internal sealed class ReloadableSource : IConfigurationSource
{
    public ReloadableProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

internal sealed class ReloadableProvider : ConfigurationProvider, IStoreConfigurationProvider
{
    public new IDictionary<string, string?> Data => base.Data;

    public void Change(string key, string value)
    {
        Data[key] = value;
        OnReload();
    }
}
