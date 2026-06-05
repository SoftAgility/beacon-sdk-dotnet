// Covers (session-end-durability PRD — Parts A/B; "Test plan (SDK unit tests)"):
//   Case 1  — Dispose() with an active session writes a durable, self-contained pending-end
//             record with ended_at stamped at write time.
//   Case 2  — Immediate-send SUCCESS deletes the record; FAILURE/TIMEOUT leaves it persisted.
//   Case 3  — Next-launch init delivers a persisted record as end_reason="sdk_recovery" with the
//             ORIGINAL ended_at (not now) and the full start context.
//   Case 4  — A 404 (session_not_found) on delivery is non-terminal: the record is removed locally
//             and the queue does not stall on subsequent records.
//   Case 5  — EndSessionAsync() and EndSession()+FlushAsync() drain a pending end (send happens).
//   Case 6  — ShutdownFlushTimeout = Zero skips the blocking send but still persists.
//   Case 7  — Multiple pending ends: StartSession → StartSession → Dispose persists TWO records
//             (queue, not single-row); both delivered on next launch. Each record carries the
//             actor that owned THAT session (self-contained start context).
//   Case 8  — Opt-out / Enabled=false / no active session → no record written.
//   Case 9  — Opt-out AFTER a record exists → the pending end is purged, not delivered;
//             Reset() clears the store.
//   Case 10 — The pending-end store is SEPARATE from the event DiskQueue (a session-end never
//             lands in the event batch).
//   RG-1    — write-before-clear: the record is persisted even though _sessionId is observed null
//             afterward.
//   RG-2    — the recovery payload field names match the server contract
//             (product / product_version / actor_id / started_at / account_id / license_id).
//
// HTTP-controllable cases drive a real BeaconHttpClient through a local HttpListener
// (RecordingSessionEndServer) — the same HttpListener approach as HttpClientTests; the SDK's
// BeaconHttpClient takes no injectable handler, so no new fake is invented (DP-16 reuse).
// "Offline" persistence cases point the tracker at a non-routable URL so the best-effort live
// send fails and the durable record stays on disk for inspection.
// Each tracker uses a unique Product so its LocalApplicationData SQLite store/disk-queue is
// isolated; DiskQueueLocationHelper.CleanUp removes the per-product directory in teardown.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SoftAgility.Beacon.Internal;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

[Collection("HttpListener")]
public sealed class SessionEndDurabilityTests : IDisposable
{
    // RFC 5737 TEST-NET-1 — guaranteed non-routable, so any best-effort live send fails and the
    // durable record stays persisted (mirrors DisposeTests' use of 192.0.2.x).
    private const string NonRoutableUrl = "http://192.0.2.1";

    private readonly List<string> _products = [];

    public void Dispose()
    {
        foreach (var product in _products)
            DiskQueueLocationHelper.CleanUp(product);
    }

    // Creates a tracker with a unique Product (isolated store) pointed at the given base URL.
    // High flush interval so the background timer does not race the assertions.
    private BeaconTracker CreateTracker(
        string baseUrl,
        bool enabled = true,
        TimeSpan? shutdownFlushTimeout = null)
    {
        var options = BuildOptions(NewProduct(), baseUrl, enabled, shutdownFlushTimeout);
        return new BeaconTracker(options);
    }

    private string NewProduct()
    {
        var product = $"DurTest_{Guid.NewGuid():N}";
        _products.Add(product);
        return product;
    }

    private static BeaconOptions BuildOptions(
        string product,
        string baseUrl,
        bool enabled = true,
        TimeSpan? shutdownFlushTimeout = null)
    {
        var options = new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = baseUrl,
            Product = product,
            ProductVersion = "1.2.3",
            Enabled = enabled,
            MaxBatchSize = 1000,
            FlushIntervalSeconds = 3600
        };
        if (shutdownFlushTimeout is { } t)
            options.ShutdownFlushTimeout = t;
        return options;
    }

    // ── Case 1: Dispose persists a self-contained durable record ─────────────────────────────

    // Case 1: Dispose() with an active session writes one durable pending-end record carrying the
    // full self-contained start context, with ended_at stamped ~now (write time).
    [Fact]
    public async Task Dispose_WithActiveSession_PersistsSelfContainedPendingEnd()
    {
        // Arrange — Zero timeout so dispose persists but never sends (isolates the durable write).
        using var server = new RecordingSessionEndServer();
        var tracker = CreateTracker(server.BaseUrl, shutdownFlushTimeout: TimeSpan.Zero);
        tracker.SetAccount("acct_acme");
        tracker.SetLicense("lic_42");
        tracker.StartSession("user-1");
        var sessionId = TrackerTestHelper.GetSessionId(tracker);
        var before = DateTimeOffset.UtcNow;

        // Act
        await tracker.DisposeAsync();
        var after = DateTimeOffset.UtcNow;

        // Assert — exactly one record with the self-contained start context.
        var ends = ReopenStore(ProductOf(tracker));
        ends.Should().HaveCount(1);
        var record = ends[0];
        record.SessionId.Should().Be(sessionId);
        record.ActorId.Should().Be("user-1");
        record.Product.Should().StartWith("DurTest_");
        record.ProductVersion.Should().Be("1.2.3");
        record.AccountId.Should().Be("acct_acme");
        record.LicenseId.Should().Be("lic_42");
        record.StartedAt.Should().NotBeNullOrEmpty();

        // ended_at is stamped at write time (between before/after the dispose), NOT delivery time.
        var endedAt = DateTimeOffset.Parse(record.EndedAt);
        endedAt.Should().BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(after.AddSeconds(1));
    }

    // ── Case 2: immediate-send success deletes; failure/timeout leaves persisted ─────────────

    // Case 2 (success): a successful best-effort dispose send deletes the durable record.
    [Fact]
    public async Task Dispose_ImmediateSendSucceeds_DeletesRecord()
    {
        // Arrange — server returns 200 promptly, generous timeout so the send completes.
        using var server = new RecordingSessionEndServer { SessionEndStatusCode = 200 };
        var tracker = CreateTracker(server.BaseUrl, shutdownFlushTimeout: TimeSpan.FromSeconds(10));
        tracker.StartSession("user-1");

        // Act
        await tracker.DisposeAsync();

        // Assert — the end was delivered and the record removed.
        (await server.WaitForSessionEndRequestsAsync(1, TimeSpan.FromSeconds(5))).Should().BeTrue();
        ReopenStore(ProductOf(tracker)).Should().BeEmpty("a successful immediate send deletes the durable record");
    }

    // Case 2 (timeout): when the send exceeds ShutdownFlushTimeout, the record stays persisted.
    [Fact]
    public async Task Dispose_ImmediateSendTimesOut_LeavesRecordPersisted()
    {
        // Arrange — server delays its response well past the short shutdown timeout.
        using var server = new RecordingSessionEndServer
        {
            SessionEndStatusCode = 200,
            ResponseDelay = TimeSpan.FromSeconds(5)
        };
        var tracker = CreateTracker(server.BaseUrl, shutdownFlushTimeout: TimeSpan.FromMilliseconds(200));
        tracker.StartSession("user-1");

        // Act
        await tracker.DisposeAsync();

        // Assert — the timed-out send did NOT delete the record; it persists for next-launch recovery.
        ReopenStore(ProductOf(tracker)).Should().HaveCount(1,
            "a send that exceeds ShutdownFlushTimeout must leave the durable record");
    }

    // ── Case 3: next-launch delivers sdk_recovery with original ended_at + full context ──────

    // Case 3: a record persisted by one tracker is delivered by the next launch (same Product) as
    // end_reason="sdk_recovery" carrying the ORIGINAL ended_at and the full start context.
    [Fact]
    public async Task NextLaunch_DeliversPersistedRecord_AsSdkRecovery_WithOriginalEndedAt()
    {
        // Arrange — launch 1 persists (Zero timeout, no send); a second tracker on the SAME product
        // drains it on FlushAsync.
        using var server = new RecordingSessionEndServer { SessionEndStatusCode = 200 };
        var product = NewProduct();

        var launch1 = new BeaconTracker(BuildOptions(product, server.BaseUrl, shutdownFlushTimeout: TimeSpan.Zero));
        launch1.SetAccount("acct_x");
        launch1.StartSession("user-7");
        await launch1.DisposeAsync();

        var originalEndedAt = ReopenStore(product).Single().EndedAt;
        originalEndedAt.Should().NotBeNullOrEmpty();

        // Act — launch 2 drains the pending end via FlushAsync.
        var launch2 = new BeaconTracker(BuildOptions(product, server.BaseUrl));
        await launch2.FlushAsync();
        (await server.WaitForSessionEndRequestsAsync(1, TimeSpan.FromSeconds(5))).Should().BeTrue();

        // Assert — delivered as sdk_recovery with the original ended_at and full start context.
        var end = server.SessionEndRequests.Single();
        using var doc = JsonDocument.Parse(end.Body);
        var root = doc.RootElement;
        root.GetProperty("end_reason").GetString().Should().Be("sdk_recovery");
        root.GetProperty("ended_at").GetString().Should().Be(originalEndedAt, "recovery uses the ORIGINAL ended_at, not now()");
        root.GetProperty("actor_id").GetString().Should().Be("user-7");
        root.GetProperty("product_version").GetString().Should().Be("1.2.3");
        root.GetProperty("account_id").GetString().Should().Be("acct_x");
        root.TryGetProperty("started_at", out _).Should().BeTrue();

        await launch2.DisposeAsync();
        // Store should now be empty (delivered + deleted).
        ReopenStore(product).Should().BeEmpty();
    }

    // ── Case 4: 404 on delivery is non-terminal; queue does not stall ────────────────────────

    // Case 4: when a recovery delivery returns 404 (session_not_found), the record is removed
    // locally (create-on-recovery owns it server-side) and the drain proceeds to the next record —
    // the queue does not stall on the 404.
    [Fact]
    public async Task NextLaunch_404OnDelivery_RemovesRecordsAndDoesNotStall()
    {
        // Arrange — persist TWO records, then a launch whose server returns 404 for every end.
        using var server404 = new RecordingSessionEndServer { SessionEndStatusCode = 404 };
        var product = NewProduct();

        // Persist offline (non-routable) so neither end is delivered before launch 2.
        var launch1 = new BeaconTracker(BuildOptions(product, NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        launch1.StartSession("user-a");
        launch1.StartSession("user-b"); // ends user-a, starts user-b
        await launch1.DisposeAsync();    // persists the user-b end too → two records

        ReopenStore(product).Should().HaveCount(2, "two sessions ended offline → two queued records");

        // Act — launch 2 drains against the 404 server.
        var launch2 = new BeaconTracker(BuildOptions(product, server404.BaseUrl));
        await launch2.FlushAsync();
        await server404.WaitForSessionEndRequestsAsync(2, TimeSpan.FromSeconds(5));

        // Assert — both records were attempted (no stall) and removed locally on 404.
        server404.SessionEndRequests.Count.Should().BeGreaterThanOrEqualTo(2,
            "the drain must continue past a 404 rather than stalling on the first record");
        ReopenStore(product).Should().BeEmpty(
            "a 404 is non-terminal: the record is dropped locally for the server's create-on-recovery upsert");

        await launch2.DisposeAsync();
    }

    // ── Case 5: EndSessionAsync / EndSession()+FlushAsync drain a pending end ─────────────────

    // Case 5a: EndSessionAsync() persists and drains the pending end (awaitable guarantee).
    [Fact]
    public async Task EndSessionAsync_DrainsPendingEnd_SendHappens()
    {
        // Arrange
        using var server = new RecordingSessionEndServer { SessionEndStatusCode = 200 };
        var tracker = CreateTracker(server.BaseUrl);
        tracker.StartSession("user-1");

        // Act
        await tracker.EndSessionAsync();

        // Assert — a session-end request reached the server and the session is cleared.
        (await server.WaitForSessionEndRequestsAsync(1, TimeSpan.FromSeconds(5))).Should().BeTrue();
        TrackerTestHelper.GetSessionId(tracker).Should().BeNull();

        await tracker.DisposeAsync();
    }

    // Case 5b: EndSession(); await FlushAsync(); drains the pending end.
    [Fact]
    public async Task EndSessionThenFlushAsync_DrainsPendingEnd_SendHappens()
    {
        // Arrange
        using var server = new RecordingSessionEndServer { SessionEndStatusCode = 200 };
        var tracker = CreateTracker(server.BaseUrl);
        tracker.StartSession("user-1");

        // Act
        tracker.EndSession();
        await tracker.FlushAsync();

        // Assert — at least one session-end delivered (best-effort fire-and-forget + FlushAsync drain).
        (await server.WaitForSessionEndRequestsAsync(1, TimeSpan.FromSeconds(5))).Should().BeTrue();

        await tracker.DisposeAsync();
    }

    // ── Case 6: ShutdownFlushTimeout = Zero skips the blocking send but persists ──────────────

    // Case 6: with ShutdownFlushTimeout = Zero, dispose does NOT make a blocking session-end send,
    // yet the durable record is still persisted for next-launch recovery.
    [Fact]
    public async Task Dispose_WithZeroTimeout_PersistsRecord_NoBlockingSend()
    {
        // Arrange
        using var server = new RecordingSessionEndServer { SessionEndStatusCode = 200 };
        var tracker = CreateTracker(server.BaseUrl, shutdownFlushTimeout: TimeSpan.Zero);
        tracker.StartSession("user-1");
        var product = ProductOf(tracker);

        // Act
        await tracker.DisposeAsync();
        // Give any (incorrect) fire-and-forget send a brief window to arrive.
        await server.WaitForSessionEndRequestsAsync(1, TimeSpan.FromMilliseconds(500));

        // Assert — record persisted, no blocking session-end send was made.
        ReopenStore(product).Should().HaveCount(1);
        server.SessionEndRequests.Should().BeEmpty(
            "ShutdownFlushTimeout=Zero skips the blocking dispose send entirely (disk-only)");
    }

    // ── Case 7: multiple pending ends are a queue, not a single row ───────────────────────────

    // Case 7a: StartSession → StartSession → Dispose persists TWO records (queue, not single-row).
    [Fact]
    public async Task TwoSessionsEndedInOneRun_PersistTwoRecords()
    {
        // Arrange — non-routable URL so the best-effort live sends fail and both ends stay on disk.
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        var product = ProductOf(tracker);

        // Act — second StartSession ends the first; Dispose ends the second.
        tracker.StartSession("user-1");
        tracker.StartSession("user-2");
        await tracker.DisposeAsync();

        // Assert — two records queued (a single-row store would silently drop the first end).
        ReopenStore(product).Should().HaveCount(2);
    }

    // Case 7 (self-contained start context): each persisted end carries the actor that owned THAT
    // session, not a later one. StartSession("user-1") then StartSession("user-2") must persist a
    // user-1 end and a user-2 end. (This isolates the per-record actor correctness from the count.)
    [Fact]
    public async Task TwoSessionsEndedInOneRun_EachRecordCarriesItsOwnActor()
    {
        // Arrange — non-routable so both ends persist for inspection.
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        var product = ProductOf(tracker);

        // Act
        tracker.StartSession("user-1");
        tracker.StartSession("user-2");
        await tracker.DisposeAsync();

        // Assert — the two records carry user-1 and user-2 respectively (self-contained start ctx).
        var ends = ReopenStore(product);
        ends.Should().HaveCount(2);
        ends.Select(e => e.ActorId).Should().BeEquivalentTo(["user-1", "user-2"],
            "each durable end must carry the actor that owned the session it ends, not a later one");
    }

    // Case 7b: both persisted ends are delivered on the next launch.
    [Fact]
    public async Task TwoPersistedRecords_BothDeliveredOnNextLaunch()
    {
        // Arrange — persist two ends offline, then drain them in a second launch.
        using var server = new RecordingSessionEndServer { SessionEndStatusCode = 200 };
        var product = NewProduct();

        var launch1 = new BeaconTracker(BuildOptions(product, NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        launch1.StartSession("user-1");
        launch1.StartSession("user-2");
        await launch1.DisposeAsync();
        ReopenStore(product).Should().HaveCount(2);

        // Act
        var launch2 = new BeaconTracker(BuildOptions(product, server.BaseUrl));
        await launch2.FlushAsync();
        (await server.WaitForSessionEndRequestsAsync(2, TimeSpan.FromSeconds(5))).Should().BeTrue();

        // Assert — both ends delivered, store drained.
        server.SessionEndRequests.Should().HaveCountGreaterThanOrEqualTo(2);
        ReopenStore(product).Should().BeEmpty();

        await launch2.DisposeAsync();
    }

    // ── Case 8: opt-out / disabled / no session → no record written ──────────────────────────

    // Case 8a: a tracker that has opted out writes no durable record on dispose.
    [Fact]
    public async Task OptedOut_Dispose_WritesNoRecord()
    {
        // Arrange
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        tracker.StartSession("user-1");
        tracker.OptOut();
        var product = ProductOf(tracker);

        // Act
        await tracker.DisposeAsync();

        // Assert
        ReopenStore(product).Should().BeEmpty("opt-out suppresses the durable end write");
    }

    // Case 8b: a disabled tracker (Enabled=false) writes no durable record.
    [Fact]
    public async Task Disabled_Dispose_WritesNoRecord()
    {
        // Arrange — a disabled tracker has no store at all; StartSession/Dispose are no-ops.
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, enabled: false, shutdownFlushTimeout: TimeSpan.Zero));
        tracker.StartSession("user-1");
        var product = ProductOf(tracker);

        // Act
        await tracker.DisposeAsync();

        // Assert — nothing persisted (the store is never initialized when disabled).
        ReopenStore(product).Should().BeEmpty();
    }

    // Case 8c: dispose with NO active session writes no durable record.
    [Fact]
    public async Task NoActiveSession_Dispose_WritesNoRecord()
    {
        // Arrange — never call StartSession.
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        var product = ProductOf(tracker);

        // Act
        await tracker.DisposeAsync();

        // Assert
        ReopenStore(product).Should().BeEmpty();
    }

    // ── Case 9: opt-out after a record exists purges; Reset clears the store ──────────────────

    // Case 9a: a pending end persisted before OptOut() is purged (not delivered).
    [Fact]
    public async Task OptOut_AfterRecordExists_PurgesNotDelivers()
    {
        // Arrange — persist a record offline (non-routable): a second StartSession ends the first
        // session and its best-effort live send fails, leaving the end on disk.
        using var server = new RecordingSessionEndServer { SessionEndStatusCode = 200 };
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        tracker.StartSession("user-1");
        tracker.StartSession("user-2"); // ends user-1 → one durable record persisted (offline)
        var product = ProductOf(tracker);
        ReopenStoreLive(tracker).Should().HaveCountGreaterThanOrEqualTo(1);

        // Act — opt out should purge pending ends, not deliver them.
        tracker.OptOut();

        // Assert — the store is purged.
        PendingSessionEndTestHelper.GetPendingEndCount(tracker).Should().Be(0,
            "OptOut() purges pending session-ends rather than delivering them");

        await tracker.DisposeAsync();
        ReopenStore(product).Should().BeEmpty("the purged record is gone, never delivered");
    }

    // Case 9b: Reset() clears the pending-end store.
    [Fact]
    public async Task Reset_ClearsPendingEndStore()
    {
        // Arrange — persist a record via an offline-style session swap.
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        tracker.StartSession("user-1");
        tracker.StartSession("user-2"); // ends user-1 → persisted record (offline)
        ReopenStoreLive(tracker).Should().HaveCountGreaterThanOrEqualTo(1);

        // Act
        tracker.Reset();

        // Assert
        PendingSessionEndTestHelper.GetPendingEndCount(tracker).Should().Be(0,
            "Reset() clears the pending-end store");

        await tracker.DisposeAsync();
    }

    // ── Case 10: store is separate from the event DiskQueue ──────────────────────────────────

    // Case 10: a session-end record never lands in the event DiskQueue batch — the two stores are
    // separate tables. Tracking an event + ending the session leaves the event(s) in the event
    // queue and the session-end in the pending-end store, with no cross-contamination.
    [Fact]
    public async Task SessionEnd_NeverLandsInEventDiskQueue()
    {
        // Arrange — non-routable so neither queue is drained; Zero timeout so the end is persisted.
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        var product = ProductOf(tracker);

        tracker.StartSession("user-1");
        tracker.Track("cat", "an_event", "user-1");

        // Act
        await tracker.DisposeAsync();

        // Assert — the event disk queue holds the tracked event(s); none of them is a session-end
        // payload (no end_reason field), and the pending-end store holds exactly the session end.
        var dbPath = DiskQueueLocationHelper.QueueDbPath(product);
        using (var eventQueue = new DiskQueue(dbPath, null))
        {
            var events = eventQueue.DequeueUpTo(1000);
            events.Should().NotBeEmpty("the tracked event was persisted to the event disk queue");
            foreach (var e in events)
                e.Payload.Should().NotContain("end_reason",
                    "a session-end lifecycle record must never appear in the event batch");
        }

        ReopenStore(product).Should().HaveCount(1, "the session-end lives only in the pending-end store");
    }

    // ── RG-1: write-before-clear ─────────────────────────────────────────────────────────────

    // RG-1: the durable record is persisted even though _sessionId is cleared by the same end path.
    // Guards the persist-BEFORE-clear ordering: after the prior session ends, _sessionId no longer
    // holds it AND a record for it exists — proving the write happened before the clear (a crash
    // between them loses nothing).
    [Fact]
    public void RG1_PersistBeforeClear_RecordPersistedEvenAfterSessionIdCleared()
    {
        // Arrange — non-routable so the prior end is persisted (live send fails) without a flush.
        var tracker = new BeaconTracker(BuildOptions(NewProduct(), NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        tracker.StartSession("user-1");
        var firstSessionId = TrackerTestHelper.GetSessionId(tracker);

        // Act — ending user-1 (via a new StartSession) clears its session id AND persists its end.
        tracker.StartSession("user-2");

        // Assert — the first session's id is no longer the active id, yet its end IS persisted.
        TrackerTestHelper.GetSessionId(tracker).Should().NotBe(firstSessionId);
        var ends = PendingSessionEndTestHelper.GetPendingEnds(tracker);
        ends.Should().Contain(e => e.SessionId == firstSessionId,
            "the durable write must happen BEFORE _sessionId is cleared, so the end is never lost");

        tracker.Dispose();
    }

    // ── RG-2: recovery payload field names match the server contract ─────────────────────────

    // RG-2: the next-launch recovery delivery sends EXACTLY the server-contract field names —
    // product, product_version, actor_id, started_at, account_id, license_id (plus session_id,
    // ended_at, end_reason). A rename on either side breaks create-on-recovery silently.
    [Fact]
    public async Task RG2_RecoveryPayload_CarriesServerContractFieldNames()
    {
        // Arrange — persist a record with account+license context, then drain it on next launch.
        using var server = new RecordingSessionEndServer { SessionEndStatusCode = 200 };
        var product = NewProduct();

        var launch1 = new BeaconTracker(BuildOptions(product, NonRoutableUrl, shutdownFlushTimeout: TimeSpan.Zero));
        launch1.SetAccount("acct_z");
        launch1.SetLicense("lic_z");
        launch1.StartSession("actor-99");
        await launch1.DisposeAsync();

        // Act
        var launch2 = new BeaconTracker(BuildOptions(product, server.BaseUrl));
        await launch2.FlushAsync();
        (await server.WaitForSessionEndRequestsAsync(1, TimeSpan.FromSeconds(5))).Should().BeTrue();

        // Assert — the captured JSON carries the exact snake_case server-contract keys.
        var end = server.SessionEndRequests.Single();
        using var doc = JsonDocument.Parse(end.Body);
        var root = doc.RootElement;

        root.TryGetProperty("session_id", out _).Should().BeTrue();
        root.TryGetProperty("ended_at", out _).Should().BeTrue();
        root.TryGetProperty("end_reason", out _).Should().BeTrue();
        root.TryGetProperty("actor_id", out var actor).Should().BeTrue();
        root.TryGetProperty("product", out var prod).Should().BeTrue();
        root.TryGetProperty("product_version", out var pver).Should().BeTrue();
        root.TryGetProperty("started_at", out _).Should().BeTrue();
        root.TryGetProperty("account_id", out var acct).Should().BeTrue();
        root.TryGetProperty("license_id", out var lic).Should().BeTrue();

        actor.GetString().Should().Be("actor-99");
        prod.GetString().Should().Be(product);
        pver.GetString().Should().Be("1.2.3");
        acct.GetString().Should().Be("acct_z");
        lic.GetString().Should().Be("lic_z");

        // The recovery payload must NOT use any camelCase server-contract aliases.
        root.TryGetProperty("productVersion", out _).Should().BeFalse();
        root.TryGetProperty("actorId", out _).Should().BeFalse();
        root.TryGetProperty("startedAt", out _).Should().BeFalse();

        await launch2.DisposeAsync();
    }

    // ── Shared reflection / store helpers ─────────────────────────────────────────────────────

    private static string ProductOf(BeaconTracker tracker)
    {
        var options = (BeaconOptions)typeof(BeaconTracker)
            .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tracker)!;
        return options.Product;
    }

    // Reads the tracker's LIVE store (still open) — used while the tracker is undisposed.
    private static IReadOnlyList<PendingSessionEnd> ReopenStoreLive(BeaconTracker tracker)
        => PendingSessionEndTestHelper.GetPendingEnds(tracker);

    // Re-opens the per-product pending-end store DB file and returns its rows (oldest first).
    // Used after Dispose, which closes the tracker's own store connection.
    private static IReadOnlyList<PendingSessionEnd> ReopenStore(string product)
    {
        var dbPath = DiskQueueLocationHelper.QueueDbPath(product);
        if (!File.Exists(dbPath))
            return [];
        using var store = new PendingSessionEndStore(dbPath, null);
        return store.DequeueUpTo(1000);
    }
}
