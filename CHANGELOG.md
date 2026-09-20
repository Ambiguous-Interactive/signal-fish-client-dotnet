# Changelog

All notable, user-visible changes to the `SignalFish.Client` package are
documented here. Format: [keep-a-changelog]; versions follow [semver]. Internal
changes (CI, tests, tooling, docs) are not listed.

## [Unreleased]

### Added

- Protocol envelope decoding. Total: any input yields a typed event, never an
  exception. Unknown message types and unknown fields surface as
  forward-compatible events. Zero allocations on known messages.
- Protocol envelope encoding for every outbound v2/v3 client message.
  Byte-identical to the server's own wire samples.

[keep-a-changelog]: https://keepachangelog.com/en/1.1.0/
[semver]: https://semver.org/spec/v2.0.0.html
