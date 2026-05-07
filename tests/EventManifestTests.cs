// Covers:
//   AC-1: EventDefinitionBuilder.Define() returns itself (fluent chaining)
//   AC-2: EventDefinitionBuilder.Define() throws ArgumentException for null/empty/whitespace category or name
//   AC-3: EventDefinitionBuilder.Build() deduplicates entries (same category+name added twice -> appears once)
//   AC-4: EventDefinitionBuilder.Build() returns entries sorted by category then name
//   AC-5: EventDefinitionBuilder.Build() returns empty list when no events defined
//   AC-6: ExportEventManifest(filePath) writes valid JSON matching the portal import schema
//   AC-7: ExportEventManifest works even when the SDK is disabled (Enabled = false)
//   AC-8: ExportEventManifest with no defined events writes a valid manifest with empty entries array

using System.Text.Json;
using FluentAssertions;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for EventDefinitionBuilder and BeaconTracker.ExportEventManifest().
/// </summary>
public sealed class EventManifestTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { /* best effort cleanup */ }
        }
    }

    private string CreateTempFilePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"beacon_manifest_{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    // ── EventDefinitionBuilder tests ────────────────────────────────

    // AC-1: Define() returns the same builder instance for fluent chaining
    [Fact]
    public void Define_ReturnsSameBuilderInstance()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var result = builder.Define("category", "name");

        // Assert
        result.Should().BeSameAs(builder, "Define() must return the same instance for fluent chaining");
    }

    // AC-1 supplemental: Multiple chained calls all return the same instance
    [Fact]
    public void Define_ChainedMultipleTimes_ReturnsSameInstance()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var result = builder
            .Define("cat1", "name1")
            .Define("cat2", "name2")
            .Define("cat3", "name3");

        // Assert
        result.Should().BeSameAs(builder);
    }

    // AC-2: Define() throws ArgumentException for null category
    [Fact]
    public void Define_WithNullCategory_ThrowsArgumentException()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var act = () => builder.Define(null!, "name");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // AC-2: Define() throws ArgumentException for empty category
    [Fact]
    public void Define_WithEmptyCategory_ThrowsArgumentException()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var act = () => builder.Define("", "name");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // AC-2: Define() throws ArgumentException for whitespace-only category
    [Fact]
    public void Define_WithWhitespaceCategory_ThrowsArgumentException()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var act = () => builder.Define("   ", "name");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // AC-2: Define() throws ArgumentException for null name
    [Fact]
    public void Define_WithNullName_ThrowsArgumentException()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var act = () => builder.Define("category", null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // AC-2: Define() throws ArgumentException for empty name
    [Fact]
    public void Define_WithEmptyName_ThrowsArgumentException()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var act = () => builder.Define("category", "");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // AC-2: Define() throws ArgumentException for whitespace-only name
    [Fact]
    public void Define_WithWhitespaceName_ThrowsArgumentException()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var act = () => builder.Define("category", "  \t ");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // AC-3: Build() deduplicates entries — same (category, name) added twice appears once
    [Fact]
    public void Build_WithDuplicateEntries_DeduplicatesToSingleEntry()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();
        builder.Define("billing", "payment_received");
        builder.Define("billing", "payment_received"); // duplicate

        // Act
        var result = builder.Build();

        // Assert
        result.Should().HaveCount(1);
        result[0].Category.Should().Be("billing");
        result[0].Name.Should().Be("payment_received");
    }

    // AC-3 supplemental: Different category or name are not treated as duplicates
    [Fact]
    public void Build_WithSameCategoryDifferentName_KeepsBoth()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();
        builder.Define("billing", "payment_received");
        builder.Define("billing", "payment_failed");

        // Act
        var result = builder.Build();

        // Assert
        result.Should().HaveCount(2);
    }

    // AC-4: Build() returns entries sorted by category first, then by name
    [Fact]
    public void Build_ReturnsSortedByCategoryThenName()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();
        builder.Define("users", "logout");
        builder.Define("billing", "refund");
        builder.Define("billing", "charge");
        builder.Define("users", "login");
        builder.Define("analytics", "page_view");

        // Act
        var result = builder.Build();

        // Assert
        result.Should().HaveCount(5);
        result[0].Should().Be(("analytics", "page_view"));
        result[1].Should().Be(("billing", "charge"));
        result[2].Should().Be(("billing", "refund"));
        result[3].Should().Be(("users", "login"));
        result[4].Should().Be(("users", "logout"));
    }

    // AC-5: Build() returns empty list when no events defined
    [Fact]
    public void Build_WithNoEvents_ReturnsEmptyList()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();

        // Act
        var result = builder.Build();

        // Assert
        result.Should().BeEmpty();
    }

    // ── ExportEventManifest tests ───────────────────────────────────

    // AC-6: ExportEventManifest writes valid JSON matching the portal import schema
    [Fact]
    public void ExportEventManifest_WritesValidJsonWithCorrectSchema()
    {
        // Arrange
        var options = new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "https://beacon.example.com",
            AppName = "MyApp",
            AppVersion = "3.2.1",
            FlushIntervalSeconds = 3600
        };
        options.Events
            .Define("billing", "charge")
            .Define("users", "login");

        var tracker = new BeaconTracker(options);
        var filePath = CreateTempFilePath();

        // Act
        tracker.ExportEventManifest(filePath);

        // Assert
        File.Exists(filePath).Should().BeTrue("manifest file should be written to disk");

        var json = File.ReadAllText(filePath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("schema_version").GetString().Should().Be("1");
        root.GetProperty("generated_at").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("source_app").GetString().Should().Be("MyApp");
        root.GetProperty("source_version").GetString().Should().Be("3.2.1");

        var entries = root.GetProperty("entries");
        entries.GetArrayLength().Should().Be(2);

        // Entries should be sorted (billing before users)
        entries[0].GetProperty("category").GetString().Should().Be("billing");
        entries[0].GetProperty("name").GetString().Should().Be("charge");
        entries[1].GetProperty("category").GetString().Should().Be("users");
        entries[1].GetProperty("name").GetString().Should().Be("login");

        doc.Dispose();
        tracker.Dispose();
    }

    // AC-6 supplemental: generated_at is a valid ISO 8601 timestamp near current time
    [Fact]
    public void ExportEventManifest_GeneratedAtIsValidTimestamp()
    {
        // Arrange
        var options = new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "https://beacon.example.com",
            AppName = "TestApp",
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600
        };
        options.Events.Define("test", "event");

        var tracker = new BeaconTracker(options);
        var filePath = CreateTempFilePath();
        var before = DateTimeOffset.UtcNow;

        // Act
        tracker.ExportEventManifest(filePath);

        // Assert
        var after = DateTimeOffset.UtcNow;
        var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        var generatedAtStr = doc.RootElement.GetProperty("generated_at").GetString()!;
        var generatedAt = DateTimeOffset.Parse(generatedAtStr);

        generatedAt.Should().BeOnOrAfter(before.AddSeconds(-1));
        generatedAt.Should().BeOnOrBefore(after.AddSeconds(1));

        doc.Dispose();
        tracker.Dispose();
    }

    // AC-7: ExportEventManifest works when the SDK is disabled (Enabled = false)
    [Fact]
    public void ExportEventManifest_WhenDisabled_StillWritesManifest()
    {
        // Arrange
        var options = new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "https://beacon.example.com",
            AppName = "DisabledApp",
            AppVersion = "1.0.0",
            Enabled = false
        };
        options.Events
            .Define("billing", "invoice_created")
            .Define("users", "signup");

        var tracker = new BeaconTracker(options);
        var filePath = CreateTempFilePath();

        // Act
        tracker.ExportEventManifest(filePath);

        // Assert
        File.Exists(filePath).Should().BeTrue("manifest should be written even when SDK is disabled");

        var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        var root = doc.RootElement;

        root.GetProperty("schema_version").GetString().Should().Be("1");
        root.GetProperty("source_app").GetString().Should().Be("DisabledApp");
        root.GetProperty("source_version").GetString().Should().Be("1.0.0");

        var entries = root.GetProperty("entries");
        entries.GetArrayLength().Should().Be(2);
        entries[0].GetProperty("category").GetString().Should().Be("billing");
        entries[0].GetProperty("name").GetString().Should().Be("invoice_created");
        entries[1].GetProperty("category").GetString().Should().Be("users");
        entries[1].GetProperty("name").GetString().Should().Be("signup");

        doc.Dispose();
        tracker.Dispose();
    }

    // AC-8: ExportEventManifest with no defined events writes manifest with empty entries array
    [Fact]
    public void ExportEventManifest_WithNoEvents_WritesEmptyEntriesArray()
    {
        // Arrange
        var options = new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "https://beacon.example.com",
            AppName = "EmptyApp",
            AppVersion = "0.0.1",
            FlushIntervalSeconds = 3600
        };
        // No events defined on options.Events

        var tracker = new BeaconTracker(options);
        var filePath = CreateTempFilePath();

        // Act
        tracker.ExportEventManifest(filePath);

        // Assert
        var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        var root = doc.RootElement;

        root.GetProperty("schema_version").GetString().Should().Be("1");
        root.GetProperty("source_app").GetString().Should().Be("EmptyApp");
        root.GetProperty("source_version").GetString().Should().Be("0.0.1");

        var entries = root.GetProperty("entries");
        entries.GetArrayLength().Should().Be(0, "entries array should be empty when no events are defined");

        doc.Dispose();
        tracker.Dispose();
    }

    // AC-6 supplemental: JSON entries have exactly category and name properties
    [Fact]
    public void ExportEventManifest_EntriesHaveOnlyCategoryAndNameProperties()
    {
        // Arrange
        var options = new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "https://beacon.example.com",
            AppName = "TestApp",
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600
        };
        options.Events.Define("feature", "click");

        var tracker = new BeaconTracker(options);
        var filePath = CreateTempFilePath();

        // Act
        tracker.ExportEventManifest(filePath);

        // Assert
        var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        var entry = doc.RootElement.GetProperty("entries")[0];

        // Entry should have exactly 2 properties: category and name
        var propertyCount = 0;
        foreach (var _ in entry.EnumerateObject())
            propertyCount++;

        propertyCount.Should().Be(2, "each entry should have exactly category and name");
        entry.GetProperty("category").GetString().Should().Be("feature");
        entry.GetProperty("name").GetString().Should().Be("click");

        doc.Dispose();
        tracker.Dispose();
    }

    // AC-6 supplemental: Root JSON has exactly the 5 required top-level properties
    [Fact]
    public void ExportEventManifest_RootHasExactlyFiveProperties()
    {
        // Arrange
        var options = new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "https://beacon.example.com",
            AppName = "TestApp",
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600
        };
        options.Events.Define("test", "event");

        var tracker = new BeaconTracker(options);
        var filePath = CreateTempFilePath();

        // Act
        tracker.ExportEventManifest(filePath);

        // Assert
        var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        var root = doc.RootElement;

        var propertyNames = new List<string>();
        foreach (var prop in root.EnumerateObject())
            propertyNames.Add(prop.Name);

        propertyNames.Should().HaveCount(5);
        propertyNames.Should().Contain("schema_version");
        propertyNames.Should().Contain("generated_at");
        propertyNames.Should().Contain("source_app");
        propertyNames.Should().Contain("source_version");
        propertyNames.Should().Contain("entries");

        doc.Dispose();
        tracker.Dispose();
    }

    // AC-3 + AC-4 combined: Deduplication and sorting work together
    [Fact]
    public void Build_WithDuplicatesAndUnsortedEntries_DeduplicatesAndSorts()
    {
        // Arrange
        var builder = new EventDefinitionBuilder();
        builder.Define("zeta", "last");
        builder.Define("alpha", "first");
        builder.Define("zeta", "last"); // duplicate
        builder.Define("alpha", "second");
        builder.Define("alpha", "first"); // duplicate

        // Act
        var result = builder.Build();

        // Assert
        result.Should().HaveCount(3);
        result[0].Should().Be(("alpha", "first"));
        result[1].Should().Be(("alpha", "second"));
        result[2].Should().Be(("zeta", "last"));
    }

    // BeaconOptions.Events is initialized and usable by default
    [Fact]
    public void BeaconOptions_Events_IsInitializedByDefault()
    {
        // Arrange & Act
        var options = new BeaconOptions();

        // Assert
        options.Events.Should().NotBeNull();
        options.Events.Should().BeOfType<EventDefinitionBuilder>();
    }
}
