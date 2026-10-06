namespace SignalFish.Client.Tests
{
    using System;
    using System.Reflection;
    using NUnit.Framework;
    using SignalFish.Client;

    [TestFixture]
    public class SignalFishClientInfoTests
    {
        [Test]
        public void DefaultServerUriDefaultHostMatchesProtocolDefaults()
        {
            Uri uri = SignalFishClientInfo.DefaultServerUri();

            Assert.That(uri.Port, Is.EqualTo(3536));
            Assert.That(uri.AbsolutePath, Is.EqualTo("/v2/ws"));
            Assert.That(uri.Scheme, Is.EqualTo("ws"));
        }

        [Test]
        public void DefaultServerUriCustomHostPreservesHost()
        {
            Uri uri = SignalFishClientInfo.DefaultServerUri("games.example.com");

            Assert.That(uri.Host, Is.EqualTo("games.example.com"));
        }

        [TestCase("::1", "[::1]")]
        [TestCase("2001:db8::1", "[2001:db8::1]")]
        [TestCase("::ffff:127.0.0.1", "[::ffff:127.0.0.1]")]
        public void DefaultServerUriIpv6HostBracketsHostForValidUri(
            string host,
            string expectedHost
        )
        {
            Uri uri = SignalFishClientInfo.DefaultServerUri(host);

            Assert.That(uri.Host, Is.EqualTo(expectedHost));
            Assert.That(uri.Port, Is.EqualTo(SignalFishClientInfo.DefaultPort));
            Assert.That(uri.AbsolutePath, Is.EqualTo("/v2/ws"));
        }

        [Test]
        public void DefaultServerUriAlreadyBracketedIpv6HostIsNotDoubleBracketed()
        {
            Uri uri = SignalFishClientInfo.DefaultServerUri("[::1]");

            Assert.That(uri.Host, Is.EqualTo("[::1]"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void DefaultServerUriEmptyHostThrows(string? host)
        {
            Assert.That(
                (Action)(() => SignalFishClientInfo.DefaultServerUri(host!)),
                Throws.ArgumentException
            );
        }

        [Test]
        public void ProtocolConstantsMatchQuickReference()
        {
            Assert.That(SignalFishClientInfo.V2WebSocketPath, Is.EqualTo("/v2/ws"));
            Assert.That(SignalFishClientInfo.V3WebSocketPath, Is.EqualTo("/v3/ws"));
            Assert.That(SignalFishClientInfo.Platform, Is.EqualTo("dotnet"));
        }

        [Test]
        public void SdkVersionMatchesTheAssemblyStampSansMetadata()
        {
            /*
                M9.1: the reported version is the one stamped into the
                library assembly (MinVer from the git tag), minus the
                source-revision metadata the .NET SDK appends after '+'
                in CI builds. The dotnet build always carries the stamp;
                the Unity fallback (no stamp, or a toolchain default of
                0.0.0) returns the 0.1.0 floor inside the library —
                there is no unit-testable seam for it here, and the
                Unity lane pins its compilation, not the value.
            */
            string informational =
                typeof(SignalFishClientInfo)
                    .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                ?? SignalFishClientInfo.SdkVersion;

            Assert.That(
                SignalFishClientInfo.SdkVersion,
                Is.EqualTo(StripInformationalVersionMetadata(informational))
            );
        }

        [Test]
        public void SdkVersionIsASemverCoreVersion()
        {
            /*
                The property strips build metadata ('+'), so a
                pre-release tail is the only optional part.
            */
            Assert.That(
                SignalFishClientInfo.SdkVersion,
                Does.Match(@"^\d+\.\d+\.\d+(-[0-9A-Za-z\-.]+)?$"),
                "the version reported on Authenticate must be a semver core version"
            );
        }

        private static string StripInformationalVersionMetadata(string version)
        {
            int metadata = version.IndexOf('+', StringComparison.Ordinal);
            return metadata < 0 ? version : version.Substring(0, metadata);
        }
    }
}
