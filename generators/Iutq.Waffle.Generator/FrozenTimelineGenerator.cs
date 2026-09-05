using Microsoft.CodeAnalysis;

namespace Iutq.Waffle.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class FrozenTimelineGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var blobs = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(".iutq", StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => BlobParser.Parse(f.Path, File.ReadAllBytes(f.Path)));

        context.RegisterSourceOutput(blobs, static (spc, source) => Emitter.Emit(source, spc));
    }
}
