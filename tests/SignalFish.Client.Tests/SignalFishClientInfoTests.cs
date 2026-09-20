namespace SignalFish.Client.Tests
{
    using System;
    using NUnit.Framework;
    using SignalFish.Client;

    [TestFixture]
    public class SignalFishClientInfoTests
    {
        [Test]
        public void DefaultServerUri_DefaultHost_MatchesProtocolDefaults()
        {
            Uri uri = SignalFishClientInfo.DefaultServerUri();

            Assert.That(uri.Port, Is.EqualTo(3536));
            Assert.That(uri.AbsolutePath, Is.EqualTo("/v2/ws"));
            Assert.That(uri.Scheme, Is.EqualTo("ws"));
        }

        [Test]
        public void DefaultServerUri_CustomHost_PreservesHost()
        {
            Uri uri = SignalFishClientInfo.DefaultServerUri("games.example.com");

            Assert.That(uri.Host, Is.EqualTo("games.example.com"));
        }

        [TestCase("::1", "[::1]")]
        [TestCase("2001:db8::1", "[2001:db8::1]")]
        [TestCase("::ffff:127.0.0.1", "[::ffff:127.0.0.1]")]
        public void DefaultServerUri_Ipv6Host_BracketsHostForValidUri(string host, string expectedHost)
        {
            Uri uri = SignalFishClientInfo.DefaultServerUri(host);

            Assert.That(uri.Host, Is.EqualTo(expectedHost));
            Assert.That(uri.Port, Is.EqualTo(SignalFishClientInfo.DefaultPort));
            Assert.That(uri.AbsolutePath, Is.EqualTo("/v2/ws"));
        }

        [Test]
        public void DefaultServerUri_AlreadyBracketedIpv6Host_IsNotDoubleBracketed()
        {
            Uri uri = SignalFishClientInfo.DefaultServerUri("[::1]");

            Assert.That(uri.Host, Is.EqualTo("[::1]"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void DefaultServerUri_EmptyHost_Throws(string? host)
        {
            Assert.That(() => SignalFishClientInfo.DefaultServerUri(host!), Throws.ArgumentException);
        }

        [Test]
        public void ProtocolConstants_MatchQuickReference()
        {
            Assert.That(SignalFishClientInfo.V2WebSocketPath, Is.EqualTo("/v2/ws"));
            Assert.That(SignalFishClientInfo.V3WebSocketPath, Is.EqualTo("/v3/ws"));
            Assert.That(SignalFishClientInfo.Platform, Is.EqualTo("dotnet"));
        }
    }
}
