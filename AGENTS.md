# MC Panel Agent Instructions

## Server deployment

- Deploy or start MC Panel only when the user explicitly asks for it or when the completed task changes runtime-affecting application or deployment code. Do not install, update, start, or restart the server for documentation, tests-only, agent-instruction, repository-metadata, ignore-rule, or cleanup-only changes.
- When deployment is required, deploy the current checkout with `./mcpanel.sh install --source local` when MC Panel is not installed or `./mcpanel.sh update --source local` when an installation already exists.
- If installation, update, startup, or verification fails, diagnose the failure and fix the responsible script or application code. Rebuild, redeploy, and retry until the installed service stays running and its HTTP endpoint responds.
- Always expose the installed panel to the local network by binding it to `0.0.0.0`. Select an available port without disrupting unrelated services, and report the resulting local-network URL.
- After completing a runtime-affecting code change, update the system installation from the current checkout, start the panel and persistent runtime services, and verify both systemd state and the HTTP readiness endpoint before handing the change back to the user. For non-code changes, leave the installed services untouched.
- Preserve existing panel configuration, server data, and rollback artifacts during deployment unless the user explicitly asks to remove them.
