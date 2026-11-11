# Changelog

All notable changes to this brick will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Changed: the Android platform is its own brick instead of being built into Turian.
- Added: the Android SDK, JDK and adb are found without environment variables or set per user.
- Added: exports install on a device and start there; `turian devices` and `turian deploy`.
