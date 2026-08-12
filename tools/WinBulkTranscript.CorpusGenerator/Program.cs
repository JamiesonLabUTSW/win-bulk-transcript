using System.Globalization;

namespace WinBulkTranscript.CorpusGenerator;

/// <summary>
/// Entry point for the development-only synthetic media corpus generator.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Generates a corpus or lists the usable installed English voices.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>A process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = GeneratorOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(GeneratorOptions.Usage);
                return 0;
            }

            if (options.ListVoices)
            {
                foreach (var voice in WindowsTtsAndMedia.ListEnglishVoices())
                {
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{voice.Id}\t{voice.DisplayName}\t{voice.Language}\t{voice.Gender}"));
                }

                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            if (options.Phase0FixturePath is not null)
            {
                using var media = WindowsTtsAndMedia.Create(options);
                var result = await Phase0PcmFixtureWriter.WriteAsync(options.Phase0FixturePath, media, cancellation.Token).ConfigureAwait(false);
                Console.WriteLine($"Phase 0 PCM fixture written to '{result.PcmPath}'.");
                Console.WriteLine($"Provenance sidecar written to '{result.ManifestPath}'. SHA-256: {result.Sha256}");
                return 0;
            }

            if (options.CancellationFixturePath is not null)
            {
                using var media = WindowsTtsAndMedia.Create(options);
                var result = await CancellationProbeFixtureWriter.WriteAsync(options.CancellationFixturePath, media, cancellation.Token).ConfigureAwait(false);
                Console.WriteLine($"Cancellation probe fixture written to '{result.Mp4Path}'.");
                Console.WriteLine($"Provenance sidecar written to '{result.ManifestPath}'. SHA-256: {result.Sha256}");
                Console.WriteLine($"Decoded duration: {result.DecodedDurationSeconds:F3} seconds.");
                return 0;
            }
            if (options.MediaFixtureMatrixRoot is not null)
            {
                using var media = WindowsTtsAndMedia.Create(options);
                var result = await MediaFixtureMatrixWriter.WriteAsync(options.MediaFixtureMatrixRoot, media, cancellation.Token).ConfigureAwait(false);
                Console.WriteLine($"Media fixture matrix written to '{result.RootPath}'.");
                Console.WriteLine($"Provenance sidecar written to '{result.ManifestPath}'.");
                foreach (var fixture in result.Fixtures)
                {
                    Console.WriteLine($"  {fixture.FileName}: {fixture.Sha256}");
                }

                return 0;
            }


            Console.WriteLine($"Generating synthetic corpus in '{options.OutputRoot}'.");
            await new SyntheticCorpusGenerator(options).GenerateAsync(cancellation.Token).ConfigureAwait(false);
            Console.WriteLine("Synthetic corpus generation completed successfully.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Synthetic corpus generation was cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Synthetic corpus generation failed: {exception.Message}");
            return 1;
        }
    }
}
