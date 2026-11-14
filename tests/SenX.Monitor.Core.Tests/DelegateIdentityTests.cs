using System;
using SenX.Monitor.Core;
using Xunit;

namespace SenX.Monitor.Core.Tests;

public class DelegateIdentityTests
{
    private sealed class Target
    {
        public int Calls;

        public void First() => Calls++;

        public void Second() => Calls += 2;
    }

    private class Base
    {
        public virtual void Tick()
        {
        }
    }

    private sealed class Derived : Base
    {
        public override void Tick()
        {
        }
    }

    private static void StaticMethod()
    {
    }

    private static void OtherStaticMethod()
    {
    }

    public DelegateIdentityTests() => TestSetup.Reset();

    [Fact]
    public void Is_available_on_this_runtime() => Assert.True(DelegateIdentity.Available);

    [Fact]
    public void One_method_gives_one_key_across_instances()
    {
        var a = new Target();
        var b = new Target();

        Assert.Equal(DelegateIdentity.Key((Action)a.First), DelegateIdentity.Key((Action)b.First));
    }

    [Fact]
    public void Different_methods_give_different_keys()
    {
        var target = new Target();

        Assert.NotEqual(DelegateIdentity.Key((Action)target.First), DelegateIdentity.Key((Action)target.Second));
    }

    [Fact]
    public void Static_methods_are_told_apart()
    {
        Assert.NotEqual(DelegateIdentity.Key((Action)StaticMethod), DelegateIdentity.Key((Action)OtherStaticMethod));
        Assert.Equal(DelegateIdentity.Key((Action)StaticMethod), DelegateIdentity.Key((Action)StaticMethod));
    }

    [Fact]
    public void Virtual_methods_resolve_to_the_override()
    {
        Action viaBase = new Base().Tick;
        Action viaDerived = new Derived().Tick;

        Assert.NotEqual(DelegateIdentity.Key(viaBase), DelegateIdentity.Key(viaDerived));
    }

    [Fact]
    public void Keys_are_never_zero()
    {
        var target = new Target();

        Assert.NotEqual(0L, DelegateIdentity.Key((Action)target.First));
        Assert.NotEqual(0L, DelegateIdentity.Key((Action)StaticMethod));
    }

    [Fact]
    public void Keys_stay_stable_after_the_method_has_been_hot()
    {
        // On .NET 10 tiered compilation replaces hot code, and the key must survive that.
        var target = new Target();
        Action callback = target.First;
        long before = DelegateIdentity.Key(callback);

        for (int round = 0; round < 40; round++)
        {
            for (int i = 0; i < 100_000; i++)
                callback();

            System.Threading.Thread.Sleep(5);
        }

        Assert.Equal(before, DelegateIdentity.Key(callback));
        Assert.Equal(before, DelegateIdentity.Key((Action)target.First));
    }
}
