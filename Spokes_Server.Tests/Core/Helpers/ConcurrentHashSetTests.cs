namespace Spokes_Server.Tests.Core.Helpers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Spokes_Server.Core.Helpers;
using Xunit;

public class ConcurrentHashSetTests
{
    [Fact]
    public void BasicOperations_AddRemoveContains_WorkCorrectly()
    {
        var set = new ConcurrentHashSet<string>();

        Assert.True(set.Add("alpha"));
        Assert.False(set.Add("alpha")); // Duplicate
        Assert.Contains("alpha", set);
        Assert.Single(set);

        Assert.True(set.Remove("alpha"));
        Assert.False(set.Remove("alpha"));
        Assert.Empty(set);
    }

    [Fact]
    public void ReplaceWith_ReconcilesItemsWithoutClearingUnchanged()
    {
        var set = new ConcurrentHashSet<string>(["a", "b", "c"]);

        set.ReplaceWith(["b", "c", "d"]);

        Assert.Equal(3, set.Count);
        Assert.DoesNotContain("a", set);
        Assert.Contains("b", set);
        Assert.Contains("c", set);
        Assert.Contains("d", set);
    }

    [Fact]
    public void SetAlgebra_UnionIntersectExcept_WorkCorrectly()
    {
        var set1 = new ConcurrentHashSet<int>([1, 2, 3]);
        set1.UnionWith([3, 4, 5]);
        Assert.Equal(5, set1.Count);

        set1.IntersectWith([2, 4, 6]);
        Assert.Equal(2, set1.Count);
        Assert.Contains(2, set1);
        Assert.Contains(4, set1);

        set1.ExceptWith([2]);
        Assert.Single(set1);
        Assert.Contains(4, set1);
    }

    [Fact]
    public async Task ConcurrentMutationAndEnumeration_DoesNotThrowInvalidOperationException()
    {
        var set = new ConcurrentHashSet<int>(Enumerable.Range(0, 1000));
        var running = true;

        var mutateTask = Task.Run(() =>
        {
            var r = new Random(42);
            while (running)
            {
                var val = r.Next(0, 2000);
                if (r.Next(2) == 0)
                {
                    set.Add(val);
                }
                else
                {
                    set.Remove(val);
                }
            }
        });

        var enumerateTask = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                // Snapshot iteration or ToList should NEVER throw InvalidOperationException
                var list = set.ToList();
                Assert.NotNull(list);
                foreach (var item in set)
                {
                    _ = item;
                }
            }
        });

        await Task.WhenAny(enumerateTask, Task.Delay(5000));
        running = false;
        await Task.WhenAll(enumerateTask, mutateTask);
    }
}
