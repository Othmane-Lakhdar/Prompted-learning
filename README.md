# Prompted-learning

Planning and implementation workspace for a C++ and C# code-extraction tool.

Current stage: M0 design passed independent review. M1A foundation is ready to begin; implementation acceptance remains gated.

- [M0 contracts, acceptance plan and evidence](docs/m0/README.md)
- [Capability inventory and checked status](docs/capabilities.md)
- [Pinned mixed-language test resources and clone/build instructions](docs/testing/README.md)
- [Machine-readable candidate sizes and source selections](docs/testing/candidates.json)
- [Deviation and failure log](docs/deviations.json)
- [Active post-preflight checkpoint](docs/checkpoints/2026-11-17-post-preflight.md)
- [Historical preimplementation checkpoint](docs/checkpoints/2026-11-16-preimplementation.md)
- [Current project state](docs/project-state.json)
- [Repository work rules](AGENTS.md)

The work branch is codex/extractor-design-implementation, based on the completed preflight at 4f194253e7a4a01ffa2c6452d62ce2d12b781d49. Executed builds, tests and their limits are recorded in the capability and deviation files. Future extractor correctness requires its own acceptance tests.
