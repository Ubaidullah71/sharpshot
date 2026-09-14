# Changelog

All notable changes to Sharpshot are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project follows [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-09-14

First release.

### Added

- Region and full-screen capture with global hotkeys, on any number of monitors with different scaling.
- Uploads to your own Cloudflare R2 bucket. The link is copied right away, uploads are queued on disk and retried, and local copies are optional.
- A lossless PNG encoder that produces smaller files than Windows' built-in one.
- Link styles: plain, braille, blocks, emoji and invisible.
- Embeds, so links show as a card in Discord and other apps with your colour, site name, title and description.
- An Uploads page that shows storage use and deletes screenshots from R2.
- A connection test, a Settings window in light and dark mode, and a tray menu.
- x64 and ARM64 builds, with SHA-256 checksums.

[1.0.0]: https://github.com/Ubaidullah71/sharpshot/releases/tag/v1.0.0