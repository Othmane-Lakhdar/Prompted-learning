# Prompted-learning

Planning and implementation workspace for a C++ and C# code-extraction tool.

Current stage: preflight completed with declared scope limits; M0 design contracts are next. Extractor implementation has not started.

- [Capability inventory and checked status](docs/capabilities.md)
- [Pinned mixed-language test resources and clone/build instructions](docs/testing/README.md)
- [Machine-readable candidate sizes and source selections](docs/testing/candidates.json)
- [Deviation and failure log](docs/deviations.json)
- [Active post-preflight checkpoint](docs/checkpoints/2026-11-17-post-preflight.md)
- [Historical preimplementation checkpoint](docs/checkpoints/2026-11-16-preimplementation.md)
- [Current project state](docs/project-state.json)
- [Repository work rules](AGENTS.md)

The work branch is codex/extractor-design-implementation, based on the completed preflight at 4f194253e7a4a01ffa2c6452d62ce2d12b781d49. Executed builds, tests and their limits are recorded in the capability and deviation files. Future extractor correctness requires its own acceptance tests.
