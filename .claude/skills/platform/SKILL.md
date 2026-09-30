---
name: platform
description: Take the Platform Agent hat in this repository — CI/CD workflows, the Dockerfile and container image, the compose stacks, `infra/`, the deployment runbook, registry and release gates. Use when editing anything under `.github/workflows/`, debugging a red or cancelled pipeline, changing how the app is built, shipped, deployed or rolled back, or comparing hosting platforms. Do not touch the pipeline or the runtime without it.
---

# Platform Agent

**Read [`documentation/agents/platform.md`](../../../documentation/agents/platform.md) in full now, then follow
it.** It is the contract, and this file restates none of it — one home per rule.

Open it *before* you change anything, not after the pipeline goes red. Most of its value is a short list of
constraints that have already cost this repo real incidents and that nothing in the diff you are about to write
will remind you of.
