using System.Reflection;
using MarqSpec.TradingCopilot.Domain.Signals;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Signals;

/// <summary>
/// One half of the invariant gh#27 exists to guard (ADR-0007, ADR-0014): importance feedback is a <b>soft salience
/// weight, never a risk control</b>. A star or mute may change what the operator sees first; it must never move a
/// risk limit, a position size, or a gate decision.
/// </summary>
/// <remarks>
/// This guards the <b>likely</b> regression <b>structurally</b>: no type in the risk namespace may take, hold, or
/// return a signals type (its member surface — fields, properties, parameters, returns; generics + arrays flattened),
/// so wiring a salience-typed dependency into risk fails the build. It deliberately does <b>not</b> scan method
/// bodies, and it cannot see the gate <i>call site</i> (<c>Api/Orders</c>, <c>Domain/Execution</c>) — where a leak
/// would be untyped anyway, e.g. scaling an <c>int</c> quantity by a multiplier before building the proposal. That
/// behavioural path — "a maximally-starred/muted signal leaves a gate outcome byte-identical" — is the paired
/// <b>QA #362</b> suite's assertion (independent, out of this tree), not this test's.
/// </remarks>
public class SalienceIsNotARiskControlTests
{
    private const string RiskNamespace = "MarqSpec.TradingCopilot.Domain.Risk";
    private const string SignalsNamespace = "MarqSpec.TradingCopilot.Domain.Signals";

    [Fact]
    public void NoRiskType_ReferencesTheSignalsNamespace_OnItsMemberSurface()
    {
        Assembly domain = typeof(SalienceScorer).Assembly;
        HashSet<Type> signalsTypes = [.. domain.GetTypes().Where(type => type.Namespace == SignalsNamespace)];
        List<Type> riskTypes = [.. domain.GetTypes().Where(type => type.Namespace == RiskNamespace)];

        riskTypes.Should().NotBeEmpty("the risk gate types must exist for this guard to mean anything");
        signalsTypes.Should().NotBeEmpty("the signals types must exist for this guard to mean anything");

        List<string> leaks =
        [
            .. from riskType in riskTypes
               from referenced in SurfaceTypes(riskType).SelectMany(Flatten)
               where signalsTypes.Contains(referenced)
               select $"{riskType.Name} -> {referenced.Name}",
        ];

        leaks.Should().BeEmpty(
            "a risk type referencing a signals type would let importance feedback reach enforcement (ADR-0007) — "
            + "salience must stay a read-side weight");
    }

    [Fact]
    public void TheSentimentKinds_LiveInTheGuardedSignalsNamespace()
    {
        // gh#762: name the direction axis explicitly, so its coverage by the risk<->signals structural guard is ON THE
        // RECORD rather than incidental. ThumbsUp / ThumbsDown are SoftSignalKind values, and SoftSignalKind lives in
        // Domain.Signals -- the namespace NoRiskType_ReferencesTheSignalsNamespace forbids any risk type from touching.
        // So a risk type that took a sentiment kind (directly or via SoftSignalKind) would already fail that guard;
        // this pins the intent that direction feedback, like importance, is structurally unreachable from enforcement.
        Enum.IsDefined(SoftSignalKind.ThumbsUp).Should().BeTrue();
        Enum.IsDefined(SoftSignalKind.ThumbsDown).Should().BeTrue();
        typeof(SoftSignalKind).Namespace.Should().Be(SignalsNamespace);
        typeof(SoftSignalAxis).Namespace.Should().Be(SignalsNamespace);

        Assembly domain = typeof(SalienceScorer).Assembly;
        HashSet<Type> signalsTypes = [.. domain.GetTypes().Where(type => type.Namespace == SignalsNamespace)];
        signalsTypes.Should().Contain(
            typeof(SoftSignalKind),
            "the sentiment kinds' enum must sit in the guarded namespace for the structural guard to cover them");
    }

    // Every type on a type's member surface: field + property types, method return + parameter types, constructor
    // parameter types (public and non-public). A salience input wired into the gate surfaces here.
    private static IEnumerable<Type> SurfaceTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (FieldInfo field in type.GetFields(all))
        {
            yield return field.FieldType;
        }

        foreach (PropertyInfo property in type.GetProperties(all))
        {
            yield return property.PropertyType;
        }

        foreach (MethodInfo method in type.GetMethods(all))
        {
            yield return method.ReturnType;
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(all))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    // Unwrap generics + arrays so a List<SalienceScore> or SalienceScore[] hiding a reference is still caught.
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is Type element)
        {
            foreach (Type inner in Flatten(element))
            {
                yield return inner;
            }
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type inner in Flatten(argument))
                {
                    yield return inner;
                }
            }
        }
    }
}
