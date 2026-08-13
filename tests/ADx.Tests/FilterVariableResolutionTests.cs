using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Cmdlets.Filter;
using ADx.Engine.Filter;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// 0.4: -Filter variable resolution against a REAL SessionState, hosted in-process. The two
/// invariants in tension: a typo'd variable must stay a loud error (never silently $null),
/// AND provider-drive paths -- $env:COMPUTERNAME is the RSAT-documented idiom -- must
/// resolve. PSVariable.Get alone delivers the first and breaks the second; GetValue alone
/// the reverse. The sentinel fallback delivers both.
/// </summary>
public class FilterVariableResolutionTests : IDisposable
{
    private readonly PowerShell _ps;
    private readonly SessionState _session;

    public FilterVariableResolutionTests()
    {
        _ps = PowerShell.Create();
        _ps.AddScript(
            "Set-Variable -Name adxTestVar -Value 42\n" +
            "Set-Variable -Name adxDefinedNull -Value $null\n" +
            "Set-Item env:ADX_TEST_ENV -Value 'dc01'\n" +
            "$ExecutionContext.SessionState");
        _session = (SessionState)_ps.Invoke()[^1].BaseObject;
    }

    public void Dispose() => _ps.Dispose();

    [Fact]
    public void PlainVariables_ResolveWithValue()
    {
        Assert.Equal((true, (object?)42), ADxObjectCmdletBase.ResolveFilterVariable(_session, "adxTestVar"));
    }

    [Fact]
    public void DefinedAsNull_IsFound_NotUndefined()
    {
        // The distinction Get preserves and GetValue destroys: silently treating a typo as
        // $null is the failure the loud error exists for, so "defined as $null" must stay
        // distinguishable from "not defined".
        Assert.Equal((true, (object?)null), ADxObjectCmdletBase.ResolveFilterVariable(_session, "adxDefinedNull"));
    }

    [Fact]
    public void EnvironmentDrive_Resolves()
    {
        var (found, value) = ADxObjectCmdletBase.ResolveFilterVariable(_session, "env:ADX_TEST_ENV");
        Assert.True(found);
        Assert.Equal("dc01", value);
    }

    [Fact]
    public void UndefinedPlainAndUndefinedDrivePaths_AreNotFound()
    {
        Assert.False(ADxObjectCmdletBase.ResolveFilterVariable(_session, "adxNoSuchVariable").Found);
        Assert.False(ADxObjectCmdletBase.ResolveFilterVariable(_session, "env:ADX_NO_SUCH_ENV_XYZ").Found);
        // A nonexistent drive returns the GetValue default rather than throwing -- no new
        // exception path leaks out of resolution.
        Assert.False(ADxObjectCmdletBase.ResolveFilterVariable(_session, "nosuchdrive:xyz").Found);
    }

    // ---- end to end through the translator: the drive-qualified UserPath flow ----

    private string T(string filter) =>
        AdFilterEmitter.Emit(AdFilterTranslator.Translate(
            filter, name => ADxObjectCmdletBase.ResolveFilterVariable(_session, name))!);

    [Fact]
    public void EnvVariable_InFilter_Emits()
    {
        Assert.Equal("(name=dc01)", T("Name -eq $env:ADX_TEST_ENV"));
    }

    [Fact]
    public void BracedEnvVariable_InFilter_Emits()
    {
        Assert.Equal("(name=dc01)", T("Name -eq ${env:ADX_TEST_ENV}"));
    }

    [Fact]
    public void EnvVariable_InExpandableString_Emits()
    {
        Assert.Equal("(name=*dc01*)", T("Name -like \"*$env:ADX_TEST_ENV*\""));
    }

    [Fact]
    public void UndefinedEnvVariable_InFilter_StaysALoudError()
    {
        var ex = Assert.Throws<AdFilterTranslationException>(
            () => T("Name -eq $env:ADX_NO_SUCH_ENV_XYZ"));
        Assert.Contains("not defined", ex.Message);
    }
}
