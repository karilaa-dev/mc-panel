# MC Panel Agent Instructions

## Server deployment

- When the user asks to start the dev server or server, deploy the current checkout to the system installation with `./mcpanel.sh`. Use `install` when MC Panel is not installed and `update` when an installation already exists.
- If installation, update, startup, or verification fails, diagnose the failure and fix the responsible script or application code. Rebuild, redeploy, and retry until the installed service stays running and its HTTP endpoint responds.
- Always expose the installed panel to the local network by binding it to `0.0.0.0`. Select an available port without disrupting unrelated services, and report the resulting local-network URL.
- After completing any feature, update the system installation from the current checkout, start the panel and persistent runtime services, and verify both systemd state and the HTTP health endpoint before handing the feature back to the user.
- Preserve existing panel configuration, server data, and rollback artifacts during deployment unless the user explicitly asks to remove them.
