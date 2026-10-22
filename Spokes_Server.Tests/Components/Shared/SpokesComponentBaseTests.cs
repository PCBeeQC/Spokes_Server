using System;
using System.Threading.Tasks;
using Spokes_Server.Components.Shared;
using Xunit;

namespace Spokes_Server.Tests.Components.Shared;

public class SpokesComponentBaseTests
{
    private class TestComponent : SpokesComponentBase
    {
        public void AddTestDisposable(IDisposable d) => _disposables.Add(d);
        public int DisposablesCount => _disposables.Count;
    }

    private class DisposableSpy : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public async Task DisposeAsync_CleansUpRegisteredDisposables()
    {
        var comp = new TestComponent();
        var spy1 = new DisposableSpy();
        var spy2 = new DisposableSpy();

        comp.AddTestDisposable(spy1);
        comp.AddTestDisposable(spy2);
        Assert.Equal(2, comp.DisposablesCount);

        await comp.DisposeAsync();

        Assert.True(spy1.IsDisposed);
        Assert.True(spy2.IsDisposed);
        Assert.Equal(0, comp.DisposablesCount);
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_IsSafe()
    {
        var comp = new TestComponent();
        await comp.DisposeAsync();
        await comp.DisposeAsync();
        Assert.Equal(0, comp.DisposablesCount);
    }
}
