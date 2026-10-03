# Attributions

What this repository is built on, who made it, and how it was made.

## AI-assisted development

> **AI disclosure:** This project was developed with substantial
> assistance from AI coding agents (Codex, Gemini, GLM, and others).
> Humans created the protocol concepts and core design and retained
> oversight of architecture and code review.

## Third-party .NET packages

The shipped library has **zero NuGet dependencies**:
`src/SignalFish.Client/SignalFish.Client.csproj` declares no
`PackageReference`. JSON is a hand-rolled UTF-8 codec and queuing is a
channel-free bounded ring, so nothing needs to be carried into a player
or a NuGet consumer.

The test and benchmark projects reference dev-time-only packages that
never ship with the library:

| Use | Package |
|-----|---------|
| Unit tests | NUnit, NUnit3TestAdapter, NUnit.Analyzers, Microsoft.NET.Test.Sdk, coverlet.collector, FsCheck.NUnit |
| Fuzz tests | SharpFuzz |
| Benchmarks | BenchmarkDotNet |

## Documentation toolchain

This site is built with [MkDocs](https://www.mkdocs.org/) and the
[Material for MkDocs](https://squidfunk.github.io/mkdocs-material/)
theme. CI validates every markdown file with
[markdownlint-cli2](https://github.com/DavidAnson/markdownlint-cli2),
scans the whole repository with the
[typos](https://github.com/crate-ci/typos) spell checker, and checks
links with [lychee](https://github.com/lycheeverse/lychee)
(`.github/workflows/docs.yml`).

## Unity package

The Unity package (`com.ambiguous-interactive.signalfish`) contains no
third-party code or assets: its `Runtime/` sources mirror
`src/SignalFish.Client` exactly (enforced by CI), the WebGL plugin in
`Plugins/SignalFishWebGL/` is authored in this repository, `link.xml`
and the `PollingDriver` sample are original, and the package ships
under the repository's MIT license.

## API parity

The client tracks the Rust SDK as its API-parity source:
[signal-fish-client-rust](https://github.com/Ambiguous-Interactive/signal-fish-client-rust).
Behavior notes that call out "Rust parity" (admission ordering,
mesh-session folding, the delivery-accountability engine) port that
client's documented semantics.

## License

The repository is licensed under the
[MIT license](https://github.com/Ambiguous-Interactive/signal-fish-client-dotnet/blob/main/LICENSE).
