# Platform Agent — pointer

The contract governing CI/CD, the container image, the local stack, and the deploy target is
**[`documentation/agents/platform.md`](../../documentation/agents/platform.md)**. Read it before changing
anything here, in the `Dockerfile`, in `docker-compose*.yml`, or in the deployment runbook — it is role-scoped
and owns those wherever they live.

Kept as a pointer, not a copy: the full contract would otherwise load for everyone who touches this directory.
