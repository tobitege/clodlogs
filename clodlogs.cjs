#!/usr/bin/env node

const childProcess = require("node:child_process");
const path = require("node:path");

const scriptPath = path.join(__dirname, "clodlogs-sessions.ts");
const result = childProcess.spawnSync(
  process.execPath,
  ["--no-warnings", "--experimental-strip-types", scriptPath, ...process.argv.slice(2)],
  {
    env: {
      ...process.env,
      CLODLOGS_COMMAND_NAME: "clodlogs",
    },
    stdio: "inherit",
    windowsHide: false,
  },
);

if (result.error) {
  console.error(`Failed to launch clodlogs: ${result.error.message}`);
  process.exit(1);
}

if (result.signal) {
  process.kill(process.pid, result.signal);
}

process.exit(result.status ?? 1);
