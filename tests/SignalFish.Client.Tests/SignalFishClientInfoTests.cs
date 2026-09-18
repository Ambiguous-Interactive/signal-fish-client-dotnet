using System;
using NUnit.Framework;
using SignalFish.Client;

namespace SignalFish.Client.Tests
{
    [TestFixture]
    public class SignalFishClientInfoTests
    {
        [Test]
        public void DefaultServerUri_DefaultHost_MatchesProtocolDefaults()
        {
            var uri = SignalFishClientInfo.DefaultServerUri();

            Assert.That(uri.Port, Is.EqualTo(3536));
            Assert.That(uri.AbsolutePath, Is.EqualTo("/v2/ws"));
            Assert.That(uri.Scheme, Is.EqualTo("ws"));
        }

        [Test]
        public void DefaultServerUri_CustomHost_PreservesHost()
        {
            var uri = SignalFishClientInfo.DefaultServerUri("games.example.com");

            Assert.That(uri.Host, Is.EqualTo("games.example.com"));
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
