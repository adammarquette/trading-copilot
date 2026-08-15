using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class VenueCredentialSchemaTests
{
    private static VenueCredentialField Field(string key) =>
        VenueCredentialField.Create(key, label: key, secret: false, required: true);

    [Fact]
    public void Of_ShouldPreserveFieldOrder_WhenGivenMultipleFields()
    {
        // The schema is an *ordered* list (ADR-0023) — the order fields are declared is the order the onboarding
        // form renders them, so it must survive construction exactly.
        VenueCredentialSchema schema = VenueCredentialSchema.Of(Field("first"), Field("second"), Field("third"));

        schema.Fields.Select(f => f.Key).Should().ContainInOrder("first", "second", "third");
    }

    [Fact]
    public void Of_ShouldThrow_WhenNoFieldsGiven()
    {
        Action act = () => VenueCredentialSchema.Of();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Of_ShouldThrow_WhenKeysCollide()
    {
        Action act = () => VenueCredentialSchema.Of(Field("ApiKey"), Field("ApiKey"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Of_ShouldThrow_WhenKeysCollideCaseInsensitively()
    {
        // .NET configuration keys are case-insensitive, so two fields differing only in case would map to one config
        // entry — an ambiguity the schema refuses at declaration rather than silently dropping a field.
        Action act = () => VenueCredentialSchema.Of(Field("apikey"), Field("ApiKey"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Of_ShouldNotReflectLaterMutationOfTheInputArray_WhenBuilt()
    {
        VenueCredentialField[] fields = [Field("first"), Field("second")];

        VenueCredentialSchema schema = VenueCredentialSchema.Of(fields);
        fields[0] = Field("mutated");

        schema.Fields[0].Key.Should().Be("first");
    }

    [Fact]
    public void Of_ShouldThrow_WhenFieldsArrayIsNull()
    {
        Action act = () => VenueCredentialSchema.Of(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Of_ShouldThrow_WhenAFieldIsNull()
    {
        Action act = () => VenueCredentialSchema.Of(Field("ApiKey"), null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
