namespace SignalFish.Client.Tests
{
    using System;
    using System.IO;
    using System.Text.Json;
    using NUnit.Framework;

    /// <summary>
    /// Red-green anchor for the vendored golden fixtures (PLAN.md M1.1): the
    /// pinned upstream corpus must be present and structurally consumable as
    /// JSON envelopes, so M1.2/M1.3 codec work starts from trustworthy
    /// samples. Content drift versus upstream is guarded by
    /// scripts/sync-protocol-fixtures.ps1 (verify mode).
    /// </summary>
    [TestFixture]
    public class GoldenFixtureCorpusTests
    {
        private static readonly string[] PinnedFixtureFiles =
        {
            "v2-client-messages.jsonl",
            "v2-server-messages.jsonl",
            "v3-client-messages.jsonl",
            "v3-server-messages.jsonl",
        };

        private static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "Golden");

        [Test]
        public void CorpusWithPinnedFilesAreVendoredAndNonEmpty()
        {
            Assert.That(
                Directory.Exists(GoldenDirectory),
                Is.True,
                $"Golden fixtures missing from test output at {GoldenDirectory}."
            );

            foreach (string fileName in PinnedFixtureFiles)
            {
                string path = Path.Combine(GoldenDirectory, fileName);
                Assert.That(File.Exists(path), Is.True, $"Missing fixture: {fileName}.");
                string[] lines = File.ReadAllLines(path);
                Assert.That(
                    lines.Count(IsEnvelopeLine),
                    Is.GreaterThan(0),
                    $"Fixture has no wire samples: {fileName}."
                );
                Assert.That(
                    lines.Where(l => !IsEnvelopeLine(l)),
                    Is.Empty,
                    $"Fixture has blank or whitespace-only lines (JSONL corruption): {fileName}."
                );
            }
        }

        [Test]
        public void CorpusEveryEnvelopeLineIsJsonObjectWithTypedDiscriminator()
        {
            foreach (string fileName in PinnedFixtureFiles)
            {
                string path = Path.Combine(GoldenDirectory, fileName);
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!IsEnvelopeLine(lines[i]))
                    {
                        continue;
                    }

                    JsonDocument doc;
                    try
                    {
                        doc = JsonDocument.Parse(lines[i]);
                    }
                    catch (JsonException ex)
                    {
                        Assert.Fail($"{fileName}:{i + 1} is not valid JSON: {ex.Message}");
                        return;
                    }

                    using (doc)
                    {
                        Assert.That(
                            doc.RootElement.ValueKind,
                            Is.EqualTo(JsonValueKind.Object),
                            $"{fileName}:{i + 1} must be a JSON object."
                        );
                        Assert.That(
                            HasNonEmptyType(doc.RootElement),
                            Is.True,
                            $"{fileName}:{i + 1} must carry a non-empty string \"type\" discriminator."
                        );
                        if (doc.RootElement.TryGetProperty("data", out JsonElement data))
                        {
                            Assert.That(
                                data.ValueKind,
                                Is.EqualTo(JsonValueKind.Object),
                                $"{fileName}:{i + 1} \"data\" must be an object when present."
                            );
                        }
                    }
                }
            }
        }

        private static bool HasNonEmptyType(JsonElement envelope)
        {
            return envelope.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString()!.Length > 0;
        }

        private static bool IsEnvelopeLine(string line) => line.Trim().Length > 0;
    }
}
