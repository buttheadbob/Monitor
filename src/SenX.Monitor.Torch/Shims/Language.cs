// Types the compiler expects for newer language features. net48 doesn't have them, and nothing else about
// these features needs the runtime, so they only have to exist.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute;

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute;
}

namespace System
{
    /// <summary>What <c>list[^1]</c> becomes.</summary>
    internal readonly struct Index(int value, bool fromEnd = false)
    {
        public static implicit operator Index(int value) => new(value);

        public int GetOffset(int length) => fromEnd ? length - value : value;
    }

    /// <summary>What <c>text[..5]</c> becomes.</summary>
    internal readonly struct Range(Index start, Index end)
    {
        public static Range All => new(0, new Index(0, true));

        public Index Start { get; } = start;

        public Index End { get; } = end;

        public static Range StartAt(Index start) => new(start, new Index(0, true));

        public static Range EndAt(Index end) => new(0, end);
    }
}
