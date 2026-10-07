using VerbaFlow.Core.Audit;

namespace VerbaFlow.Tests.Core;

public class AuditChainTests
{
    private static List<AuditEvent> Chain(int n)
    {
        var list = new List<AuditEvent>();
        var prev = AuditChain.Genesis;
        for (var i = 1; i <= n; i++)
        {
            var e = AuditChain.Build(i, T.Now.AddSeconds(i), Guid.NewGuid(), "author", "speak", Guid.NewGuid(),
                "item.created", AuditChain.Details(("n", i)), prev);
            list.Add(e);
            prev = e.Hash;
        }
        return list;
    }

    [Fact] public void An_intact_chain_verifies() => Assert.True(AuditChain.Verify(Chain(5)).Ok);

    [Fact] public void An_empty_chain_verifies() => Assert.True(AuditChain.Verify([]).Ok);

    [Fact]
    public void Editing_an_entry_is_detected()
    {
        var c = Chain(5);
        c[2] = c[2] with { Details = AuditChain.Details(("n", 999)) };
        var r = AuditChain.Verify(c);
        Assert.False(r.Ok);
        Assert.Equal(3, r.BadSeq);
    }

    [Fact]
    public void Deleting_an_entry_is_detected()
    {
        var c = Chain(5);
        c.RemoveAt(1);
        var r = AuditChain.Verify(c);
        Assert.False(r.Ok);
        Assert.Equal(3, r.BadSeq);
    }

    [Fact]
    public void Rehashing_one_entry_cannot_hide_tampering_because_the_next_link_breaks()
    {
        var c = Chain(4);
        var tampered = c[1] with { Details = "{}" };
        tampered = AuditChain.Build(tampered.Seq, tampered.At, tampered.ActorId, tampered.Capacity, tampered.Product,
            tampered.ItemId, tampered.Type, "{}", tampered.PrevHash);
        c[1] = tampered; // rehashed entry 2, but entry 3 still points at the old hash
        var r = AuditChain.Verify(c);
        Assert.False(r.Ok);
        Assert.Equal(3, r.BadSeq);
    }

    [Fact]
    public void Details_are_stable_regardless_of_key_order() =>
        Assert.Equal(AuditChain.Details(("a", 1), ("b", 2)), AuditChain.Details(("b", 2), ("a", 1)));
}
