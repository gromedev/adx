using System.Management.Automation;
using ADx.Cmdlets.Base;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// 0.4: EffectivePort / IsGlobalCatalog must see a port embedded in -Server. Before this,
/// "-Server dc01:3268" bound the Global Catalog (the native stack honours the embedded port)
/// while IsGlobalCatalog -- keyed solely on -Port -- stayed false, silently re-enabling the
/// cross-domain primaryGroupID over-reporting the GC safeguards exist to prevent.
/// </summary>
public class ADxCmdletEndpointTests
{
    private sealed class Probe : ADxCmdletBase
    {
        public int ResolvedPort => EffectivePort;
        public bool Gc => IsGlobalCatalog;
    }

    [Theory]
    [InlineData("dc01.corp.com", 0, false, 389)]
    [InlineData("dc01.corp.com", 0, true, 636)]
    [InlineData("dc01.corp.com", 3268, false, 3268)]
    [InlineData("dc01.corp.com:3268", 0, false, 3268)]
    [InlineData("dc01.corp.com:636", 0, false, 636)]
    [InlineData("[::1]:3269", 0, false, 3269)]
    // Explicit -Port outranks the embedded spelling numerically; the conflicting combination
    // itself is rejected by endpoint validation before connecting.
    [InlineData("dc01.corp.com:3268", 389, false, 389)]
    public void EffectivePort_SeesTheEmbeddedServerPort(
        string server, int port, bool useSsl, int expected)
    {
        var probe = new Probe { Server = server, Port = port, UseSsl = new SwitchParameter(useSsl) };
        Assert.Equal(expected, probe.ResolvedPort);
    }

    [Theory]
    [InlineData("dc01.corp.com:3268", 0, true)]
    [InlineData("dc01.corp.com:3269", 0, true)]
    [InlineData("dc01.corp.com", 3268, true)]
    [InlineData("dc01.corp.com:389", 0, false)]
    [InlineData("dc01.corp.com:636", 0, false)]
    [InlineData("dc01.corp.com", 0, false)]
    public void IsGlobalCatalog_SeesTheEmbeddedServerPort(string server, int port, bool expected)
    {
        var probe = new Probe { Server = server, Port = port };
        Assert.Equal(expected, probe.Gc);
    }
}
