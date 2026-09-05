using System.Runtime.CompilerServices;

namespace Iutq.Core;

// CA1512 would demand the .NET 8+ ThrowIf* helpers, which Unity's BCL lacks;
// explicit throws keep this engine compilable when dropped into Unity.
#pragma warning disable CA1512

// Exception policy — the single switch point for the whole engine.
//
// Managed exceptions cannot cross a Burst-compiled call, so every throw that is
// reachable at runtime lives behind this class and is compiled out where Burst
// runs. Omitted contracts follow the engine's "validate once, trust forever"
// law: validation happened at the managed boundary (bake, Load, builder), the
// kernel is total (empty spans, Try* methods, default handles), and callers that
// omit checks own the precondition — exactly like Burst-era Unity APIs.
//
//   default (.NET, Unity Editor, Development builds)  -> throw on violation
//   Unity player builds under UNITY_5_3_OR_NEWER      -> omitted (Burst-safe)
//   IUTQ_NO_EXCEPTIONS                                -> omitted everywhere
//
// DatabaseBuilder, Load and Create keep unconditional throws: they are
// inherently managed (collections, string diagnostics) and never Burst-compiled.
internal static class Check
{
#if IUTQ_NO_EXCEPTIONS || (UNITY_5_3_OR_NEWER && !UNITY_EDITOR && !DEVELOPMENT_BUILD)

    // A clip type key of zero is reserved as "invalid"; enforced at authoring time.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TypeKeyNonZero(ulong _) { }

    // Slot values originate from a validated binary search over the type table,
    // so slot + 1 cannot overflow in a checked build either; the checked context
    // exists only to surface authoring bugs in diagnostics builds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SlotPlusOne(int typeSlot) => typeSlot + 1;

    // Resolve of a missing type yields a default (invalid) handle; Burst callers
    // check IsValid exactly like TryResolve.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TypePresent(ulong _) { }

    // An invalid handle yields a default ClipQuery. Using that query is a
    // contract violation — callers resolve handles on the same database first.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HandleUsable<TClip>(in ClipTypeHandle<TClip> _) where TClip : unmanaged { }

#else

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TypeKeyNonZero(ulong key)
    {
        if (key == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SlotPlusOne(int typeSlot) => checked(typeSlot + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TypePresent(ulong key)
    {
        throw new KeyNotFoundException($"Timeline clip type 0x{key:X16} is not present in this database.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HandleUsable<TClip>(in ClipTypeHandle<TClip> handle) where TClip : unmanaged
    {
        if (!handle.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }
    }

#endif
}
