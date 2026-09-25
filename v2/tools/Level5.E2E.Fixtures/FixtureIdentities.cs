namespace Level5.E2E.Fixtures;

/// <summary>
/// The four deterministic E2E identities every scenario is built from (spec: baseline/friends/
/// pending-friend/challenge-invited/series-*/challenge-*). Real domain constructors always mint a
/// fresh <c>AccountId</c>/<c>PlayerId</c> (<see cref="Level5.Domain.Identity.Account.Register"/>
/// never accepts a caller-supplied id) - reusing them here (rather than bypassing them to force a
/// literal GUID) is the whole point of fixture tooling that seeds through real persistence paths.
/// What is actually fixed and stable across every reset+reseed cycle is the username/tag/display
/// name below - an E2E test looks a fixture player up by tag (exactly how a real client would,
/// e.g. <c>GET /players/by-tag/{tag}</c>), not by a hardcoded id literal, so this is deterministic
/// in the sense that matters to a client.
/// </summary>
public sealed record FixtureIdentity(string Label, string Username, string DisplayName, string Tag);

public static class FixtureIdentities
{
    // Username allows only letters/digits/underscore/period; PlayerTag requires
    // "<2-20 letters/digits/underscore>#<3-6 digits>" - neither accepts a hyphen, so these use
    // underscore-separated names rather than the hyphenated form the spec sketches.
    public static readonly FixtureIdentity Patrick = new("Patrick", "e2e_patrick", "Patrick E2E", "E2E_PATRICK#0001");
    public static readonly FixtureIdentity Alice = new("Alice", "e2e_alice", "Alice E2E", "E2E_ALICE#0002");
    public static readonly FixtureIdentity Bob = new("Bob", "e2e_bob", "Bob E2E", "E2E_BOB#0003");
    public static readonly FixtureIdentity Carol = new("Carol", "e2e_carol", "Carol E2E", "E2E_CAROL#0004");

    public static readonly IReadOnlyList<FixtureIdentity> All = [Patrick, Alice, Bob, Carol];

    /// <summary>
    /// A fixed, clearly-test-only password shared by every fixture account. Hashed by the real
    /// <c>AspNetPasswordHasher</c> (never a shortcut/plaintext comparison), so an E2E test's login
    /// flow exercises the exact same verification path production traffic does.
    /// </summary>
    public const string Password = "E2E-Fixture-Password-Not-For-Production-1!";
}
