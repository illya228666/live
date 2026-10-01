# Implementation plan

The repository began empty, without Git metadata. The environment was inspected before implementation; official release metadata and NuGet package manifests were checked on 2026-10-02.

1. Pin compatible tools and dependencies; initialize a small solution.
2. Implement seedable world orchestration, legal movement, type registries and bounded reward aggregation.
3. Implement temperature as a separate physical/sensory/drive module.
4. Implement masked Deep Sets, shared candidate Q scoring and managed replay using TorchSharp.
5. Train Double DQN headlessly, then compare frozen policies with random and untrained baselines.
6. Save weights together with registry identity, configuration and checksum.
7. Add neutral snapshots and a Godot C# rendering adapter.
8. Exercise core, neural contracts, checkpoint loading and short end-to-end learning tests.
9. Finish repository-local bootstrap, CI, README and technical documentation.
10. Run the documented workflow and inspect an actual Godot capture.

No runtime plugin loader, additional game mechanics, LLM integration or network service is included.
