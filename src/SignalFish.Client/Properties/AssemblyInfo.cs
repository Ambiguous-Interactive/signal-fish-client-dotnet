using System.Runtime.CompilerServices;

// Test-suite visibility: payload struct decode stays internal until the M3
// state machine freezes the public decode surface.
[assembly: InternalsVisibleTo("SignalFish.Client.Tests")]
[assembly: InternalsVisibleTo("SignalFish.Client.FuzzTests")]
